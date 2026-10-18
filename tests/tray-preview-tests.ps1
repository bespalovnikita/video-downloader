#requires -Version 7.0
$ErrorActionPreference="Stop"
$root=Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'lib/VideoDownloader.TrayStatus.psm1') -Force

function Assert([bool]$Condition,[string]$Description) { if (-not $Condition) { throw $Description } }
$ready=Get-VdTrayStatusText
Assert ($ready -eq "Video Downloader | Ready") "Idle tooltip must show Ready"
$active=Get-VdTrayStatusText -Success 12 -Failed 2 -Remaining 5 -SpeedBytes (2.5MB) -Running $true -HasSession $true
Assert ($active.Contains("OK 12") -and $active.Contains("Err 2") -and $active.Contains("Left 5") -and $active.Contains("2.5 MB/s")) "Active tooltip must show all live metrics"
$paused=Get-VdTrayStatusText -Success 2 -Failed 1 -Remaining 9 -SpeedBytes (30MB) -Running $true -Paused $true -HasSession $true
Assert ($paused.Contains("Paused") -and -not $paused.Contains("MB/s")) "Paused tooltip must not show a misleading live speed"
$finished=Get-VdTrayStatusText -Success 9 -Failed 1 -Remaining 0 -HasSession $true
Assert ($finished.Contains("Finished") -and $finished.Contains("Left 0")) "Finished tooltip must retain session totals"
$stopped=Get-VdTrayStatusText -Success 1 -Remaining 4 -HasSession $true -Stopped $true
Assert ($stopped.Contains("Stopped") -and $stopped.Contains("Left 4")) "Stopped tooltip must show remaining jobs"
Assert ((Get-VdTrayStatusText -Success ([int]::MaxValue) -Failed ([int]::MaxValue) -Remaining ([int]::MaxValue) -Running $true -HasSession $true -SpeedBytes (50GB)).Length -le 63) "NotifyIcon.Text must fit the 63-character shell limit"

# Evaluate real GUI priority function in an isolated harness. No yt-dlp or
# actual clipboard needed; its task is to schedule background prefetch, not
# launch a second foreground metadata request.
$gui=Join-Path $root 'ytdl-manager-gui.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($gui,[ref]$tokens,[ref]$errors)
Assert ($errors.Count -eq 0) "GUI must parse"
$fn=@($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "Queue-PreviewPrefetch" },$true))[0]
Assert ($null -ne $fn) "GUI prefetch scheduler must exist"
function Is-ValidUrl([string]$value) { return $value -match '^https://'}
function Get-CachedPreview([string]$Url) { return $null }
function Start-NextPreviewPrefetch { }
$script:previewPrefetchUrl=""
$script:previewPrefetchQueue=[Collections.Generic.List[string]]::new()
$script:previewPrefetchQueued=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
Invoke-Expression $fn.Extent.Text
Queue-PreviewPrefetch -Url "https://example.com/old-1"
Queue-PreviewPrefetch -Url "https://example.com/old-2"
Queue-PreviewPrefetch -Url "https://example.com/clipboard" -Priority $true
Assert ($script:previewPrefetchQueue.Count -eq 3) "All URLs must be scheduled"
Assert ($script:previewPrefetchQueue[0] -eq "https://example.com/clipboard") "New clipboard URL must be the next metadata prefetch"
Queue-PreviewPrefetch -Url "https://example.com/clipboard" -Priority $true
Assert ($script:previewPrefetchQueue.Count -eq 3) "Priority prefetch cannot create duplicate work"
Assert ($script:previewPrefetchQueue[0] -eq "https://example.com/clipboard") "Duplicate priority must preserve clipboard priority"

$text=[IO.File]::ReadAllText($gui)
Assert ($text.Contains('Add-Urls $urls -FromClipboard -ShowPreview')) "Manual Paste must prioritize and immediately select clipboard preview"
Assert ($text.Contains('Start-Preview ([string]$newSelection.Url)')) "Manual Paste must immediately display its new link preview"
Assert ($text.Contains('Add-Urls $values -FromClipboard')) "Watch clipboard must mark links as clipboard-derived"
Assert ($text.Contains('Queue-PreviewPrefetch -Url $u -Priority ([bool]$FromClipboard -and [bool]$running)')) "Clipboard additions must request high-priority prefetch during downloads"
Assert ($text.Contains('Complete-PreviewPrefetch')) "GUI must process completed background prefetch while downloading"
Write-Host "Tray tooltip formatting and live clipboard preview priority tests passed."
