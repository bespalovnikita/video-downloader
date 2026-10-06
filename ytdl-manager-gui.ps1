#requires -Version 7.0
param(
    [Parameter(ValueFromRemainingArguments=$true)]
    [string[]]$StartupArgs
)

if (-not $IsWindows) { throw "Windows only." }

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Xaml
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeProcessControl {
    [DllImport("ntdll.dll", SetLastError=true)]
    public static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll", SetLastError=true)]
    public static extern int NtResumeProcess(IntPtr processHandle);
}
"@

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$engine = Join-Path $root "ytdl-manager-v8.ps1"
$ytDlp = Join-Path $root "yt-dlp.exe"
$coreModule = Join-Path $root "lib\VideoDownloader.Core.psm1"
$xamlPath = Join-Path $root "ui\MainWindow.xaml"

foreach ($required in @($engine,$ytDlp,$coreModule,$xamlPath)) {
    if (-not (Test-Path $required)) { throw "Missing required file: $required" }
}
Import-Module $coreModule -Force

function Remove-StaleGuiTempDirs {
    $cutoff = (Get-Date).AddHours(-24)
    foreach ($dir in @(Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter "ytdl-gui-*" -ErrorAction SilentlyContinue)) {
        if ($dir.LastWriteTime -gt $cutoff) { continue }
        $owner = Join-Path $dir.FullName "owner.pid"
        if (Test-Path $owner) {
            try {
                $pidValue = [int](Get-Content $owner -ErrorAction Stop | Select-Object -First 1)
                if (Get-Process -Id $pidValue -ErrorAction SilentlyContinue) { continue }
            } catch {}
        }
        try { Remove-Item $dir.FullName -Recurse -Force -ErrorAction Stop } catch {}
    }
}
Remove-StaleGuiTempDirs

[xml]$xaml = Get-Content $xamlPath -Raw -Encoding UTF8
$reader = [System.Xml.XmlNodeReader]::new($xaml)
$window = [System.Windows.Markup.XamlReader]::Load($reader)

$controlNames = @(
    "HeaderStats","HeaderStatus","QueueCount","UrlInput","AddUrlButton","OpenListButton","RemoveQueueButton",
    "ClearQueueButton","MoveUpButton","MoveDownButton","RetryFailedButton","QueueGrid","WatchClipboardCheck",
    "ClipboardHint","DownloadGrid","OpenFileButton","OpenFolderButton","CopyPathButton","OpenUrlButton","LogBox",
    "PreviewImage","PreviewTitle","PreviewMeta","PreviewFormats","ProfileCombo","QualityCombo","CodecCombo",
    "ContainerCombo","RateLimitCombo","ThreadsBox","FragmentsCombo","FilenameTemplateBox","WriteSubsCheck",
    "AutoSubsCheck","EmbedSubsCheck","SubtitleLangsBox","EmbedThumbnailCheck","EmbedMetadataCheck",
    "EmbedChaptersCheck","ArchiveCheck","SponsorCheck","AudioOnlyCheck","OutputBox","PickOutputButton",
    "CookiesBox","PickCookiesButton","DependencyStatus","RefreshDepsButton","InstallFfmpegButton",
    "UpdateYtDlpButton","RegisterProtocolButton","SessionStats","OverallProgress","TrayButton","PauseButton",
    "StopButton","StartButton"
)
foreach ($name in $controlNames) {
    Set-Variable -Name $name -Value $window.FindName($name) -Scope Script
}

$script:queue = [Collections.ObjectModel.ObservableCollection[object]]::new()
$script:downloads = [Collections.ObjectModel.ObservableCollection[object]]::new()
$QueueGrid.ItemsSource = $script:queue
$DownloadGrid.ItemsSource = $script:downloads

$script:proc = $null
$script:runDir = $null
$script:runLog = $null
$script:runEvents = $null
$script:logLines = 0
$script:eventLines = 0
$script:failedUrls = [Collections.Generic.List[string]]::new()
$script:speeds = @{}
$script:pausedPids = @()
$script:isPaused = $false
$script:stopping = $false
$script:previewJob = $null
$script:previewUrl = ""
$script:dependencyJob = $null
$script:lastClipboardText = ""
$script:dragItem = $null
$script:dragStart = [System.Windows.Point]::new(0,0)
$script:sessionStarted = $null
$script:sessionSuccess = 0
$script:sessionFailed = 0
$script:sessionBytes = [int64]0
$script:sessionTotal = 0
$script:lastFiles = @{}
$script:allowClose = $false

function Log-Line([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return }
    $LogBox.AppendText($Text + [Environment]::NewLine)
    $LogBox.ScrollToEnd()
}

function Format-Bytes([int64]$Bytes) {
    if ($Bytes -ge 1TB) { return "{0:N2} TB" -f ($Bytes/1TB) }
    if ($Bytes -ge 1GB) { return "{0:N2} GB" -f ($Bytes/1GB) }
    if ($Bytes -ge 1MB) { return "{0:N1} MB" -f ($Bytes/1MB) }
    if ($Bytes -ge 1KB) { return "{0:N0} KB" -f ($Bytes/1KB) }
    return "$Bytes B"
}

function Format-Speed([double]$Bytes) {
    if ($Bytes -ge 1GB) { return "{0:N1} GB/s" -f ($Bytes/1GB) }
    if ($Bytes -ge 1MB) { return "{0:N1} MB/s" -f ($Bytes/1MB) }
    if ($Bytes -ge 1KB) { return "{0:N0} KB/s" -f ($Bytes/1KB) }
    return "0 KB/s"
}

