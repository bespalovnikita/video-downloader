Set-StrictMode -Version Latest

function Get-VdPropertyValue {
    param(
        [AllowNull()][object]$InputObject,
        [Parameter(Mandatory=$true)][string]$Name,
        [AllowNull()][object]$Default = $null
    )

    if ($null -eq $InputObject) { return $Default }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    if ($null -eq $property.Value) { return $Default }
    return $property.Value
}

function Get-VdFormatSummary {
    param(
        [AllowNull()][object]$Source
    )

    $sourceFormats = @(Get-VdPropertyValue -InputObject $Source -Name "formats" -Default @())
    $formats = @(
        $sourceFormats | Where-Object {
            $vc = [string](Get-VdPropertyValue -InputObject $_ -Name "vcodec" -Default "")
            $vc -and $vc -ne "none"
        }
    )

    $maxHeight = 0
    $maxFps = 0.0
    $codecSet = [Collections.Generic.HashSet[string]]::new()
    $rangeSet = [Collections.Generic.HashSet[string]]::new()

    foreach ($fmt in $formats) {
        $height = Get-VdPropertyValue -InputObject $fmt -Name "height" -Default 0
        if ($height -and [int]$height -gt $maxHeight) { $maxHeight = [int]$height }

        $fps = Get-VdPropertyValue -InputObject $fmt -Name "fps" -Default 0
        if ($fps -and [double]$fps -gt $maxFps) { $maxFps = [double]$fps }

        $vc = [string](Get-VdPropertyValue -InputObject $fmt -Name "vcodec" -Default "")
        if ($vc.StartsWith("av01")) { [void]$codecSet.Add("AV1") }
        elseif ($vc.StartsWith("vp9")) { [void]$codecSet.Add("VP9") }
        elseif ($vc.StartsWith("avc1") -or $vc.StartsWith("h264")) { [void]$codecSet.Add("H264") }
        elseif ($vc) { [void]$codecSet.Add($vc.Split('.')[0]) }

        $dr = [string](Get-VdPropertyValue -InputObject $fmt -Name "dynamic_range" -Default "")
        if ($dr -and $dr -ne "SDR" -and $dr -ne "None") { [void]$rangeSet.Add($dr) }
    }

    return [pscustomobject]@{
        MaxHeight = $maxHeight
        MaxFps = $maxFps
        Codecs = (@($codecSet) -join ", ")
        DynamicRange = (@($rangeSet) -join ", ")
    }
}
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
        Title = [string](Get-VdPropertyValue -InputObject $Preview -Name "Title" -Default "")
        PlaylistTitle = [string](Get-VdPropertyValue -InputObject $Preview -Name "PlaylistTitle" -Default "")
        Uploader = [string](Get-VdPropertyValue -InputObject $Preview -Name "Uploader" -Default "")
        Duration = [double](Get-VdPropertyValue -InputObject $Preview -Name "Duration" -Default 0)
        ThumbnailBase64 = [string](Get-VdPropertyValue -InputObject $Preview -Name "ThumbnailBase64" -Default "")
        ThumbnailUrl = [string](Get-VdPropertyValue -InputObject $Preview -Name "ThumbnailUrl" -Default "")
        ThumbnailError = [string](Get-VdPropertyValue -InputObject $Preview -Name "ThumbnailError" -Default "")
        Extractor = [string](Get-VdPropertyValue -InputObject $Preview -Name "Extractor" -Default "")
        MaxHeight = [int](Get-VdPropertyValue -InputObject $Preview -Name "MaxHeight" -Default 0)
        MaxFps = [double](Get-VdPropertyValue -InputObject $Preview -Name "MaxFps" -Default 0)
        Codecs = [string](Get-VdPropertyValue -InputObject $Preview -Name "Codecs" -Default "")
        DynamicRange = [string](Get-VdPropertyValue -InputObject $Preview -Name "DynamicRange" -Default "")
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


