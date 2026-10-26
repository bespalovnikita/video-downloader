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

function Test-VdPreviewCacheRefreshNeeded {
    param([AllowNull()][object]$Preview,[string]$YtDlpPath='')
    if($null -eq $Preview){return $false}
    $thumbnail=[string](Get-VdPropertyValue $Preview 'ThumbnailBase64' '')
    if($thumbnail){
        if([int](Get-VdPropertyValue $Preview 'ThumbnailValidationVersion' 0) -ge 1){return $false}
        try {
            if(-not (Test-VdPreviewImageBytes ([Convert]::FromBase64String($thumbnail)))){return $true}
            $Preview | Add-Member -NotePropertyName ThumbnailValidationVersion -NotePropertyValue 1 -Force
            return $false
        } catch {return $true}
    }
    $errorText=[string](Get-VdPropertyValue $Preview 'ThumbnailError' '')
    if($errorText -ceq 'no JPEG/PNG thumbnail was provided by yt-dlp' -and [int](Get-VdPropertyValue $Preview 'PreviewFetchVersion' 0) -lt 2){return $true}
    return $errorText -cin @('WebP/AVIF thumbnail requires FFmpeg; text metadata is still available.',
        'WebP/AVIF thumbnail needs FFmpeg; text metadata is available.') -and
        [bool](Find-VdPreviewConverter -YtDlpPath $YtDlpPath)
}

function Find-VdPreviewConverter([string]$YtDlpPath='') {
    $directory=if($YtDlpPath){[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($YtDlpPath))}else{Split-Path -Parent $PSScriptRoot}
    $nextToYtDlp=Join-Path $directory 'ffmpeg.exe'
    if([IO.File]::Exists($nextToYtDlp)){return $nextToYtDlp}
    foreach($command in @('ffmpeg.exe','ffmpeg')) {
        $found=Get-Command $command -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if($found){return [string]$found.Source}
    }
    return ''
}

function Test-VdThumbnailBytes([byte[]]$Bytes) {
    # WPF can decode these formats without a separately installed WebP codec.
    if ($Bytes.Length -ge 8 -and [Convert]::ToHexString($Bytes[0..7]) -ceq '89504E470D0A1A0A') { return $true }
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 255 -and $Bytes[1] -eq 216 -and $Bytes[2] -eq 255) { return $true }
    if ($Bytes.Length -ge 6 -and [Text.Encoding]::ASCII.GetString($Bytes,0,6) -in @('GIF87a','GIF89a')) { return $true }
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 66 -and $Bytes[1] -eq 77) { return $true }
    if ($Bytes.Length -ge 4 -and [Convert]::ToHexString($Bytes[0..3]) -in @('49492A00','4D4D002A','00000100')) { return $true }
    return $false
}