function Is-ValidUrl([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    $uri = $null
    if (-not [Uri]::TryCreate($Value.Trim(),[UriKind]::Absolute,[ref]$uri)) { return $false }
    return $uri.Scheme -eq "http" -or $uri.Scheme -eq "https"
}

function Extract-Urls([string]$Text) {
    $found = [Collections.Generic.List[string]]::new()
    if ([string]::IsNullOrWhiteSpace($Text)) { return $found.ToArray() }

    foreach ($token in ($Text -split "[\r\n\t ]+")) {
        $v = $token.Trim()
        if (Is-ValidUrl $v -and -not $found.Contains($v)) { [void]$found.Add($v) }
    }
    return $found.ToArray()
}

function Reindex-Queue {
    for ($i=0; $i -lt $script:queue.Count; $i++) {
        $script:queue[$i].Order = $i + 1
    }
    $QueueGrid.Items.Refresh()
    $QueueCount.Text = "$($script:queue.Count) URLs"
    Update-StartState
}

function Add-Urls([string[]]$Urls) {
    $existing = @{}
    foreach ($item in $script:queue) { $existing[$item.Url] = $true }

    foreach ($raw in $Urls) {
        $u = ([string]$raw).Trim()
        if (-not (Is-ValidUrl $u)) { continue }
        if ($existing.ContainsKey($u)) { continue }

        $script:queue.Add([pscustomobject]@{
            Order = $script:queue.Count + 1
            Url = $u
            State = "Queued"
        })
        $existing[$u] = $true
    }
    Reindex-Queue
}

function Add-FileToQueue([string]$Path) {
    if (-not (Test-Path $Path -PathType Leaf)) { return }
    try {
        if ([IO.Path]::GetExtension($Path).ToLowerInvariant() -eq ".url") {
            $line = @(Get-Content $Path -Encoding UTF8) | Where-Object { $_ -match '^URL=' } | Select-Object -First 1
            if ($line) { Add-Urls @($line.Substring(4)) }
            return
        }
        Add-Urls @(Get-Content $Path -Encoding UTF8)
    } catch {
        $message = "Cannot read file: " + $Path + [Environment]::NewLine + $_.Exception.Message
        [System.Windows.MessageBox]::Show($message,"Video Downloader") | Out-Null
    }
}

function Get-QueueUrls {
    return @($script:queue | ForEach-Object { $_.Url })
}

function Move-QueueItem([object]$Item,[int]$TargetIndex) {
    if (-not $Item) { return }
    $old = $script:queue.IndexOf($Item)
    if ($old -lt 0) { return }
    $TargetIndex = [Math]::Max(0,[Math]::Min($script:queue.Count-1,$TargetIndex))
    if ($old -eq $TargetIndex) { return }

    $script:queue.RemoveAt($old)
    if ($TargetIndex -gt $script:queue.Count) { $TargetIndex = $script:queue.Count }
    $script:queue.Insert($TargetIndex,$Item)
    Reindex-Queue
    $QueueGrid.SelectedItem = $Item
}

function Get-GridItemAtPoint($Grid,[System.Windows.Point]$Point) {
    $element = $Grid.InputHitTest($Point)
    while ($element -and -not ($element -is [System.Windows.Controls.DataGridRow])) {
        try { $element = [System.Windows.Media.VisualTreeHelper]::GetParent($element) } catch { $element = $null }
    }
    if ($element -is [System.Windows.Controls.DataGridRow]) { return $element.Item }
    return $null
}

function Find-Download([int]$Slot) {
    foreach ($item in $script:downloads) {
        if ($item.Slot -eq "T$Slot") { return $item }
    }
    return $null
}

function Set-Download {
    param(
        [int]$Slot,
        [string]$State,
        [string]$Title="",
        [string]$Url="",
        [string]$Path="",
        [string]$RawError=""
    )

    $item = Find-Download $Slot
    if (-not $item) {
        $item = [pscustomobject]@{
            Slot = "T$Slot"
            State = $State
            Title = $Title
            Url = $Url
            Path = $Path
            RawError = $RawError
        }
        $script:downloads.Add($item)
    } else {
        $item.State = $State
        if ($Title) { $item.Title = $Title }
        if ($Url) { $item.Url = $Url }
        if ($Path) { $item.Path = $Path }
        if ($RawError) { $item.RawError = $RawError }
    }
    $DownloadGrid.Items.Refresh()
}

function Update-StartState {
    $running = $script:proc -and -not $script:proc.HasExited
    $StartButton.IsEnabled = (-not $running -and $script:queue.Count -gt 0)
    $StopButton.IsEnabled = [bool]$running
    $PauseButton.IsEnabled = [bool]$running
    $RetryFailedButton.IsEnabled = (-not $running -and $script:failedUrls.Count -gt 0)
}

function Set-Running([bool]$Running) {
    $StartButton.IsEnabled = (-not $Running -and $script:queue.Count -gt 0)
    $StopButton.IsEnabled = $Running
    $PauseButton.IsEnabled = $Running

    foreach ($c in @(
        $UrlInput,$AddUrlButton,$OpenListButton,$RemoveQueueButton,$ClearQueueButton,$MoveUpButton,$MoveDownButton,
        $QueueGrid,$ProfileCombo,$QualityCombo,$CodecCombo,$ContainerCombo,$RateLimitCombo,$ThreadsBox,
        $FragmentsCombo,$FilenameTemplateBox,$WriteSubsCheck,$AutoSubsCheck,$EmbedSubsCheck,$SubtitleLangsBox,
        $EmbedThumbnailCheck,$EmbedMetadataCheck,$EmbedChaptersCheck,$ArchiveCheck,$SponsorCheck,$AudioOnlyCheck,
        $OutputBox,$PickOutputButton,$CookiesBox,$PickCookiesButton
    )) {
        $c.IsEnabled = -not $Running
    }

    if ($Running) {
        $HeaderStatus.Text = "● Running"
        $HeaderStatus.Foreground = [System.Windows.Media.Brushes]::LightGreen
    } else {
        $HeaderStatus.Text = "● Ready"
        $HeaderStatus.Foreground = [System.Windows.Media.Brushes]::Gray
    }
}

function Clean-Temp {
    if ($script:runDir -and (Test-Path $script:runDir)) {
        Remove-Item $script:runDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    $script:runDir = $null
    $script:runLog = $null
    $script:runEvents = $null
}

function Update-LiveStats {
    $sum = 0.0
    $active = 0
    foreach ($v in $script:speeds.Values) {
        $sum += [double]$v
        if ([double]$v -gt 0) { $active++ }
    }
    $done = $script:sessionSuccess + $script:sessionFailed
    $HeaderStats.Text = "$active active · $(Format-Speed $sum) · $done/$($script:sessionTotal)"

    if ($script:sessionStarted) {
        $elapsed = (Get-Date) - $script:sessionStarted
        $avg = 0.0
        if ($elapsed.TotalSeconds -gt 0) { $avg = $script:sessionBytes / $elapsed.TotalSeconds }
        $SessionStats.Text = "Session: $($script:sessionSuccess) ok · $($script:sessionFailed) failed · $(Format-Bytes $script:sessionBytes) · $($elapsed.ToString('hh\:mm\:ss')) · avg $(Format-Speed $avg)"
    }
}

function Tail-Log {
    if (-not $script:runLog -or -not (Test-Path $script:runLog)) { return }
    try { $lines = @(Get-Content $script:runLog -Encoding UTF8) } catch { return }
    for ($i=$script:logLines; $i -lt $lines.Count; $i++) { Log-Line $lines[$i] }
    $script:logLines = $lines.Count
}

function Tail-Events {
    if (-not $script:runEvents -or -not (Test-Path $script:runEvents)) { return }
    try { $lines = @(Get-Content $script:runEvents -Encoding UTF8) } catch { return }

    for ($i=$script:eventLines; $i -lt $lines.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
        try { $event = $lines[$i] | ConvertFrom-Json -ErrorAction Stop } catch { continue }

        if ($event.Kind -eq "Event") {
            $slot = [int]$event.Slot
            switch ($event.EventType) {
                "Title" {
                    Set-Download -Slot $slot -State "Preparing" -Title ([string]$event.Title) -Url ([string]$event.Url)
                }
                "Progress" {
                    $parts = @("{0:N1}%" -f [double]$event.Percent)
                    if ($event.Speed) { $parts += [string]$event.Speed }
                    if ($event.ETA) { $parts += ("ETA " + [string]$event.ETA) }
                    Set-Download -Slot $slot -State ($parts -join " · ") -Url ([string]$event.Url)
                    $script:speeds[$slot] = Convert-SpeedTextToBytes ([string]$event.Speed)
                }
                "Merge" { Set-Download -Slot $slot -State "Merging..." -Url ([string]$event.Url) }
                "Retry" { Set-Download -Slot $slot -State "Retrying..." -Url ([string]$event.Url) }
                "File" {
                    $path = [string]$event.Text
                    $script:lastFiles[$slot] = $path
                    Set-Download -Slot $slot -State "Finalizing..." -Url ([string]$event.Url) -Path $path
                }
                "Error" {
                    Set-Download -Slot $slot -State "Error" -Url ([string]$event.Url) -RawError ([string]$event.Text)
                }
            }
        } elseif ($event.Kind -eq "Result") {
            $slot = [int]$event.Slot
            $script:speeds[$slot] = 0

            if ([bool]$event.Success) {
                $script:sessionSuccess++
                $path = [string]$event.Path
                if (-not $path -and $script:lastFiles.ContainsKey($slot)) { $path = [string]$script:lastFiles[$slot] }
                $size = [int64]$event.FileSize
                if ($size -le 0 -and $path -and (Test-Path $path -PathType Leaf)) {
                    try { $size = (Get-Item $path).Length } catch {}
                }
                $script:sessionBytes += $size
                Set-Download -Slot $slot -State "Done" -Url ([string]$event.Url) -Path $path
            } else {
                $script:sessionFailed++
                $u = [string]$event.Url
                if ($u -and -not $script:failedUrls.Contains($u)) { [void]$script:failedUrls.Add($u) }
                $friendly = [string]$event.FriendlyError
                if (-not $friendly) { $friendly = Get-FriendlyDownloadError ([string]$event.Error) }
                Set-Download -Slot $slot -State $friendly -Url $u -RawError ([string]$event.Error)
            }

            $done = $script:sessionSuccess + $script:sessionFailed
            $pct = 0
            if ($script:sessionTotal -gt 0) { $pct = [int](100*$done/$script:sessionTotal) }
            $OverallProgress.Value = [Math]::Min(100,[Math]::Max(0,$pct))
            Update-LiveStats
        }
    }

    $script:eventLines = $lines.Count
    Update-LiveStats
}

function Show-Notification([string]$Title,[string]$Message,[bool]$Error=$false) {
    $shown = $false
    try {
        [void][Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime]
        $safeTitle = [Security.SecurityElement]::Escape($Title)
        $safeMessage = [Security.SecurityElement]::Escape($Message)
        $xmlText = "<toast><visual><binding template='ToastGeneric'><text>$safeTitle</text><text>$safeMessage</text></binding></visual></toast>"
        $doc = [Windows.Data.Xml.Dom.XmlDocument]::new()
        $doc.LoadXml($xmlText)
        $toast = [Windows.UI.Notifications.ToastNotification]::new($doc)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier("Video Downloader").Show($toast)
        $shown = $true
    } catch {}

    if (-not $shown) {
        $tray.BalloonTipTitle = $Title
        $tray.BalloonTipText = $Message
        if ($Error) { $tray.BalloonTipIcon = "Error" } else { $tray.BalloonTipIcon = "Info" }
        $tray.ShowBalloonTip(5000)
    }
}

function Get-ProcessTreeIds([int]$RootPid) {
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Select-Object ProcessId,ParentProcessId)
    $result = [Collections.Generic.List[int]]::new()
    $queue = [Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($RootPid)

    while ($queue.Count -gt 0) {
        $pidValue = $queue.Dequeue()
        if (-not $result.Contains($pidValue)) { [void]$result.Add($pidValue) }
        foreach ($child in @($all | Where-Object { [int]$_.ParentProcessId -eq $pidValue })) {
            $queue.Enqueue([int]$child.ProcessId)
        }
    }
    return $result.ToArray()
}

function Suspend-ProcessTree {
    if (-not $script:proc -or $script:proc.HasExited) { return }
    $ids = @(Get-ProcessTreeIds $script:proc.Id)
    $script:pausedPids = $ids

    foreach ($pidValue in ($ids | Sort-Object -Descending)) {
        try {
            $p = [Diagnostics.Process]::GetProcessById($pidValue)
            [void][NativeProcessControl]::NtSuspendProcess($p.Handle)
            $p.Dispose()
        } catch {}
    }

    $script:isPaused = $true
    $PauseButton.Content = "Resume"
    $HeaderStatus.Text = "● Paused"
    $HeaderStatus.Foreground = [System.Windows.Media.Brushes]::Orange
}

function Resume-ProcessTree {
    foreach ($pidValue in @($script:pausedPids | Sort-Object)) {
        try {
            $p = [Diagnostics.Process]::GetProcessById($pidValue)
            [void][NativeProcessControl]::NtResumeProcess($p.Handle)
            $p.Dispose()
        } catch {}
    }

    $script:pausedPids = @()
    $script:isPaused = $false
    $PauseButton.Content = "Pause"
    $HeaderStatus.Text = "● Running"
    $HeaderStatus.Foreground = [System.Windows.Media.Brushes]::LightGreen
}

function Start-Download([string[]]$OverrideUrls=$null) {
    if ($script:proc -and -not $script:proc.HasExited) { return }

    $items = $OverrideUrls
    if ($null -eq $items) { $items = @(Get-QueueUrls) }
    if ($items.Count -eq 0) { return }

    $dest = $OutputBox.Text.Trim()
    if (-not $dest) {
        $dest = Join-Path (Join-Path $env:USERPROFILE "Downloads") "downloaded-video"
        $OutputBox.Text = $dest
    }

    try {
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        $dest = (Resolve-Path $dest).Path
    } catch {
        [System.Windows.MessageBox]::Show("Cannot use output folder: $dest","Video Downloader") | Out-Null
        return
    }

    $threadCount = 4
    [void][int]::TryParse($ThreadsBox.Text,[ref]$threadCount)
    $threadCount = [Math]::Max(1,[Math]::Min(32,$threadCount))

    Clean-Temp
    $script:runDir = Join-Path ([IO.Path]::GetTempPath()) ("ytdl-gui-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:runDir -Force | Out-Null
    Set-Content (Join-Path $script:runDir "owner.pid") $PID -Encoding ASCII

    $queuePath = Join-Path $script:runDir "queue.txt"
    $script:runLog = Join-Path $script:runDir "run.log"
    $script:runEvents = Join-Path $script:runDir "events.jsonl"
    Set-Content $queuePath $items -Encoding UTF8

    $script:logLines = 0
    $script:eventLines = 0
    $script:failedUrls.Clear()
    $script:speeds.Clear()
    $script:lastFiles.Clear()
    $script:downloads.Clear()
    $LogBox.Clear()
    $script:stopping = $false
    $script:isPaused = $false
    $script:pausedPids = @()
    $PauseButton.Content = "Pause"

    $script:sessionStarted = Get-Date
    $script:sessionSuccess = 0
    $script:sessionFailed = 0
    $script:sessionBytes = [int64]0
    $script:sessionTotal = $items.Count
    $OverallProgress.Value = 0
    Update-LiveStats

    $pwsh = Join-Path $PSHOME "pwsh.exe"
    if (-not (Test-Path $pwsh)) { $pwsh = "pwsh.exe" }
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $pwsh
    $psi.WorkingDirectory = $root
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $resultDir = $dest
    if ($ArchiveCheck.IsChecked) { $resultDir = Join-Path $dest (Get-Date -Format "yyyy-MM-dd") }

    $args = @(
        "-NoProfile","-ExecutionPolicy","Bypass","-File",$engine,
        "-In",$queuePath,"-Out",$dest,"-Threads",[string]$threadCount,
        "-NoProgress","-Log",$script:runLog,"-EventFile",$script:runEvents,"-ResultDir",$resultDir,
        "-Quality",[string]$QualityCombo.SelectedItem,
        "-Container",[string]$ContainerCombo.SelectedItem,
        "-VideoCodec",[string]$CodecCombo.SelectedItem,
        "-FilenameTemplate",$FilenameTemplateBox.Text,
        "-Retries","3","-RetryDelaySeconds","5"
    )

    $rate = $RateLimitCombo.Text.Trim()
    if ($rate -and $rate -ne "Unlimited") { $args += @("-RateLimit",$rate) }

    if ([string]$FragmentsCombo.SelectedItem -eq "Auto") { $args += "-AutoFragments" }
    else { $args += @("-Fragments",[string]$FragmentsCombo.SelectedItem) }

    if ($ArchiveCheck.IsChecked) { $args += "-Archive" }
    if ($SponsorCheck.IsChecked) { $args += "-SponsorBlock" }
    if ($AudioOnlyCheck.IsChecked) { $args += "-AudioOnly" }

    $cookie = $CookiesBox.Text.Trim()
    if ($cookie) {
        if (-not (Test-Path $cookie)) {
            [System.Windows.MessageBox]::Show("Cookies file not found.","Video Downloader") | Out-Null
            return
        }
        $args += @("-Cookies",(Resolve-Path $cookie).Path)
    }

    if ($WriteSubsCheck.IsChecked) { $args += "-WriteSubtitles" }
    if ($AutoSubsCheck.IsChecked) { $args += "-WriteAutoSubtitles" }
    if ($EmbedSubsCheck.IsChecked) { $args += "-EmbedSubtitles" }
    if ($SubtitleLangsBox.Text.Trim()) { $args += @("-SubtitleLangs",$SubtitleLangsBox.Text.Trim()) }
    if ($EmbedThumbnailCheck.IsChecked) { $args += "-EmbedThumbnail" }
    if ($EmbedMetadataCheck.IsChecked) { $args += "-EmbedMetadata" }
    if ($EmbedChaptersCheck.IsChecked) { $args += "-EmbedChapters" }

    foreach ($a in $args) { [void]$psi.ArgumentList.Add($a) }

    try {
        $script:proc = [Diagnostics.Process]::Start($psi)
        Set-Running $true
        Log-Line "Started $($items.Count) URL(s) into $dest"
    } catch {
        $script:proc = $null
        Set-Running $false
        Clean-Temp
        [System.Windows.MessageBox]::Show("Failed to start downloader: $($_.Exception.Message)","Video Downloader") | Out-Null
    }
}

function Stop-Download {
    if (-not $script:proc -or $script:proc.HasExited) { return }
    $script:stopping = $true
    if ($script:isPaused) { Resume-ProcessTree }
    try { $script:proc.Kill($true) } catch {}
}

function Finish-Download {
    Tail-Events
    Tail-Log

    $code = $null
    try { $code = $script:proc.ExitCode } catch {}
    try { $script:proc.Dispose() } catch {}
    $script:proc = $null

    Set-Running $false
    $PauseButton.Content = "Pause"
    $script:isPaused = $false
    $script:pausedPids = @()

    Update-LiveStats
    $elapsed = if ($script:sessionStarted) { (Get-Date)-$script:sessionStarted } else { [TimeSpan]::Zero }
    $summary = "$($script:sessionSuccess) ok · $($script:sessionFailed) failed · $(Format-Bytes $script:sessionBytes) · $($elapsed.ToString('hh\:mm\:ss'))"

    if ($script:stopping) {
        $HeaderStatus.Text = "● Stopped"
        $HeaderStatus.Foreground = [System.Windows.Media.Brushes]::Orange
    } elseif ($script:sessionFailed -eq 0 -and $code -eq 0) {
        $HeaderStatus.Text = "● Completed"
        $HeaderStatus.Foreground = [System.Windows.Media.Brushes]::LightGreen
        try { [Media.SystemSounds]::Asterisk.Play() } catch {}
        Show-Notification "Video Downloader" $summary
    } else {
        $HeaderStatus.Text = "● Completed with errors"
        $HeaderStatus.Foreground = [System.Windows.Media.Brushes]::Tomato
        try { [Media.SystemSounds]::Hand.Play() } catch {}
        Show-Notification "Video Downloader" $summary $true
    }

    $SessionStats.Text = "Session: $summary"
    $RetryFailedButton.IsEnabled = $script:failedUrls.Count -gt 0
    $script:stopping = $false
    Clean-Temp
}

function Start-Preview([string]$Url) {
    if (-not (Is-ValidUrl $Url)) { return }
    if ($script:previewJob) {
        try { Stop-Job $script:previewJob -ErrorAction SilentlyContinue; Remove-Job $script:previewJob -Force -ErrorAction SilentlyContinue } catch {}
    }

    $script:previewUrl = $Url
    $PreviewTitle.Text = "Loading..."
    $PreviewMeta.Text = $Url
    $PreviewFormats.Text = ""
    $PreviewImage.Source = $null

    $cookie = $CookiesBox.Text.Trim()
    $exe = $ytDlp
    $script:previewJob = Start-ThreadJob -ArgumentList $exe,$Url,$cookie -ScriptBlock {
        param($exe,$url,$cookie)

        $args = @("--dump-single-json","--skip-download","--no-warnings","--encoding","utf-8","--impersonate","chrome")
        if ($cookie -and (Test-Path $cookie)) { $args += @("--cookies",$cookie) }
        $args += $url

        $raw = & $exe @args 2>$null
        if ($LASTEXITCODE -ne 0) { throw "yt-dlp metadata failed" }
        $j = ($raw -join [Environment]::NewLine) | ConvertFrom-Json

        $formats = @($j.formats | Where-Object { $_.vcodec -and $_.vcodec -ne "none" })
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

        [pscustomobject]@{
            Title = [string]$j.title
            Uploader = [string]$j.uploader
            Duration = [double]$j.duration
            Thumbnail = [string]$j.thumbnail
            Extractor = [string]$j.extractor_key
            MaxHeight = $maxHeight
            MaxFps = $maxFps
            Codecs = (@($codecSet) -join ", ")
            DynamicRange = (@($rangeSet) -join ", ")
        }
    }
}

function Complete-Preview {
    if (-not $script:previewJob -or $script:previewJob.State -eq "Running") { return }

    try {
        if ($script:previewJob.State -ne "Completed") { throw "Preview failed" }
        $r = Receive-Job $script:previewJob -ErrorAction Stop | Select-Object -Last 1

        $PreviewTitle.Text = if ($r.Title) { [string]$r.Title } else { "Untitled" }
        $duration = ""
        if ($r.Duration -gt 0) {
            $ts = [TimeSpan]::FromSeconds([double]$r.Duration)
            $duration = if ($ts.TotalHours -ge 1) { $ts.ToString("hh\:mm\:ss") } else { $ts.ToString("mm\:ss") }
        }

        $meta = @()
        if ($r.Uploader) { $meta += [string]$r.Uploader }
        if ($duration) { $meta += $duration }
        if ($r.Extractor) { $meta += [string]$r.Extractor }
        $PreviewMeta.Text = $meta -join " · "

        $formatParts = @()
        if ($r.MaxHeight -gt 0) {
            $res = [string]$r.MaxHeight + "p"
            if ($r.MaxFps -gt 0) { $res += ("{0:N0}" -f [double]$r.MaxFps) }
            $formatParts += $res
        }
        if ($r.Codecs) { $formatParts += ("Codecs: " + [string]$r.Codecs) }
        if ($r.DynamicRange) { $formatParts += ("HDR: " + [string]$r.DynamicRange) }
        $PreviewFormats.Text = $formatParts -join " · "

        if ($r.Thumbnail) {
            try {
                $bitmap = [System.Windows.Media.Imaging.BitmapImage]::new()
                $bitmap.BeginInit()
                $bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
                $bitmap.UriSource = [Uri]::new([string]$r.Thumbnail)
                $bitmap.EndInit()
                $bitmap.Freeze()
                $PreviewImage.Source = $bitmap
            } catch {}
        }
    } catch {
        $PreviewTitle.Text = "Preview unavailable"
        $PreviewMeta.Text = $_.Exception.Message
    } finally {
        try { Remove-Job $script:previewJob -Force -ErrorAction SilentlyContinue } catch {}
        $script:previewJob = $null
    }
}

function Refresh-Dependencies {
    $parts = [Collections.Generic.List[string]]::new()
    [void]$parts.Add("PowerShell $($PSVersionTable.PSVersion)")

    try {
        $v = & $ytDlp --version 2>$null | Select-Object -First 1
        if ($v) { [void]$parts.Add("yt-dlp $v") } else { [void]$parts.Add("yt-dlp: missing") }
    } catch { [void]$parts.Add("yt-dlp: error") }

    $ffmpeg = Join-Path $root "ffmpeg.exe"
    if (-not (Test-Path $ffmpeg)) {
        $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
        if ($cmd) { $ffmpeg = $cmd.Source }
    }

    $ffprobe = Join-Path $root "ffprobe.exe"
    if (-not (Test-Path $ffprobe)) {
        $cmd = Get-Command ffprobe.exe -ErrorAction SilentlyContinue
        if ($cmd) { $ffprobe = $cmd.Source }
    }

    if ($ffmpeg -and (Test-Path $ffmpeg)) {
        try { [void]$parts.Add((& $ffmpeg -version 2>$null | Select-Object -First 1)) } catch { [void]$parts.Add("ffmpeg: installed") }
        $InstallFfmpegButton.IsEnabled = $false
    } else {
        [void]$parts.Add("ffmpeg: missing")
        $InstallFfmpegButton.IsEnabled = $true
    }

    if ($ffprobe -and (Test-Path $ffprobe)) { [void]$parts.Add("ffprobe: installed") }
    else { [void]$parts.Add("ffprobe: missing") }

    $DependencyStatus.Text = $parts -join [Environment]::NewLine
}

function Start-DependencyJob([string]$Kind) {
    if ($script:dependencyJob) { return }

    if ($Kind -eq "ffmpeg") {
        $InstallFfmpegButton.IsEnabled = $false
        $DependencyStatus.Text = "Downloading FFmpeg..."
        $destRoot = $root
        $script:dependencyJob = Start-ThreadJob -ArgumentList $destRoot -ScriptBlock {
            param($destRoot)
            $temp = Join-Path ([IO.Path]::GetTempPath()) ("video-downloader-ffmpeg-" + [guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Path $temp -Force | Out-Null
            try {
                $zip = Join-Path $temp "ffmpeg.zip"
                Invoke-WebRequest "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $zip
                Expand-Archive $zip (Join-Path $temp "unpacked") -Force
                $ffmpeg = Get-ChildItem (Join-Path $temp "unpacked") -Filter ffmpeg.exe -Recurse | Select-Object -First 1
                $ffprobe = Get-ChildItem (Join-Path $temp "unpacked") -Filter ffprobe.exe -Recurse | Select-Object -First 1
                if (-not $ffmpeg -or -not $ffprobe) { throw "FFmpeg binaries not found in archive" }
                Copy-Item $ffmpeg.FullName (Join-Path $destRoot "ffmpeg.exe") -Force
                Copy-Item $ffprobe.FullName (Join-Path $destRoot "ffprobe.exe") -Force
                [pscustomobject]@{ Success=$true; Message="FFmpeg installed next to the app." }
            } catch {
                [pscustomobject]@{ Success=$false; Message=$_.Exception.Message }
            } finally {
                Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    } elseif ($Kind -eq "ytdlp") {
        $UpdateYtDlpButton.IsEnabled = $false
        $DependencyStatus.Text = "Updating yt-dlp..."
        $exe = $ytDlp
        $script:dependencyJob = Start-ThreadJob -ArgumentList $exe -ScriptBlock {
            param($exe)
            try {
                $text = & $exe -U 2>&1
                [pscustomobject]@{ Success=($LASTEXITCODE -eq 0); Message=($text -join [Environment]::NewLine) }
            } catch {
                [pscustomobject]@{ Success=$false; Message=$_.Exception.Message }
            }
        }
    }
}

function Complete-DependencyJob {
    if (-not $script:dependencyJob -or $script:dependencyJob.State -eq "Running") { return }

    try {
        $r = Receive-Job $script:dependencyJob -ErrorAction Stop | Select-Object -Last 1
        if ($r.Success) {
            Log-Line ([string]$r.Message)
            Show-Notification "Video Downloader" ([string]$r.Message)
        } else {
            Log-Line ("Dependency operation failed: " + [string]$r.Message)
            [System.Windows.MessageBox]::Show([string]$r.Message,"Dependency error") | Out-Null
        }
    } catch {
        Log-Line ("Dependency operation failed: " + $_.Exception.Message)
    } finally {
        try { Remove-Job $script:dependencyJob -Force -ErrorAction SilentlyContinue } catch {}
        $script:dependencyJob = $null
        $UpdateYtDlpButton.IsEnabled = $true
        Refresh-Dependencies
    }
}

function Register-Protocol {
    try {
        $base = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\Classes\videodownloader")
        $base.SetValue("","URL:VideoDownloader Protocol")
        $base.SetValue("URL Protocol","")

        $exe = Join-Path $root "VideoDownloader.exe"
        $iconKey = $base.CreateSubKey("DefaultIcon")
        if (Test-Path $exe) { $iconKey.SetValue("",('"' + $exe + '",0')) }
        $iconKey.Close()

        $commandKey = $base.CreateSubKey("shell\open\command")
        if (Test-Path $exe) {
            $command = '"' + $exe + '" "%1"'
        } else {
            $pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source
            $command = '"' + $pwsh + '" -NoProfile -ExecutionPolicy Bypass -STA -File "' + $PSCommandPath + '" "%1"'
        }
        $commandKey.SetValue("",$command)
        $commandKey.Close()
        $base.Close()

        [System.Windows.MessageBox]::Show("Registered videodownloader:// for the current user.","Video Downloader") | Out-Null
    } catch {
        [System.Windows.MessageBox]::Show("Protocol registration failed: $($_.Exception.Message)","Video Downloader") | Out-Null
    }
}

function Handle-StartupArgs {
    foreach ($arg in @($StartupArgs)) {
        if ([string]::IsNullOrWhiteSpace($arg)) { continue }

        if ($arg.StartsWith("videodownloader://",[StringComparison]::OrdinalIgnoreCase)) {
            $payload = $arg.Substring("videodownloader://".Length).TrimStart('/')
            try { $payload = [Uri]::UnescapeDataString($payload) } catch {}
            if (Is-ValidUrl $payload) { Add-Urls @($payload) }
            continue
        }

        if (Test-Path $arg -PathType Leaf) {
            Add-FileToQueue $arg
            continue
        }

        if (Is-ValidUrl $arg) { Add-Urls @($arg) }
    }
}

function Apply-Profile([string]$Name) {
    switch ($Name) {
        "Universal" {
            $QualityCombo.SelectedItem="Best"; $CodecCombo.SelectedItem="Auto"; $ContainerCombo.SelectedItem="Auto"
            $AudioOnlyCheck.IsChecked=$false; $EmbedMetadataCheck.IsChecked=$true; $EmbedChaptersCheck.IsChecked=$true
            $EmbedThumbnailCheck.IsChecked=$false; $WriteSubsCheck.IsChecked=$false; $EmbedSubsCheck.IsChecked=$false
        }
        "4K archive" {
            $QualityCombo.SelectedItem="2160"; $CodecCombo.SelectedItem="Auto"; $ContainerCombo.SelectedItem="MKV"
            $AudioOnlyCheck.IsChecked=$false; $EmbedMetadataCheck.IsChecked=$true; $EmbedChaptersCheck.IsChecked=$true
            $EmbedThumbnailCheck.IsChecked=$true
        }
        "MP4 compatibility" {
            $QualityCombo.SelectedItem="1080"; $CodecCombo.SelectedItem="H264"; $ContainerCombo.SelectedItem="MP4"
            $AudioOnlyCheck.IsChecked=$false; $EmbedMetadataCheck.IsChecked=$true; $EmbedThumbnailCheck.IsChecked=$true
        }
        "Music MP3" {
            $QualityCombo.SelectedItem="Best"; $CodecCombo.SelectedItem="Auto"; $ContainerCombo.SelectedItem="Auto"
            $AudioOnlyCheck.IsChecked=$true; $EmbedMetadataCheck.IsChecked=$true; $EmbedThumbnailCheck.IsChecked=$true
            $WriteSubsCheck.IsChecked=$false; $EmbedSubsCheck.IsChecked=$false
        }
        "Subtitles archive" {
            $QualityCombo.SelectedItem="Best"; $CodecCombo.SelectedItem="Auto"; $ContainerCombo.SelectedItem="MKV"
            $AudioOnlyCheck.IsChecked=$false; $WriteSubsCheck.IsChecked=$true; $AutoSubsCheck.IsChecked=$true
            $EmbedSubsCheck.IsChecked=$true; $EmbedMetadataCheck.IsChecked=$true; $EmbedChaptersCheck.IsChecked=$true
        }
    }
}

foreach ($v in @("Best","2160","1440","1080","720")) { [void]$QualityCombo.Items.Add($v) }
foreach ($v in @("Auto","H264","VP9","AV1")) { [void]$CodecCombo.Items.Add($v) }
foreach ($v in @("Auto","MP4","MKV","WebM")) { [void]$ContainerCombo.Items.Add($v) }
foreach ($v in @("Unlimited","1M","5M","10M","25M","50M")) { [void]$RateLimitCombo.Items.Add($v) }
foreach ($v in @("Auto","1","2","3","4","6","8")) { [void]$FragmentsCombo.Items.Add($v) }
foreach ($v in @("Universal","4K archive","MP4 compatibility","Music MP3","Subtitles archive")) { [void]$ProfileCombo.Items.Add($v) }

$QualityCombo.SelectedItem = "Best"
$CodecCombo.SelectedItem = "Auto"
$ContainerCombo.SelectedItem = "Auto"
$RateLimitCombo.SelectedItem = "Unlimited"
$FragmentsCombo.SelectedItem = "Auto"
$ProfileCombo.SelectedItem = "Universal"
$OutputBox.Text = Join-Path (Join-Path $env:USERPROFILE "Downloads") "downloaded-video"

$folderDialog = [System.Windows.Forms.FolderBrowserDialog]::new()
$fileDialog = [System.Windows.Forms.OpenFileDialog]::new()

$appIcon = Join-Path $root "assets\app.ico"
$tray = [System.Windows.Forms.NotifyIcon]::new()
if (Test-Path $appIcon) {
    try { $tray.Icon = [Drawing.Icon]::new($appIcon) } catch { $tray.Icon = [Drawing.SystemIcons]::Application }
} else {
    $tray.Icon = [Drawing.SystemIcons]::Application
}
$tray.Text = "Video Downloader"
$tray.Visible = $true
$trayMenu = [System.Windows.Forms.ContextMenuStrip]::new()
$trayShow = $trayMenu.Items.Add("Open")
$trayFolder = $trayMenu.Items.Add("Open downloads")
[void]$trayMenu.Items.Add("-")
$trayExit = $trayMenu.Items.Add("Exit")
$tray.ContextMenuStrip = $trayMenu

$queueMenu = [System.Windows.Controls.ContextMenu]::new()
$queueOpen = [System.Windows.Controls.MenuItem]::new(); $queueOpen.Header = "Open URL"
$queueCopy = [System.Windows.Controls.MenuItem]::new(); $queueCopy.Header = "Copy URL"
$queueRetry = [System.Windows.Controls.MenuItem]::new(); $queueRetry.Header = "Download selected"
$queueRemove = [System.Windows.Controls.MenuItem]::new(); $queueRemove.Header = "Remove"
foreach ($m in @($queueOpen,$queueCopy,$queueRetry,$queueRemove)) { [void]$queueMenu.Items.Add($m) }
$QueueGrid.ContextMenu = $queueMenu

$downloadMenu = [System.Windows.Controls.ContextMenu]::new()
$dlOpenFile = [System.Windows.Controls.MenuItem]::new(); $dlOpenFile.Header = "Open file"
$dlFolder = [System.Windows.Controls.MenuItem]::new(); $dlFolder.Header = "Show in folder"
$dlCopyPath = [System.Windows.Controls.MenuItem]::new(); $dlCopyPath.Header = "Copy path"
$dlOpenUrl = [System.Windows.Controls.MenuItem]::new(); $dlOpenUrl.Header = "Open URL"
$dlRetry = [System.Windows.Controls.MenuItem]::new(); $dlRetry.Header = "Retry URL"
$dlError = [System.Windows.Controls.MenuItem]::new(); $dlError.Header = "Show raw error"
foreach ($m in @($dlOpenFile,$dlFolder,$dlCopyPath,$dlOpenUrl,$dlRetry,$dlError)) { [void]$downloadMenu.Items.Add($m) }
$DownloadGrid.ContextMenu = $downloadMenu

function Open-SelectedFile {
    $item = $DownloadGrid.SelectedItem
    if ($item -and $item.Path -and (Test-Path $item.Path -PathType Leaf)) { Start-Process $item.Path }
}

function Open-SelectedFolder {
    $item = $DownloadGrid.SelectedItem
    if ($item -and $item.Path -and (Test-Path $item.Path -PathType Leaf)) {
        Start-Process explorer.exe -ArgumentList @("/select," + [char]34 + $item.Path + [char]34)
    } elseif (Test-Path $OutputBox.Text) {
        Start-Process explorer.exe -ArgumentList @($OutputBox.Text)
    }
}

function Copy-SelectedPath {
    $item = $DownloadGrid.SelectedItem
    if ($item -and $item.Path) { [System.Windows.Clipboard]::SetText([string]$item.Path) }
}

$AddUrlButton.Add_Click({
    $values = @(Extract-Urls $UrlInput.Text)
    if ($values.Count -eq 0) {
        [System.Windows.MessageBox]::Show("No valid HTTP/HTTPS URL found.","Video Downloader") | Out-Null
        return
    }
    Add-Urls $values
    $UrlInput.Clear()
})

$UrlInput.Add_KeyDown({
    param($s,$e)
    if ($e.Key -eq [System.Windows.Input.Key]::Enter) {
        $AddUrlButton.RaiseEvent([System.Windows.RoutedEventArgs]::new([System.Windows.Controls.Button]::ClickEvent))
        $e.Handled = $true
    }
})

$OpenListButton.Add_Click({
    $fileDialog.Filter = "URL lists (*.txt;*.url)|*.txt;*.url|All files (*.*)|*.*"
    if ($fileDialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Add-FileToQueue $fileDialog.FileName }
})

$RemoveQueueButton.Add_Click({
    foreach ($item in @($QueueGrid.SelectedItems)) { [void]$script:queue.Remove($item) }
    Reindex-Queue
})

$ClearQueueButton.Add_Click({ $script:queue.Clear(); Reindex-Queue })

$MoveUpButton.Add_Click({
    $item = $QueueGrid.SelectedItem
    if ($item) { Move-QueueItem $item ([Math]::Max(0,$script:queue.IndexOf($item)-1)) }
})

$MoveDownButton.Add_Click({
    $item = $QueueGrid.SelectedItem
    if ($item) { Move-QueueItem $item ([Math]::Min($script:queue.Count-1,$script:queue.IndexOf($item)+1)) }
})

$RetryFailedButton.Add_Click({
    $items = @($script:failedUrls.ToArray())
    if ($items.Count -gt 0) { Start-Download $items }
})

$QueueGrid.Add_SelectionChanged({
    $item = $QueueGrid.SelectedItem
    if ($item -and $item.Url -and $item.Url -ne $script:previewUrl) { Start-Preview ([string]$item.Url) }
})

$QueueGrid.Add_PreviewMouseLeftButtonDown({
    param($s,$e)
    $script:dragStart = $e.GetPosition($QueueGrid)
    $script:dragItem = Get-GridItemAtPoint $QueueGrid $script:dragStart
})

$QueueGrid.Add_MouseMove({
    param($s,$e)
    if ($e.LeftButton -ne [System.Windows.Input.MouseButtonState]::Pressed -or -not $script:dragItem) { return }
    $pos = $e.GetPosition($QueueGrid)
    $dx = [Math]::Abs($pos.X-$script:dragStart.X)
    $dy = [Math]::Abs($pos.Y-$script:dragStart.Y)
    if ($dx -lt [System.Windows.SystemParameters]::MinimumHorizontalDragDistance -and $dy -lt [System.Windows.SystemParameters]::MinimumVerticalDragDistance) { return }

    $data = [System.Windows.DataObject]::new()
    $data.SetData("VideoDownloader.QueueItem",$script:dragItem)
    [void][System.Windows.DragDrop]::DoDragDrop($QueueGrid,$data,[System.Windows.DragDropEffects]::Move)
})

$QueueGrid.Add_DragOver({
    param($s,$e)
    if ($e.Data.GetDataPresent("VideoDownloader.QueueItem") -or $e.Data.GetDataPresent([System.Windows.DataFormats]::FileDrop) -or $e.Data.GetDataPresent([System.Windows.DataFormats]::UnicodeText)) {
        $e.Effects = [System.Windows.DragDropEffects]::Move
    } else {
        $e.Effects = [System.Windows.DragDropEffects]::None
    }
    $e.Handled = $true
})

$QueueGrid.Add_Drop({
    param($s,$e)
    if ($e.Data.GetDataPresent("VideoDownloader.QueueItem")) {
        $drag = $e.Data.GetData("VideoDownloader.QueueItem")
        $target = Get-GridItemAtPoint $QueueGrid ($e.GetPosition($QueueGrid))
        if ($drag -and $target -and $drag -ne $target) { Move-QueueItem $drag ($script:queue.IndexOf($target)) }
        $script:dragItem = $null
        return
    }

    if ($e.Data.GetDataPresent([System.Windows.DataFormats]::FileDrop)) {
        foreach ($p in @($e.Data.GetData([System.Windows.DataFormats]::FileDrop))) { Add-FileToQueue ([string]$p) }
    }
    if ($e.Data.GetDataPresent([System.Windows.DataFormats]::UnicodeText)) {
        Add-Urls @(Extract-Urls ([string]$e.Data.GetData([System.Windows.DataFormats]::UnicodeText)))
    }
})

$window.Add_Drop({
    param($s,$e)
    if ($e.Data.GetDataPresent([System.Windows.DataFormats]::FileDrop)) {
        foreach ($p in @($e.Data.GetData([System.Windows.DataFormats]::FileDrop))) { Add-FileToQueue ([string]$p) }
    } elseif ($e.Data.GetDataPresent([System.Windows.DataFormats]::UnicodeText)) {
        Add-Urls @(Extract-Urls ([string]$e.Data.GetData([System.Windows.DataFormats]::UnicodeText)))
    }
})

$ProfileCombo.Add_SelectionChanged({
    if ($ProfileCombo.SelectedItem) { Apply-Profile ([string]$ProfileCombo.SelectedItem) }
})

$PickOutputButton.Add_Click({
    $folderDialog.SelectedPath = $OutputBox.Text
    if ($folderDialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $OutputBox.Text = $folderDialog.SelectedPath }
})

$PickCookiesButton.Add_Click({
    $fileDialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
    if ($fileDialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $CookiesBox.Text = $fileDialog.FileName }
})

$OpenFileButton.Add_Click({ Open-SelectedFile })
$OpenFolderButton.Add_Click({ Open-SelectedFolder })
$CopyPathButton.Add_Click({ Copy-SelectedPath })
$OpenUrlButton.Add_Click({ $item=$DownloadGrid.SelectedItem; if ($item -and (Is-ValidUrl ([string]$item.Url))) { Start-Process ([string]$item.Url) } })

$queueOpen.Add_Click({ $item=$QueueGrid.SelectedItem; if ($item -and (Is-ValidUrl ([string]$item.Url))) { Start-Process ([string]$item.Url) } })
$queueCopy.Add_Click({ $item=$QueueGrid.SelectedItem; if ($item) { [System.Windows.Clipboard]::SetText([string]$item.Url) } })
$queueRetry.Add_Click({ $item=$QueueGrid.SelectedItem; if ($item) { Start-Download @([string]$item.Url) } })
$queueRemove.Add_Click({ $item=$QueueGrid.SelectedItem; if ($item) { [void]$script:queue.Remove($item); Reindex-Queue } })

$dlOpenFile.Add_Click({ Open-SelectedFile })
$dlFolder.Add_Click({ Open-SelectedFolder })
$dlCopyPath.Add_Click({ Copy-SelectedPath })
$dlOpenUrl.Add_Click({ $item=$DownloadGrid.SelectedItem; if ($item -and (Is-ValidUrl ([string]$item.Url))) { Start-Process ([string]$item.Url) } })
$dlRetry.Add_Click({ $item=$DownloadGrid.SelectedItem; if ($item -and $item.Url) { Start-Download @([string]$item.Url) } })
$dlError.Add_Click({ $item=$DownloadGrid.SelectedItem; if ($item -and $item.RawError) { [System.Windows.MessageBox]::Show([string]$item.RawError,"Raw yt-dlp error") | Out-Null } })

$StartButton.Add_Click({ Start-Download })
$StopButton.Add_Click({ Stop-Download })
$PauseButton.Add_Click({ if ($script:isPaused) { Resume-ProcessTree } else { Suspend-ProcessTree } })

$RefreshDepsButton.Add_Click({ Refresh-Dependencies })
$InstallFfmpegButton.Add_Click({ Start-DependencyJob "ffmpeg" })
$UpdateYtDlpButton.Add_Click({ Start-DependencyJob "ytdlp" })
$RegisterProtocolButton.Add_Click({ Register-Protocol })

$tray.Add_DoubleClick({ $window.Show(); $window.WindowState=[System.Windows.WindowState]::Normal; $window.Activate() })
$trayShow.Add_Click({ $window.Show(); $window.WindowState=[System.Windows.WindowState]::Normal; $window.Activate() })
$trayFolder.Add_Click({ if (Test-Path $OutputBox.Text) { Start-Process explorer.exe -ArgumentList @($OutputBox.Text) } })
$trayExit.Add_Click({ $script:allowClose=$true; $window.Close() })
$TrayButton.Add_Click({ $window.Hide() })
$window.Add_StateChanged({ if ($window.WindowState -eq [System.Windows.WindowState]::Minimized) { $window.Hide() } })

$timer = [System.Windows.Threading.DispatcherTimer]::new()
$timer.Interval = [TimeSpan]::FromMilliseconds(350)
$timer.Add_Tick({
    Tail-Events
    Tail-Log
    Complete-Preview
    Complete-DependencyJob

    if ($script:proc) {
        try { if ($script:proc.HasExited) { Finish-Download } } catch {}
    }
})

$clipboardTimer = [System.Windows.Threading.DispatcherTimer]::new()
$clipboardTimer.Interval = [TimeSpan]::FromMilliseconds(900)
$clipboardTimer.Add_Tick({
    try {
        if (-not [System.Windows.Clipboard]::ContainsText()) { return }
        $text = [System.Windows.Clipboard]::GetText()
        if ($text -eq $script:lastClipboardText) { return }

        $values = @(Extract-Urls $text)
        if ($values.Count -gt 0) {
            $script:lastClipboardText = $text
            if ($WatchClipboardCheck.IsChecked) {
                Add-Urls $values
                $ClipboardHint.Text = "Auto-added $($values.Count) URL(s) from clipboard."
            } else {
                $ClipboardHint.Text = "URL detected in clipboard. Enable Watch clipboard to auto-add."
            }
        }
    } catch {}
})

$window.Add_Closing({
    param($s,$e)
    if (-not $script:allowClose -and $script:proc -and -not $script:proc.HasExited) {
        $answer = [System.Windows.MessageBox]::Show("A download is running. Stop it and exit?","Video Downloader",[System.Windows.MessageBoxButton]::YesNo,[System.Windows.MessageBoxImage]::Warning)
        if ($answer -ne [System.Windows.MessageBoxResult]::Yes) {
            $e.Cancel = $true
            return
        }
        if ($script:isPaused) { Resume-ProcessTree }
        try { $script:proc.Kill($true) } catch {}
    }

    try {
        if ($script:previewJob) { Stop-Job $script:previewJob -ErrorAction SilentlyContinue; Remove-Job $script:previewJob -Force -ErrorAction SilentlyContinue }
        if ($script:dependencyJob) { Stop-Job $script:dependencyJob -ErrorAction SilentlyContinue; Remove-Job $script:dependencyJob -Force -ErrorAction SilentlyContinue }
    } catch {}

    $tray.Visible = $false
    $tray.Dispose()
    $timer.Stop()
    $clipboardTimer.Stop()
    Clean-Temp
})

Reindex-Queue
Refresh-Dependencies
Handle-StartupArgs
$timer.Start()
$clipboardTimer.Start()
[void]$window.ShowDialog()