function Get-VdPreviewData {
    param(
        [Parameter(Mandatory=$true)][string]$YtDlpPath,
        [Parameter(Mandatory=$true)][string]$Url,
        [AllowEmptyString()][string]$CookiePath = ""
    )

    $stderrFile = Join-Path ([IO.Path]::GetTempPath()) ("video-downloader-preview-" + [guid]::NewGuid().ToString("N") + ".log")
    try {
        $args = @(
            "--dump-single-json",
            "--skip-download",
            "--no-warnings",
            "--playlist-items","1",
            "--encoding","utf-8",
            "--impersonate","chrome"
        )
        if ($CookiePath -and (Test-Path $CookiePath)) { $args += @("--cookies",$CookiePath) }
        $args += $Url

        $raw = & $YtDlpPath @args 2>$stderrFile
        $exitCode = $LASTEXITCODE
        $stderr = ""
        if (Test-Path $stderrFile) {
            $stderr = (@(Get-Content $stderrFile -Encoding UTF8 -ErrorAction SilentlyContinue) | Select-Object -Last 12) -join " | "
        }

        if ($exitCode -ne 0) {
            if ([string]::IsNullOrWhiteSpace($stderr)) { $stderr = "yt-dlp exit code $exitCode" }
            return [pscustomobject]@{
                Success = $false
                Error = $stderr
            }
        }

        $jsonText = ($raw -join [Environment]::NewLine)
        if ([string]::IsNullOrWhiteSpace($jsonText)) {
            return [pscustomobject]@{
                Success = $false
                Error = "yt-dlp returned no metadata"
            }
        }

        $j = $jsonText | ConvertFrom-Json -ErrorAction Stop
        $source = $j
        $playlistTitle = ""

        $rootFormats = Get-VdPropertyValue -InputObject $j -Name "formats" -Default $null
        $rootEntries = Get-VdPropertyValue -InputObject $j -Name "entries" -Default $null
        if ((-not $rootFormats) -and $rootEntries) {
            $first = @($rootEntries | Where-Object { $_ } | Select-Object -First 1)
            if ($first.Count -gt 0) {
                $source = $first[0]
                $playlistTitle = [string](Get-VdPropertyValue -InputObject $j -Name "title" -Default "")
            }
        }

        $formatSummary = Get-VdFormatSummary -Source $source

        $thumbnailBase64 = ""
        $thumbnailError = ""
        $thumbnailUrl = ""

        $sourceThumbnails = @(Get-VdPropertyValue -InputObject $source -Name "thumbnails" -Default @())
        $thumbnailCandidate = @(
            $sourceThumbnails |
                Where-Object {
                    $u = [string](Get-VdPropertyValue -InputObject $_ -Name "url" -Default "")
                    $u -and $u -match '(?i)\.(jpe?g|png)(?:\?|$)'
                } |
                Sort-Object @{ Expression = {
                    $w = Get-VdPropertyValue -InputObject $_ -Name "width" -Default 0
                    $h = Get-VdPropertyValue -InputObject $_ -Name "height" -Default 0
                    ([int64]$w) * ([int64]$h)
                } } -Descending |
                Select-Object -First 1
        )

        if ($thumbnailCandidate.Count -gt 0) {
            $thumbnailUrl = [string](Get-VdPropertyValue -InputObject $thumbnailCandidate[0] -Name "url" -Default "")
        } else {
            $fallbackThumbnail = [string](Get-VdPropertyValue -InputObject $source -Name "thumbnail" -Default "")
            if ($fallbackThumbnail -match '(?i)\.(jpe?g|png)(?:\?|$)') {
                $thumbnailUrl = $fallbackThumbnail
            }
        }

        if ($thumbnailUrl) {
            $client = $null
            try {
                $handler = [Net.Http.HttpClientHandler]::new()
                $handler.AllowAutoRedirect = $true
                $client = [Net.Http.HttpClient]::new($handler)
                $client.Timeout = [TimeSpan]::FromSeconds(12)
                $client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) VideoDownloader/1.0")
                $bytes = $client.GetByteArrayAsync($thumbnailUrl).GetAwaiter().GetResult()
                if ($bytes -and $bytes.Length -gt 0) {
                    $thumbnailBase64 = [Convert]::ToBase64String($bytes)
                } else {
                    $thumbnailError = "thumbnail response was empty"
                }
            } catch {
                $thumbnailError = $_.Exception.Message
            } finally {
                if ($client) { $client.Dispose() }
            }
        } else {
            $thumbnailError = "no JPEG/PNG thumbnail was provided by yt-dlp"
        }

        return [pscustomobject]@{
            Success = $true
            Error = ""
            Title = [string](Get-VdPropertyValue -InputObject $source -Name "title" -Default "")
            PlaylistTitle = $playlistTitle
            Uploader = [string](Get-VdPropertyValue -InputObject $source -Name "uploader" -Default "")
            Duration = [double](Get-VdPropertyValue -InputObject $source -Name "duration" -Default 0)
            ThumbnailBase64 = $thumbnailBase64
            ThumbnailUrl = $thumbnailUrl
            ThumbnailError = $thumbnailError
            Extractor = [string](Get-VdPropertyValue -InputObject $source -Name "extractor_key" -Default "")
            MaxHeight = [int]$formatSummary.MaxHeight
            MaxFps = [double]$formatSummary.MaxFps
            Codecs = [string]$formatSummary.Codecs
            DynamicRange = [string]$formatSummary.DynamicRange
        }
    } catch {
        return [pscustomobject]@{
            Success = $false
            Error = $_.Exception.Message
        }
    } finally {
        Remove-Item $stderrFile -Force -ErrorAction SilentlyContinue
    }
}

Export-ModuleMember -Function Get-VdPreviewCachePath,Read-VdPreviewCache,Write-VdPreviewCache,Remove-VdExpiredPreviewCache,Get-VdPreviewData
