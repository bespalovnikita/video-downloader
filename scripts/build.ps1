#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Build','Test','Publish')][string]$Task = 'Build',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$Version = '',
    [string]$OutputDirectory = 'dist/native',
    [string]$ArchivePath = 'dist/VideoDownloader-native.zip',
    [string]$ChecksumPath = 'dist/SHA256SUMS.txt'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'native/VideoDownloader.Native.csproj'
$testProject = Join-Path $root 'native-tests/VideoDownloader.NativeTests.csproj'

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}
function Resolve-ProjectPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $root $Path))
}

if (-not $IsWindows) { throw 'The WPF application must be built and tested on Windows with the .NET 8 SDK.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 8 SDK before building.' }
Push-Location $root
try {
    switch ($Task) {
        'Build' {
            Invoke-DotNet -Arguments @('build', $project, '-c', $Configuration)
        }
        'Test' {
            & (Join-Path $PSScriptRoot 'release-version-tests.ps1')
            & (Join-Path $PSScriptRoot 'smoke.ps1')
            Invoke-DotNet -Arguments @('build', $project, '-c', $Configuration)
            & (Join-Path $PSScriptRoot 'russian-ui-tests.ps1') -Configuration $Configuration
            Invoke-DotNet -Arguments @('run', '--project', $testProject, '-c', $Configuration)
            & (Join-Path $PSScriptRoot 'gui-startup.ps1') -Configuration $Configuration
            & (Join-Path $PSScriptRoot 'ui-tests.ps1') -Configuration $Configuration
        }
        'Publish' {
            if ($Version.Length -eq 0) {
                [xml]$definition = Get-Content -LiteralPath $project -Raw
                $Version = [string]$definition.Project.PropertyGroup.Version
            }
            if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid stable application version: $Version" }
            $output = Resolve-ProjectPath $OutputDirectory
            $dist = [IO.Path]::GetFullPath((Join-Path $root 'dist'))
            $relative = [IO.Path]::GetRelativePath($dist, $output)
            if ($relative -eq '.' -or $relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar) -or [IO.Path]::IsPathRooted($relative)) {
                throw 'OutputDirectory must be a child folder of this checkout''s dist directory.'
            }
            if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
            Invoke-DotNet -Arguments @('publish', $project, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
                "-p:Version=$Version", '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-o', $output)
            & (Join-Path $PSScriptRoot 'verify-package.ps1') -Directory $output -Version $Version
            $archive = Resolve-ProjectPath $ArchivePath
            $checksums = Resolve-ProjectPath $ChecksumPath
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $archive))
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $checksums))
            Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
            $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath $checksums -Encoding utf8
            Write-Host "Published Video Downloader $Version`: $archive"
        }
    }
}
finally { Pop-Location }