function Test-VdPreviewImageBytes {
    param([byte[]]$Bytes,[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None)
    if($Bytes.Length -gt 2000000 -or -not (Test-VdThumbnailBytes $Bytes)){return $false}
    $CancellationToken.ThrowIfCancellationRequested()
    $headerStream=$null;$imageStream=$null
    try {
        Add-Type -AssemblyName PresentationCore -ErrorAction Stop
        $headerStream=[IO.MemoryStream]::new($Bytes,$false)
        $decoder=[Windows.Media.Imaging.BitmapDecoder]::Create($headerStream,
            [Windows.Media.Imaging.BitmapCreateOptions]::IgnoreColorProfile,[Windows.Media.Imaging.BitmapCacheOption]::None)
        if($decoder.Frames.Count -lt 1){return $false}
        $width=[int]$decoder.Frames[0].PixelWidth;$height=[int]$decoder.Frames[0].PixelHeight
        if($width -le 0 -or $height -le 0 -or [long]$width*$height -gt 16777216){return $false}
        $CancellationToken.ThrowIfCancellationRequested()
        $imageStream=[IO.MemoryStream]::new($Bytes,$false)
        $bitmap=[Windows.Media.Imaging.BitmapImage]::new()
        $bitmap.BeginInit()
        $bitmap.CacheOption=[Windows.Media.Imaging.BitmapCacheOption]::OnLoad
        $bitmap.CreateOptions=[Windows.Media.Imaging.BitmapCreateOptions]::IgnoreColorProfile
        $scale=[Math]::Min(1.0,720.0/[Math]::Max($width,$height))
        $bitmap.DecodePixelWidth=[Math]::Max(1,[int][Math]::Round($width*$scale))
        $bitmap.DecodePixelHeight=[Math]::Max(1,[int][Math]::Round($height*$scale))
        $bitmap.StreamSource=$imageStream
        $bitmap.EndInit()
        $stride=[int][Math]::Ceiling($bitmap.PixelWidth*$bitmap.Format.BitsPerPixel/8.0)
        if($stride -le 0 -or [long]$stride*$bitmap.PixelHeight -gt 16777216){return $false}
        $pixels=[byte[]]::new($stride*$bitmap.PixelHeight)
        # Force the decoder to read the raster; a valid header alone is not an image.
        $bitmap.CopyPixels($pixels,$stride,0)
        $bitmap.Freeze()
        $CancellationToken.ThrowIfCancellationRequested()
        return $true
    } catch [OperationCanceledException] {throw}
      catch {return $false}
    finally {
        if($imageStream){$imageStream.Dispose()}
        if($headerStream){$headerStream.Dispose()}
    }
}

function ConvertTo-VdPreviewPng {
    param([byte[]]$Bytes,[string]$YtDlpPath,[Threading.CancellationToken]$CancellationToken)
    $isWebP=$Bytes.Length -ge 12 -and [Text.Encoding]::ASCII.GetString($Bytes,0,4) -ceq 'RIFF' -and [Text.Encoding]::ASCII.GetString($Bytes,8,4) -ceq 'WEBP'
    $isAvif=$Bytes.Length -ge 12 -and [Text.Encoding]::ASCII.GetString($Bytes,4,4) -ceq 'ftyp' -and [Text.Encoding]::ASCII.GetString($Bytes,8,4) -in @('avif','avis')
    if(-not $isWebP -and -not $isAvif){throw 'Thumbnail response is not a supported JPEG/PNG/GIF/BMP/TIFF/ICO image.'}
    $converter=Find-VdPreviewConverter -YtDlpPath $YtDlpPath
    if(-not $converter){throw 'WebP/AVIF thumbnail requires FFmpeg; text metadata is still available.'}
    $directory=Join-Path ([IO.Path]::GetTempPath()) ('vd-preview-image-'+[guid]::NewGuid().ToString('N'))
    $process=$null;$output=$null
    try {
        $CancellationToken.ThrowIfCancellationRequested()
        [void][IO.Directory]::CreateDirectory($directory)
        $inputFile=Join-Path $directory 'thumbnail.bin'
        [IO.File]::WriteAllBytes($inputFile,$Bytes)
        $start=[Diagnostics.ProcessStartInfo]::new($converter)
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $arguments=@('-hide_banner','-loglevel','error','-nostdin','-max_alloc','67108864','-max_pixels','16777216','-protocol_whitelist','file,pipe','-i',$inputFile,
            '-frames:v','1','-an','-vf',"scale=w='min(720,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease",'-f','image2pipe','-c:v','png','pipe:1')
        foreach($argument in $arguments){[void]$start.ArgumentList.Add($argument)}
        $process=[Diagnostics.Process]::new();$process.StartInfo=$start
        if(-not $process.Start()){throw 'Cannot start FFmpeg for thumbnail conversion.'}
        $output=[IO.MemoryStream]::new();$diagnostic=[Text.StringBuilder]::new()
        $outBuffer=[byte[]]::new(16384);$errBuffer=[byte[]]::new(4096)
        $stdout=$process.StandardOutput.BaseStream.ReadAsync($outBuffer,0,$outBuffer.Length,$CancellationToken)
        $stderr=$process.StandardError.BaseStream.ReadAsync($errBuffer,0,$errBuffer.Length,$CancellationToken)
        while($null -ne $stdout -or $null -ne $stderr -or -not $process.HasExited) {
            $CancellationToken.ThrowIfCancellationRequested()
            if($stdout -and $stdout.IsCompleted) {
                $read=$stdout.GetAwaiter().GetResult()
                if($read -eq 0){$stdout=$null}
                else {
                    if($output.Length+$read -gt 2000000){throw 'Converted thumbnail exceeds 2MB.'}
                    $output.Write($outBuffer,0,$read)
                    $stdout=$process.StandardOutput.BaseStream.ReadAsync($outBuffer,0,$outBuffer.Length,$CancellationToken)
                }
            }
            if($stderr -and $stderr.IsCompleted) {
                $read=$stderr.GetAwaiter().GetResult()
                if($read -eq 0){$stderr=$null}
                else {
                    if($diagnostic.Length -lt 2048){[void]$diagnostic.Append([Text.Encoding]::UTF8.GetString($errBuffer,0,[Math]::Min($read,2048-$diagnostic.Length)))}
                    $stderr=$process.StandardError.BaseStream.ReadAsync($errBuffer,0,$errBuffer.Length,$CancellationToken)
                }
            }
            if($stdout -or $stderr -or -not $process.HasExited){Start-Sleep -Milliseconds 10}
        }
        $result=$output.ToArray()
        if($process.ExitCode -ne 0 -or $result.Length -lt 8 -or [Convert]::ToHexString($result[0..7]) -cne '89504E470D0A1A0A') {
            throw ('FFmpeg could not convert the thumbnail. '+$diagnostic.ToString().Trim())
        }
        return ,$result
    } finally {
        if($process){
            try{if(-not $process.HasExited){$process.Kill($true);[void]$process.WaitForExit(2000)}}catch{}
            $process.Dispose()
        }
        if($output){$output.Dispose()}
        if([IO.Directory]::Exists($directory)){[IO.Directory]::Delete($directory,$true)}
    }
}

function Read-VdPreviewCache {
    param(
        [Parameter(Mandatory=$true)][string]$CacheDir,
        [Parameter(Mandatory=$true)][string]$Url,
        [ValidateRange(1,8760)][int]$TtlHours = 72,
        [string]$YtDlpPath=''
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
        # Disk contents have not yet been decoded by this process. Do not trust
        # a validation marker left by an older process or a damaged cache file.
        $record | Add-Member -NotePropertyName ThumbnailValidationVersion -NotePropertyValue 0 -Force
        if (Test-VdPreviewCacheRefreshNeeded $record -YtDlpPath $YtDlpPath) {
            Remove-Item $path -Force -ErrorAction SilentlyContinue
            return $null
        }

        $savedAt = ([DateTimeOffset]$record.SavedAtUtc).UtcDateTime

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

function New-VdPreviewCacheRecord {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][object]$Preview
    )
    $record = [ordered]@{
        CacheVersion = 1
        PreviewFetchVersion = [int](Get-VdPropertyValue -InputObject $Preview -Name 'PreviewFetchVersion' -Default 0)
        ThumbnailValidationVersion = [int](Get-VdPropertyValue -InputObject $Preview -Name 'ThumbnailValidationVersion' -Default 0)
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
    return [pscustomobject]$record
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
    $record=New-VdPreviewCacheRecord -Url $Url -Preview $Preview

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
        [AllowEmptyString()][string]$CookiePath = "",
        [ValidateRange(1,300)][int]$TimeoutSeconds = 30,
        [ValidateRange(1,60)][int]$ThumbnailTimeoutSeconds = 12
    )

    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    $process = $null
    try {
        $args = @(
            "--ignore-config",
            "--dump-single-json",
            "--skip-download",
            "--no-warnings",
            "--playlist-items","1",
            "--encoding","utf-8",
            "--impersonate","chrome"
        )
        if ($CookiePath -and (Test-Path $CookiePath)) { $args += @("--cookies",$CookiePath) }
        $args += $Url

        $psi = [Diagnostics.ProcessStartInfo]::new($YtDlpPath)
        $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
        $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
        $psi.StandardOutputEncoding=[Text.Encoding]::UTF8; $psi.StandardErrorEncoding=[Text.Encoding]::UTF8
        foreach ($argument in $args) { [void]$psi.ArgumentList.Add($argument) }
        $process=[Diagnostics.Process]::new(); $process.StartInfo=$psi
        if (-not $process.Start()) { throw 'Could not start yt-dlp for preview.' }
        $stdout=$process.StandardOutput.ReadToEndAsync($deadline.Token)
        $stderrTask=$process.StandardError.ReadToEndAsync($deadline.Token)
        [void]$process.WaitForExitAsync($deadline.Token).GetAwaiter().GetResult()
        $jsonText=$stdout.GetAwaiter().GetResult()
        $stderr=$stderrTask.GetAwaiter().GetResult().Trim()
        $exitCode=$process.ExitCode

        if ($exitCode -ne 0) {
            if ([string]::IsNullOrWhiteSpace($stderr)) { $stderr = "yt-dlp exit code $exitCode" }
            return [pscustomobject]@{
                Success = $false
                Error = $stderr
            }
        }

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
        $fallbackThumbnail = [string](Get-VdPropertyValue -InputObject $source -Name "thumbnail" -Default "")
        $candidates = @(
            @($sourceThumbnails) + @([pscustomobject]@{url=$fallbackThumbnail}) |
                Where-Object {
                    $candidate=[string](Get-VdPropertyValue $_ 'url' '')
                    $parsed=$null
                    $candidate -and [Uri]::TryCreate($candidate,[UriKind]::Absolute,[ref]$parsed) -and
                        $parsed.Scheme -in @('http','https') -and -not [string]::IsNullOrWhiteSpace($parsed.Host)
                } |
                Sort-Object @{Expression={
                    $parsed=[Uri][string](Get-VdPropertyValue $_ 'url' '')
                    if($parsed.AbsolutePath -match '(?i)\.(jpe?g|png)$'){1}else{0}
                };Descending=$true},@{Expression={
                    $width=0.0;$height=0.0
                    [void][double]::TryParse([string](Get-VdPropertyValue $_ 'width' 0),[ref]$width)
                    [void][double]::TryParse([string](Get-VdPropertyValue $_ 'height' 0),[ref]$height)
                    if([double]::IsFinite($width) -and [double]::IsFinite($height)) {[Math]::Max(0,$width)*[Math]::Max(0,$height)} else {0}
                };Descending=$true}
        )
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $thumbnailUrls=[Collections.Generic.List[string]]::new()
        foreach($candidate in $candidates) {
            $candidateUrl=[string](Get-VdPropertyValue $candidate 'url' '')
            if($seen.Add($candidateUrl)){$thumbnailUrls.Add($candidateUrl)}
            if($thumbnailUrls.Count -ge 16){break}
        }

        if ($thumbnailUrls.Count -gt 0) {
            $client = $null
            # All alternative thumbnails share one budget. A failed first URL
            # must not reset either the image deadline or the download cap.
            $imageDeadline = [Threading.CancellationTokenSource]::CreateLinkedTokenSource($deadline.Token)
            $imageDeadline.CancelAfter([TimeSpan]::FromSeconds($ThumbnailTimeoutSeconds))
            $downloadedBytes=0L
            try {
                $handler = [Net.Http.HttpClientHandler]::new()
                $handler.AllowAutoRedirect = $true
                $client = [Net.Http.HttpClient]::new($handler)
                $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
                $client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) VideoDownloader/1.0")
                $client.DefaultRequestHeaders.TryAddWithoutValidation('Accept','image/jpeg,image/png,image/gif,image/bmp,image/tiff,*/*;q=0.1') | Out-Null
                $origin=$null
                if([Uri]::TryCreate($Url,[UriKind]::Absolute,[ref]$origin) -and $origin.Scheme -in @('http','https')) {
                    $referer=[UriBuilder]::new($origin)
                    $referer.UserName='';$referer.Password='';$referer.Path='/';$referer.Query='';$referer.Fragment=''
                    $client.DefaultRequestHeaders.Referrer=$referer.Uri
                }
                foreach($candidateUrl in $thumbnailUrls) {
                    $response=$null;$stream=$null;$buffer=$null
                    try {
                        $imageDeadline.Token.ThrowIfCancellationRequested()
                        $response=$client.GetAsync($candidateUrl,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$imageDeadline.Token).GetAwaiter().GetResult()
                        [void]$response.EnsureSuccessStatusCode()
                        if ($response.Content.Headers.ContentLength -gt (2000000-$downloadedBytes)) { throw 'Thumbnail exceeds 2MB.' }
                        $stream=$response.Content.ReadAsStreamAsync($imageDeadline.Token).GetAwaiter().GetResult()
                        $buffer=[IO.MemoryStream]::new()
                        $chunk=[byte[]]::new(16384)
                        while (($read=$stream.ReadAsync($chunk,0,$chunk.Length,$imageDeadline.Token).GetAwaiter().GetResult()) -gt 0) {
                            $downloadedBytes+=$read
                            if ($downloadedBytes -gt 2000000) { throw 'Thumbnail exceeds 2MB.' }
                            $buffer.Write($chunk,0,$read)
                        }
                        $bytes=$buffer.ToArray()
                        if (-not $bytes.Length) { throw 'Thumbnail response was empty.' }
                        if (-not (Test-VdThumbnailBytes $bytes)) {
                            $bytes=ConvertTo-VdPreviewPng -Bytes $bytes -YtDlpPath $YtDlpPath -CancellationToken $imageDeadline.Token
                        }
                        if(-not (Test-VdPreviewImageBytes -Bytes $bytes -CancellationToken $imageDeadline.Token)){
                            throw 'Thumbnail image could not be decoded safely.'
                        }
                        $thumbnailBase64=[Convert]::ToBase64String($bytes)
                        $thumbnailUrl=$candidateUrl
                        $thumbnailError=''
                        break
                    } catch {
                        $thumbnailError=$_.Exception.Message
                        if($imageDeadline.IsCancellationRequested -or $downloadedBytes -ge 2000000){break}
                    } finally {
                        if($buffer){$buffer.Dispose()}
                        if($stream){$stream.Dispose()}
                        if($response){$response.Dispose()}
                    }
                }
            } finally {
                if ($client) { $client.Dispose() }
                $imageDeadline.Dispose()
            }
        } else {
            $thumbnailError = "yt-dlp did not provide a valid HTTP(S) thumbnail URL"
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
            ThumbnailValidationVersion = 1
            PreviewFetchVersion = 2
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
        if ($process) {
            try {
                if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(3000) }
            } catch [InvalidOperationException] { } catch [ComponentModel.Win32Exception] { }
            $process.Dispose()
        }
        $deadline.Dispose()
    }
}

Export-ModuleMember -Function Get-VdPreviewCachePath,Read-VdPreviewCache,Write-VdPreviewCache,New-VdPreviewCacheRecord,Remove-VdExpiredPreviewCache,Get-VdPreviewData,Test-VdPreviewCacheRefreshNeeded
