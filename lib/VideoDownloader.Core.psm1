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
        StreamId = if ($parts.Count -gt 6) { $parts[6] } else { "default" }
    }
}

function Get-FriendlyDownloadError {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return "Неизвестная ошибка" }
    $e = $Text.ToLowerInvariant()

    if ($e -match 'private|sign in|login|cookies|authentication') { return "Нужна авторизация / cookies" }
    if ($e -match 'geo|country|region|not available in your') { return "Региональное ограничение" }
    if ($e -match 'requested format|format is not available') { return "Формат недоступен" }
    if ($e -match 'not available|unavailable|removed|deleted|does not exist') { return "Видео недоступно" }
    if ($e -match '429|too many requests|rate.?limit') { return "Слишком много запросов" }
    if ($e -match 'timeout|timed out|network|connection|dns|unable to download|temporary failure|reset by peer') { return "Ошибка сети" }
    if ($e -match 'ffmpeg|ffprobe') { return "Ошибка FFmpeg" }
    return "Ошибка загрузки"
}

function Test-TransientDownloadError {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    $e = $Text.ToLowerInvariant()

    if ($e -match 'http error (400|401|403|404|405|410|451)\b|private video|sign in|login required|video unavailable|video has been removed|requested format is not available') { return $false }

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

function Read-VdEventLines {
    param([string]$Path,[hashtable]$Cursor)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $stream = [IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -lt [long]$Cursor.Offset) { $Cursor.Offset = 0L }
        [void]$stream.Seek([long]$Cursor.Offset,[IO.SeekOrigin]::Begin)
        $buffer = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($buffer)
            $bytes = $buffer.ToArray()
            $last = [Array]::LastIndexOf($bytes,[byte]10)
            if ($last -lt 0) { return }
            $text = [Text.Encoding]::UTF8.GetString($bytes,0,$last+1).TrimStart([char]0xFEFF)
            $Cursor.Offset = [long]$Cursor.Offset + $last + 1
            foreach ($line in $text.Split("`n")) { if ($line.Trim()) { $line.TrimEnd("`r") } }
        } finally { $buffer.Dispose() }
    } finally { $stream.Dispose() }
}

function Add-VdTransferSample {
    param([hashtable]$State,[string]$StreamId,[long]$DownloadedBytes)
    if (-not $State.ContainsKey('Streams')) { $State.Streams = @{}; $State.Bytes = 0L }
    $previous = if ($State.Streams.ContainsKey($StreamId)) { [long]$State.Streams[$StreamId] } else { 0L }
    $current = [Math]::Max(0L,$DownloadedBytes)
    # A counter reset denotes a new transfer/retry; repeated final samples add nothing.
    $delta = if ($current -lt $previous) { $current } else { $current-$previous }
    $State.Bytes += $delta
    $State.Streams[$StreamId] = $current
    return [long]$State.Bytes
}

Export-ModuleMember -Function Convert-SpeedTextToBytes,Parse-VdProgressLine,Get-FriendlyDownloadError,Test-TransientDownloadError,Get-VideoCodecSelector,Read-VdEventLines,Add-VdTransferSample
