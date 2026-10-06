$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root "lib\VideoDownloader.Core.psm1") -Force

function Assert-Equal($Expected,$Actual,[string]$Message) {
    if ($Expected -ne $Actual) {
        throw "$Message. Expected=[$Expected], Actual=[$Actual]"
    }
}

function Assert-True([bool]$Value,[string]$Message) {
    if (-not $Value) { throw $Message }
}

$p = Parse-VdProgressLine "__VD_PROGRESS__|42.7%|12.4MiB/s|00:18|104857600|245760000"
Assert-True ($null -ne $p) "Progress parser returned null"
Assert-Equal 42.7 $p.Percent "Percent parse"
Assert-Equal "12.4MiB/s" $p.Speed "Speed parse"
Assert-Equal "00:18" $p.ETA "ETA parse"
Assert-Equal 104857600 $p.DownloadedBytes "Downloaded bytes parse"
Assert-Equal 245760000 $p.TotalBytes "Total bytes parse"

Assert-True ($null -eq (Parse-VdProgressLine "[download] 42% of 1GiB")) "Human yt-dlp line must not be parsed as machine progress"

Assert-True (Test-TransientDownloadError "ERROR: HTTP Error 429: Too Many Requests") "429 must be transient"
Assert-True (Test-TransientDownloadError "connection reset by peer") "Connection reset must be transient"
Assert-True (-not (Test-TransientDownloadError "ERROR: Video unavailable")) "Unavailable video must not be transient"
Assert-True (-not (Test-TransientDownloadError "Sign in to confirm your age")) "Auth error must not be transient"

Assert-Equal "Нужна авторизация / cookies" (Get-FriendlyDownloadError "Sign in and pass cookies") "Friendly auth error"
Assert-Equal "Видео недоступно" (Get-FriendlyDownloadError "Video unavailable") "Friendly unavailable error"
Assert-Equal "Ошибка сети" (Get-FriendlyDownloadError "connection timed out") "Friendly network error"

Assert-Equal "" (Get-VideoCodecSelector Auto) "Auto codec selector"
Assert-Equal "avc1" (Get-VideoCodecSelector H264) "H264 selector"
Assert-Equal "vp9" (Get-VideoCodecSelector VP9) "VP9 selector"
Assert-Equal "av01" (Get-VideoCodecSelector AV1) "AV1 selector"

$bytes = Convert-SpeedTextToBytes "10.5MiB/s"
Assert-True ($bytes -gt 10MB -and $bytes -lt 11MB) "Speed conversion failed"

Write-Host "Core parser/retry tests passed."
