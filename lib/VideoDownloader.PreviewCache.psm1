Set-StrictMode -Version Latest

function Get-VdPreviewCachePath {
    param(
        [Parameter(Mandatory=$true)][string]$CacheDir,
        [Parameter(Mandatory=$true)][string]$Url
    )

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Url)
        $hash = $sha.ComputeHash($bytes)
        $key = [Convert]::ToHexString($hash).ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }

    return Join-Path $CacheDir ($key + ".json")
}

function Read-VdPreviewCache {
    param(
        [Parameter(Mandatory=$true)][string]$CacheDir,
        [Parameter(Mandatory=$true)][string]$Url,
        [ValidateRange(1,8760)][int]$TtlHours = 72
    )

    $path = Get-VdPreviewCachePath -CacheDir $CacheDir -Url $Url
    if (-not (Test-Path $path -PathType Leaf)) { return $null }

    try {
        $record = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
        if ([int]$record.CacheVersion -ne 1) {
            Remove-Item $path -Force -ErrorAction SilentlyContinue
            return $null
        }
        if (-not [string]::Equals([string]$record.Url,$Url,[StringComparison]::Ordinal)) {
            Remove-Item $path -Force -ErrorAction SilentlyContinue
            return $null
        }

        $savedAt = [DateTime]::Parse(
            [string]$record.SavedAtUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind
        ).ToUniversalTime()

        if (([DateTime]::UtcNow - $savedAt).TotalHours -ge $TtlHours) {
            Remove-Item $path -Force -ErrorAction SilentlyContinue
            return $null
        }

        return $record
    } catch {
        Remove-Item $path -Force -ErrorAction SilentlyContinue
        return $null
    }
}

function Write-VdPreviewCache {
    param(
        [Parameter(Mandatory=$true)][string]$CacheDir,
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][object]$Preview
    )

    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
    $path = Get-VdPreviewCachePath -CacheDir $CacheDir -Url $Url
    $tempPath = $path + ".tmp-" + [guid]::NewGuid().ToString("N")

    $record = [ordered]@{
        CacheVersion = 1
        SavedAtUtc = [DateTime]::UtcNow.ToString("o",[Globalization.CultureInfo]::InvariantCulture)
        Url = $Url
        Success = $true
        Error = ""
        Title = [string]$Preview.Title
        PlaylistTitle = [string]$Preview.PlaylistTitle
        Uploader = [string]$Preview.Uploader
        Duration = [double]$Preview.Duration
        ThumbnailBase64 = [string]$Preview.ThumbnailBase64
        ThumbnailUrl = [string]$Preview.ThumbnailUrl
        ThumbnailError = [string]$Preview.ThumbnailError
        Extractor = [string]$Preview.Extractor
        MaxHeight = [int]$Preview.MaxHeight
        MaxFps = [double]$Preview.MaxFps
        Codecs = [string]$Preview.Codecs
        DynamicRange = [string]$Preview.DynamicRange
    }

    try {
        $json = $record | ConvertTo-Json -Depth 4
        Set-Content $tempPath $json -Encoding UTF8
        Move-Item $tempPath $path -Force
        return [pscustomobject]$record
    } finally {
        Remove-Item $tempPath -Force -ErrorAction SilentlyContinue
    }
}

function Remove-VdExpiredPreviewCache {
    param(
        [Parameter(Mandatory=$true)][string]$CacheDir,
        [ValidateRange(1,8760)][int]$TtlHours = 72
    )

    if (-not (Test-Path $CacheDir -PathType Container)) { return }

    $cutoff = [DateTime]::UtcNow.AddHours(-$TtlHours)
    foreach ($file in @(Get-ChildItem $CacheDir -File -Filter "*.json" -ErrorAction SilentlyContinue)) {
        if ($file.LastWriteTimeUtc -lt $cutoff) {
            Remove-Item $file.FullName -Force -ErrorAction SilentlyContinue
        }
    }
}

Export-ModuleMember -Function Get-VdPreviewCachePath,Read-VdPreviewCache,Write-VdPreviewCache,Remove-VdExpiredPreviewCache
