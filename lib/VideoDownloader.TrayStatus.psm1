#requires -Version 7.0
# WinForms NotifyIcon.Text is limited to 63 visible characters on older shells.
function Format-VdTraySpeed {
    param([double]$BytesPerSecond)
    $speed=[Math]::Max([double]0,$BytesPerSecond)
    if ($speed -ge 1GB) { return ("{0:0.#} GB/s" -f ($speed/1GB)) }
    if ($speed -ge 1MB) { return ("{0:0.#} MB/s" -f ($speed/1MB)) }
    if ($speed -ge 1KB) { return ("{0:0.#} KB/s" -f ($speed/1KB)) }
    return ("{0:0} B/s" -f $speed)
}

function Get-VdTrayStatusText {
    param(
        [int]$Success=0,
        [int]$Failed=0,
        [int]$Remaining=0,
        [double]$SpeedBytes=0,
        [bool]$Running=$false,
        [bool]$Paused=$false,
        [bool]$HasSession=$false,
        [bool]$Stopped=$false
    )
    if (-not $HasSession) { return "Video Downloader | Ready" }
    $s=[Math]::Max(0,$Success)
    $f=[Math]::Max(0,$Failed)
    $r=[Math]::Max(0,$Remaining)
    $text="VD | OK $s Err $f Left $r"
    if ($Running) {
        if ($Paused) { $text+=" | Paused" }
        else { $text+=" | $(Format-VdTraySpeed $SpeedBytes)" }
    } elseif ($Stopped) { $text+=" | Stopped" }
    else { $text+=" | Finished" }
    if ($text.Length -gt 63) { return $text.Substring(0,63) }
    return $text
}
Export-ModuleMember -Function Format-VdTraySpeed,Get-VdTrayStatusText
