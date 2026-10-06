Set-StrictMode -Version Latest

function Convert-SpeedTextToBytes {
    param([AllowNull()][string]$Speed)

    if ([string]::IsNullOrWhiteSpace($Speed)) { return [double]0 }
    $s = $Speed.Trim()
    if ($s -notmatch '([0-9.,]+)\s*([KMGT]?i?B)/s') { return [double]0 }

    $value = 0.0
    $num = $matches[1].Replace(",", ".")
    if (-not [double]::TryParse(
        $num,
        [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$value
    )) { return [double]0 }

    switch -Regex ($matches[2].ToUpperInvariant()) {
        '^K' { return $value * 1KB }
        '^M' { return $value * 1MB }
        '^G' { return $value * 1GB }
        '^T' { return $value * 1TB }
        default { return $value }
    }
}

function Parse-VdProgressLine {
    param([AllowNull()][string]$Line)

    if ([string]::IsNullOrWhiteSpace($Line)) { return $null }
    if (-not $Line.StartsWith("__VD_PROGRESS__|")) { return $null }

    $parts = $Line.Split('|')
    if ($parts.Count -lt 6) { return $null }

    $percentText = $parts[1].Trim().TrimEnd('%').Trim()
    $percent = 0.0
    [void][double]::TryParse(
        $percentText.Replace(",", "."),
        [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$percent
    )

    $downloaded = [int64]0
    [void][int64]::TryParse($parts[4].Trim(), [ref]$downloaded)

    $total = [int64]0
    [void][int64]::TryParse($parts[5].Trim(), [ref]$total)

    [pscustomobject]@{
        Percent = $percent
        Speed = $parts[2].Trim()
        ETA = $parts[3].Trim()
        DownloadedBytes = $downloaded
        TotalBytes = $total
    }
}

function Get-FriendlyDownloadError {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return "Неизвестная ошибка" }
    $e = $Text.ToLowerInvariant()

    if ($e -match 'private|sign in|login|cookies|authentication') { return "Нужна авторизация / cookies" }
    if ($e -match 'not available|unavailable|removed|deleted|does not exist') { return "Видео недоступно" }
    if ($e -match 'geo|country|region|not available in your') { return "Региональное ограничение" }
    if ($e -match '429|too many requests|rate.?limit') { return "Слишком много запросов" }
    if ($e -match 'timeout|timed out|network|connection|dns|unable to download|temporary failure|reset by peer') { return "Ошибка сети" }
    if ($e -match 'requested format|format is not available') { return "Формат недоступен" }
    if ($e -match 'ffmpeg|ffprobe') { return "Ошибка FFmpeg" }
    return "Ошибка загрузки"
}

function Test-TransientDownloadError {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    $e = $Text.ToLowerInvariant()

    return [bool]($e -match (
        'timeout|timed out|temporary failure|connection reset|connection aborted|' +
        'connection refused|remote host|network is unreachable|dns|' +
        'unable to download|http error 429|too many requests|http error 5\d\d|' +
        'server error|read timed out|tls|ssl'
    ))
}

function Get-VideoCodecSelector {
    param(
        [ValidateSet("Auto","H264","VP9","AV1")][string]$Codec = "Auto"
    )

    switch ($Codec) {
        "H264" { return "avc1" }
        "VP9"  { return "vp9" }
        "AV1"  { return "av01" }
        default { return "" }
    }
}

Export-ModuleMember -Function Convert-SpeedTextToBytes,Parse-VdProgressLine,Get-FriendlyDownloadError,Test-TransientDownloadError,Get-VideoCodecSelector
