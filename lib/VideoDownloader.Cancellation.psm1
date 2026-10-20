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

function Assert-VdNoReparseAncestors {
    param([Parameter(Mandatory)][string]$Path)
    # Check every directory from the target's parent up to the volume root.
    # A lexical path under the output directory may in fact escape through a
    # parent junction, symbolic link, volume mount point or symlinked root.
    $directory=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    while ($directory) {
        try {
            $attributes=[IO.File]::GetAttributes($directory)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to access path through a symlink, junction or reparse point: $directory"
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
                throw "Parent path is not a directory: $directory"
            }
        } catch [IO.FileNotFoundException] {
            # An as-yet uncreated subdirectory is permitted for publishing.
        } catch [IO.DirectoryNotFoundException] {
            # Its ancestors will still be checked.
        }
        $parent=[IO.Path]::GetDirectoryName($directory.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar))
        if (-not $parent -or $parent -eq $directory) { break }
        $directory=$parent
    }
}

function Assert-VdRegularFileIfPresent {
    param([Parameter(Mandatory)][string]$Path)
    Assert-VdNoReparseAncestors -Path $Path
    try {
        $attributes=[IO.File]::GetAttributes([IO.Path]::GetFullPath($Path))
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing symlink/reparse-point file: $Path"
        }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            throw "Refusing to treat a directory as a file: $Path"
        }
    } catch [IO.FileNotFoundException] {
    } catch [IO.DirectoryNotFoundException] {
    }
}

function Remove-VdOwnedFiles {
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Paths
    )
    $removed=[Collections.Generic.List[string]]::new()
    foreach ($path in $Paths) {
        if (-not $path) { continue }
        if (-not (Test-VdPathInsideRoot -Path $path -Root $OutputRoot)) {
            throw "Refusing to delete file outside output folder: $path"
        }
        # No delete if *any* ancestor, including the output root itself, is
        # a symlink/junction/reparse point. Recheck just before deletion.
        Assert-VdRegularFileIfPresent -Path $path
        if (-not [IO.File]::Exists($path)) { continue }
        Assert-VdRegularFileIfPresent -Path $path
        [IO.File]::Delete($path)
        [void]$removed.Add($path)
    }
    return ,$removed.ToArray()
}

function Remove-VdStagingDirectory {
    param(
        [Parameter(Mandatory)][string]$StageDir,
        [Parameter(Mandatory)][string]$RunDir
    )
    $stagePath=[IO.Path]::GetFullPath($StageDir).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    $runPath=[IO.Path]::GetFullPath($RunDir).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    $compare=if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($stagePath),$runPath,$compare) -or
        [IO.Path]::GetFileName($stagePath) -notmatch '^download-\d+-[0-9a-f]{32}$') {
        throw "Refusing to remove a directory that is not a job staging folder: $StageDir"
    }
    Assert-VdNoReparseAncestors -Path (Join-Path $stagePath '__check__')
    if (-not (Test-Path -LiteralPath $stagePath)) { return }
    if (-not (Test-Path -LiteralPath $stagePath -PathType Container)) { throw "Staging path is not a directory: $StageDir" }
    # Do not recurse through, or silently discard, links placed inside staging.
    foreach ($entry in @(Get-ChildItem -LiteralPath $stagePath -Recurse -Force -ErrorAction Stop)) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to remove staging containing a symlink, junction or reparse point: $($entry.FullName)"
        }
    }
    Assert-VdNoReparseAncestors -Path (Join-Path $stagePath '__check__')
    [IO.Directory]::Delete($stagePath,$true)
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
    Assert-VdNoReparseAncestors -Path (Join-Path $StageDir '__check__')
    Assert-VdNoReparseAncestors -Path (Join-Path $OutputRoot '__check__')
    [void][IO.Directory]::CreateDirectory($OutputRoot)
    Assert-VdNoReparseAncestors -Path (Join-Path $OutputRoot '__check__')
    $owned=[Collections.Generic.List[string]]::new()
    $publishedMedia=''
    try {
        # A junction directory may be skipped by -File -Recurse, so reject
        # reparse directories explicitly before enumerating media files.
        foreach ($subdir in @(Get-ChildItem -LiteralPath $StageDir -Directory -Recurse -Force)) {
            if (($subdir.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Staging contains a symlink or junction directory: $($subdir.FullName)"
            }
        }
        $files=@(Get-ChildItem -LiteralPath $StageDir -File -Recurse -Force)
        $planned=[Collections.Generic.List[object]]::new()
        # Preflight the *entire* batch before moving anything, including
        # checking parent junctions and collisions with previously existing files.
        foreach ($f in $files) {
            Assert-VdRegularFileIfPresent -Path $f.FullName
            if (-not (Test-VdPathInsideRoot -Path $f.FullName -Root $StageDir)) {
                throw 'Staged file escapes work folder.'
            }
            $relative=[IO.Path]::GetRelativePath([IO.Path]::GetFullPath($StageDir),$f.FullName)
            $dest=[IO.Path]::GetFullPath((Join-Path $OutputRoot $relative))
            if (-not (Test-VdPathInsideRoot -Path $dest -Root $OutputRoot)) {
                throw 'Output path escapes download folder.'
            }
            Assert-VdRegularFileIfPresent -Path $dest
            if ([IO.File]::Exists($dest)) {
                # Earlier code claimed a colliding, pre-existing file as the
                # media path. A later Remove could delete that unrelated file.
                # Reject the publish rather than ever claim foreign ownership.
                throw "Output file already exists; will not overwrite or claim it: $dest"
            }
            [void]$planned.Add([pscustomobject]@{Source=$f.FullName; Destination=$dest})
        }
        foreach ($entry in $planned) {
            Assert-VdRegularFileIfPresent -Path $entry.Source
            Assert-VdRegularFileIfPresent -Path $entry.Destination
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($entry.Destination))
            Assert-VdRegularFileIfPresent -Path $entry.Destination
            # File.Move without overwrite is atomic within one filesystem and
            # refuses collisions created after preflight.
            [IO.File]::Move($entry.Source,$entry.Destination)
            [void]$owned.Add($entry.Destination)
            if ($MediaPath -and [string]::Equals([IO.Path]::GetFullPath($entry.Source),[IO.Path]::GetFullPath($MediaPath),
                    $(if ($IsWindows) {[StringComparison]::OrdinalIgnoreCase} else {[StringComparison]::Ordinal}))) {
                $publishedMedia=$entry.Destination
            }
        }
        if ($MediaPath -and -not $publishedMedia) {
            throw "Cannot locate staged media in published files: $MediaPath"
        }
        return [pscustomobject]@{ Path=$publishedMedia; OwnedPaths=@($owned.ToArray()) }
    } catch {
        # Roll back only new files created by this publish. Never touch old files.
        $publishError=$_.Exception
        try { [void](Remove-VdOwnedFiles -OutputRoot $OutputRoot -Paths @($owned.ToArray())) }
        catch {
            $failure=[IO.IOException]::new(($publishError.Message + ' Rollback failed: ' + $_.Exception.Message),$publishError)
            # The caller must retain ownership even when rollback itself fails.
            $failure.Data['OwnedPaths']=$owned.ToArray()
            throw $failure
        }
        throw $publishError
    }
}

Export-ModuleMember -Function Get-VdCancelMarkerPath,Request-VdCancellation,Test-VdCancellation,Test-VdPathInsideRoot,Assert-VdNoReparseAncestors,Assert-VdRegularFileIfPresent,Remove-VdOwnedFiles,Remove-VdStagingDirectory,Publish-VdStagedFiles
