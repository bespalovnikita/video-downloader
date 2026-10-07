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

        if ((-not $j.formats) -and $j.entries) {
            $first = @($j.entries | Where-Object { $_ } | Select-Object -First 1)
            if ($first.Count -gt 0) {
                $source = $first[0]
                $playlistTitle = [string]$j.title
            }
        }

        $formats = @($source.formats | Where-Object { $_.vcodec -and $_.vcodec -ne "none" })
        $maxHeight = 0
        $maxFps = 0.0
        $codecSet = [Collections.Generic.HashSet[string]]::new()
        $rangeSet = [Collections.Generic.HashSet[string]]::new()

        foreach ($fmt in $formats) {
            if ($fmt.height -and [int]$fmt.height -gt $maxHeight) { $maxHeight = [int]$fmt.height }
            if ($fmt.fps -and [double]$fmt.fps -gt $maxFps) { $maxFps = [double]$fmt.fps }

            $vc = [string]$fmt.vcodec
            if ($vc.StartsWith("av01")) { [void]$codecSet.Add("AV1") }
            elseif ($vc.StartsWith("vp9")) { [void]$codecSet.Add("VP9") }
            elseif ($vc.StartsWith("avc1") -or $vc.StartsWith("h264")) { [void]$codecSet.Add("H264") }
            elseif ($vc) { [void]$codecSet.Add($vc.Split('.')[0]) }

            $dr = [string]$fmt.dynamic_range
            if ($dr -and $dr -ne "SDR" -and $dr -ne "None") { [void]$rangeSet.Add($dr) }
        }

        $thumbnailBase64 = ""
        $thumbnailError = ""
        $thumbnailUrl = ""

        $thumbnailCandidate = @(
            $source.thumbnails |
                Where-Object {
                    $u = [string]$_.url
                    $u -and $u -match '(?i)\.(jpe?g|png)(?:\?|$)'
                } |
                Sort-Object @{ Expression = { ([int64]$_.width) * ([int64]$_.height) } } -Descending |
                Select-Object -First 1
        )

        if ($thumbnailCandidate.Count -gt 0) {
            $thumbnailUrl = [string]$thumbnailCandidate[0].url
        } elseif ([string]$source.thumbnail -match '(?i)\.(jpe?g|png)(?:\?|$)') {
            $thumbnailUrl = [string]$source.thumbnail
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
            Title = [string]$source.title
            PlaylistTitle = $playlistTitle
            Uploader = [string]$source.uploader
            Duration = [double]$source.duration
            ThumbnailBase64 = $thumbnailBase64
            ThumbnailUrl = $thumbnailUrl
            ThumbnailError = $thumbnailError
            Extractor = [string]$source.extractor_key
            MaxHeight = $maxHeight
            MaxFps = $maxFps
            Codecs = (@($codecSet) -join ", ")
            DynamicRange = (@($rangeSet) -join ", ")
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
