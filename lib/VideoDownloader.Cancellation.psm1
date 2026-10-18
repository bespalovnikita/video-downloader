#requires -Version 7.0
# Per-URL cancellation markers and strictly scoped output cleanup.
$ErrorActionPreference = 'Stop'

function Get-VdCancelMarkerPath {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Url)
    $bytes=[Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Url))
    return (Join-Path $Directory ([Convert]::ToHexString($bytes).ToLowerInvariant()+'.cancel'))
}

function Request-VdCancellation {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Url)
    [void][IO.Directory]::CreateDirectory($Directory)
    $target=Get-VdCancelMarkerPath -Directory $Directory -Url $Url
    $tmp=$target+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($tmp,$Url,[Text.UTF8Encoding]::new($false))
        [IO.File]::Move($tmp,$target,$true)
    } finally { if ([IO.File]::Exists($tmp)) { [IO.File]::Delete($tmp) } }
}

function Test-VdCancellation {
    param([string]$Directory,[string]$Url)
    if (-not $Directory -or -not $Url) { return $false }
    return [IO.File]::Exists((Get-VdCancelMarkerPath -Directory $Directory -Url $Url))
}

function Test-VdPathInsideRoot {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Root)
    $rootPath=[IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $pathValue=[IO.Path]::GetFullPath($Path)
    $compare=if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    return $pathValue.StartsWith($rootPath,$compare)
}

function Remove-VdOwnedFiles {
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Paths
    )
    $removed=[Collections.Generic.List[string]]::new()
    foreach ($path in $Paths) {
        if (-not $path) { continue }
        if (-not (Test-VdPathInsideRoot -Path $path -Root $OutputRoot)) { throw "Refusing to delete file outside output folder: $path" }
        if (-not [IO.File]::Exists($path)) { continue }
        $item=Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing to delete symlink: $path" }
        [IO.File]::Delete($path)
        [void]$removed.Add($path)
    }
    return ,$removed.ToArray()
}

function Publish-VdStagedFiles {
    param(
        [Parameter(Mandatory)][string]$StageDir,
        [Parameter(Mandatory)][string]$OutputRoot,
        [string]$MediaPath
    )
    if (-not (Test-Path -LiteralPath $StageDir -PathType Container)) {
        throw "Missing staged download folder: $StageDir"
    }
    [void][IO.Directory]::CreateDirectory($OutputRoot)
    $owned=[Collections.Generic.List[string]]::new()
    $publishedMedia=''
    try {
        $files=@(Get-ChildItem -LiteralPath $StageDir -File -Recurse -Force)
        foreach ($f in $files) {
            if (($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Staged download contains symlink: $($f.FullName)" }
            if (-not (Test-VdPathInsideRoot -Path $f.FullName -Root $StageDir)) { throw 'Staged file escapes work folder.' }
            $relative=[IO.Path]::GetRelativePath([IO.Path]::GetFullPath($StageDir),$f.FullName)
            $dest=[IO.Path]::GetFullPath((Join-Path $OutputRoot $relative))
            if (-not (Test-VdPathInsideRoot -Path $dest -Root $OutputRoot)) { throw 'Output path escapes download folder.' }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest))
            if (-not [IO.File]::Exists($dest)) {
                [IO.File]::Move($f.FullName,$dest)
                [void]$owned.Add($dest)
            } else {
                # Never overwrite a file that predates this particular download.
                [IO.File]::Delete($f.FullName)
            }
            if ($MediaPath -and [string]::Equals([IO.Path]::GetFullPath($f.FullName),[IO.Path]::GetFullPath($MediaPath),
                    $(if ($IsWindows) {[StringComparison]::OrdinalIgnoreCase} else {[StringComparison]::Ordinal}))) {
                $publishedMedia=$dest
            }
        }
        return [pscustomobject]@{ Path=$publishedMedia; OwnedPaths=@($owned.ToArray()) }
    } catch {
        # Roll back only files created by this publish, not pre-existing outputs.
        [void](Remove-VdOwnedFiles -OutputRoot $OutputRoot -Paths @($owned.ToArray()))
        throw
    }
}
Export-ModuleMember -Function Get-VdCancelMarkerPath,Request-VdCancellation,Test-VdCancellation,Test-VdPathInsideRoot,Remove-VdOwnedFiles,Publish-VdStagedFiles
