#requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KeyStorePath,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'bespalovnikita/video-downloader'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-SetupTool {
    param([string]$File, [string[]]$ToolArguments, [string]$InputText,
        [hashtable]$ChildEnvironment = @{})
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $File
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $ToolArguments) { $info.ArgumentList.Add($argument) }
    foreach ($name in $ChildEnvironment.Keys) { $info.Environment[$name] = $ChildEnvironment[$name] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($null -ne $InputText) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $stdout.GetAwaiter().GetResult()
            ErrorOutput = $stderr.GetAwaiter().GetResult()
        }
    } finally { $process.Dispose() }
}

function Set-PrivateSigningPath([string]$Path, [bool]$Directory = $false) {
    if ($IsWindows) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = if ($Directory) { [Security.AccessControl.DirectorySecurity]::new() }
               else { [Security.AccessControl.FileSecurity]::new() }
        $acl.SetAccessRuleProtection($true, $false)
        $inheritance = if ($Directory) { [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' }
                       else { [Security.AccessControl.InheritanceFlags]::None }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $identity, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance,
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $Path -AclObject $acl
    } else {
        $mode = if ($Directory) { 448 } else { 384 } # 0700 / 0600
        [IO.File]::SetUnixFileMode($Path, [IO.UnixFileMode]$mode)
    }
}

if (-not [IO.Path]::IsPathFullyQualified($KeyStorePath)) {
    throw 'KeyStorePath must be an absolute file path outside every Git checkout.'
}
# Resolve existing directory links before checking Git ancestors, including when
# this script was downloaded by itself rather than run from a repository clone.
$fullPath = [IO.Path]::GetFullPath($KeyStorePath)
$storageRoot = [IO.Path]::GetPathRoot($fullPath)
$resolved = $storageRoot
foreach ($part in $fullPath.Substring($storageRoot.Length).Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)) {
    $resolved = Join-Path $resolved $part
    if (Test-Path -LiteralPath $resolved) {
        $item = Get-Item -LiteralPath $resolved -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            $resolved = $item.ResolveLinkTarget($true).FullName
        }
    }
}
$KeyStorePath = $resolved
$parent = Split-Path $KeyStorePath -Parent
$cursor = $parent
while ($cursor) {
    $gitPath = Join-Path $cursor '.git'
    if ((Test-Path -LiteralPath $gitPath -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $gitPath 'HEAD') -PathType Leaf)) {
        throw 'Keep the signing key and recovery file outside Git checkouts.'
    }
    $next = Split-Path $cursor -Parent
    if ($next -eq $cursor) { break }
    $cursor = $next
}
if (Test-Path -LiteralPath $KeyStorePath -PathType Container) { throw 'KeyStorePath must name a file.' }
$recoveryPath = "$KeyStorePath.recovery.json"
$hasKey = Test-Path -LiteralPath $KeyStorePath -PathType Leaf
$hasRecovery = Test-Path -LiteralPath $recoveryPath -PathType Leaf
if ($hasKey -ne $hasRecovery) {
    throw 'An incomplete key/recovery pair exists. Preserve it and restore the matching file; no key will be replaced.'
}

$keytool = (Get-Command keytool -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$gh = (Get-Command gh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$auth = Invoke-SetupTool $gh @('auth', 'status', '--hostname', 'github.com')
if ($auth.ExitCode -ne 0) { throw 'Authenticate GitHub CLI with gh auth login, then rerun this command.' }
$remote = Invoke-SetupTool $gh @('secret', 'list', '--repo', "github.com/$Repository", '--json', 'name')
if ($remote.ExitCode -ne 0) { throw "GitHub CLI cannot manage repository Secrets for $Repository. Check its repository access and rerun." }
$secretNames = @('ANDROID_KEYSTORE_BASE64', 'ANDROID_KEYSTORE_PASSWORD', 'ANDROID_KEY_ALIAS', 'ANDROID_KEY_PASSWORD')
$existingNames = @($remote.Output | ConvertFrom-Json | ForEach-Object { $_.name })
if (-not $hasKey -and @($existingNames | Where-Object { $_ -in $secretNames }).Count -gt 0) {
    throw 'Android signing Secrets already exist. Restore their original key/recovery pair; this script will not replace them with a new key.'
}

if ($hasKey) {
    Set-PrivateSigningPath $KeyStorePath
    Set-PrivateSigningPath $recoveryPath
    $record = Get-Content -LiteralPath $recoveryPath -Raw | ConvertFrom-Json
    if ($record.schemaVersion -ne 1 -or $record.repository -cne $Repository -or
        $record.keyAlias -cne 'video-downloader' -or $record.storeType -cne 'PKCS12' -or
        [string]::IsNullOrWhiteSpace($record.storePassword) -or
        [string]::IsNullOrWhiteSpace($record.keyPassword) -or
        $record.storePassword -cne $record.keyPassword -or
        $record.keyStoreSha256 -cne (Get-FileHash -LiteralPath $KeyStorePath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'The recovery file does not match this repository and keystore. Existing files were preserved.'
    }
} else {
    [void][IO.Directory]::CreateDirectory($parent)
    $scratch = Join-Path $parent ".video-downloader-signing-$([guid]::NewGuid().ToString('N'))"
    [void][IO.Directory]::CreateDirectory($scratch)
    Set-PrivateSigningPath $scratch $true
    try {
        $bytes = [byte[]]::new(32)
        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $random.GetBytes($bytes) } finally { $random.Dispose() }
        $password = [Convert]::ToBase64String($bytes)
        $temporaryKey = Join-Path $scratch 'signing.p12'
        $generated = Invoke-SetupTool $keytool @('-genkeypair', '-noprompt', '-keystore', $temporaryKey,
            '-storetype', 'PKCS12', '-alias', 'video-downloader', '-storepass:env', 'VD_ANDROID_SETUP_PASSWORD',
            '-keypass:env', 'VD_ANDROID_SETUP_PASSWORD', '-keyalg', 'RSA', '-keysize', '4096',
            '-validity', '10000', '-dname', 'CN=Video Downloader') -ChildEnvironment @{ VD_ANDROID_SETUP_PASSWORD = $password }
        if ($generated.ExitCode -ne 0) { throw 'keytool could not create the signing key. Install JDK 17 or newer and retry.' }
        Set-PrivateSigningPath $temporaryKey
        $record = [ordered]@{
            schemaVersion = 1; repository = $Repository; keyAlias = 'video-downloader'; storeType = 'PKCS12'
            storePassword = $password; keyPassword = $password
            keyStoreSha256 = (Get-FileHash -LiteralPath $temporaryKey -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $temporaryRecovery = Join-Path $scratch 'recovery.json'
        [IO.File]::WriteAllText($temporaryRecovery, ($record | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Set-PrivateSigningPath $temporaryRecovery
        # File.Move without overwrite preserves any file created concurrently.
        [IO.File]::Move($temporaryRecovery, $recoveryPath)
        [IO.File]::Move($temporaryKey, $KeyStorePath)
    } finally {
        if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    }
    Write-Host 'Created a permanent signing key and private recovery file.'
}

$verified = Invoke-SetupTool $keytool @('-J-Duser.language=en', '-J-Duser.country=US', '-list', '-v',
    '-keystore', $KeyStorePath, '-storetype', 'PKCS12', '-alias', $record.keyAlias,
    '-storepass:env', 'VD_ANDROID_SETUP_PASSWORD') -ChildEnvironment @{ VD_ANDROID_SETUP_PASSWORD = $record.storePassword }
if ($verified.ExitCode -ne 0 -or $verified.Output -notmatch 'PrivateKeyEntry') {
    throw 'Cannot verify the saved private key. Existing key and recovery files were preserved.'
}
$values = @{
    ANDROID_KEYSTORE_BASE64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($KeyStorePath))
    ANDROID_KEYSTORE_PASSWORD = $record.storePassword
    ANDROID_KEY_ALIAS = $record.keyAlias
    ANDROID_KEY_PASSWORD = $record.keyPassword
}
foreach ($name in $secretNames) {
    $uploaded = Invoke-SetupTool $gh @('secret', 'set', $name, '--repo', "github.com/$Repository") -InputText $values[$name]
    if ($uploaded.ExitCode -ne 0) {
        throw "Could not set $name. Keep both local files and rerun the same command to retry with the same key."
    }
    Write-Host "Configured $name."
}
Write-Host "Android signing Secrets are configured for $Repository."
Write-Host "Keep private backups of $KeyStorePath and $recoveryPath. Neither file belongs in Git or chat."
