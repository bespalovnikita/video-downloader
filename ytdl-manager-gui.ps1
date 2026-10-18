#requires -Version 7.0
param(
    [Parameter(ValueFromRemainingArguments=$true)]
    [string[]]$StartupArgs
)

if (-not $IsWindows) { throw "Windows only." }
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $root 'lib\VideoDownloader.Ipc.psm1') -Force
$ipcName = Get-VdIpcName

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Xaml
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$createdGuiMutex = $false
$script:guiMutex = [Threading.Mutex]::new($true, "Local\VideoDownloader.GuiSession", [ref]$createdGuiMutex)
if (-not $createdGuiMutex) {
    try {
        $message = ConvertTo-Json -InputObject @($StartupArgs) -Compress
        [void][VideoDownloader.Ipc]::Send($ipcName,$message).GetAwaiter().GetResult()
    } catch {
        [System.Windows.MessageBox]::Show("Could not forward the link to the running app. Please try again: " + $_.Exception.Message, "Video Downloader") | Out-Null
    }
    $script:guiMutex.Dispose()
    return
}
$script:ipcServer = [VideoDownloader.Ipc]::new($ipcName)

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
$previewCacheModule = Join-Path $root "lib\VideoDownloader.PreviewCache.psm1"
$sessionModule = Join-Path $root "lib\VideoDownloader.Session.psm1"
$historyModule = Join-Path $root "lib\VideoDownloader.DownloadHistory.psm1"
$liveQueueModule = Join-Path $root "lib\VideoDownloader.LiveQueue.psm1"
$cancelModule = Join-Path $root "lib\VideoDownloader.Cancellation.psm1"
$trayStatusModule = Join-Path $root "lib\VideoDownloader.TrayStatus.psm1"
$duplicateDialogXaml = Join-Path $root "ui\DuplicateDownloadDialog.xaml"
$lifecycleModule = Join-Path $root "lib\VideoDownloader.WindowLifecycle.psm1"
$xamlPath = Join-Path $root "ui\MainWindow.xaml"

foreach ($required in @($engine,$ytDlp,$coreModule,$previewCacheModule,$lifecycleModule,$sessionModule,$historyModule,$liveQueueModule,$cancelModule,$trayStatusModule,$duplicateDialogXaml,$xamlPath)) {
    if (-not (Test-Path $required)) { throw "Missing required file: $required" }
}
Import-Module $coreModule -Force
Import-Module $previewCacheModule -Force
Import-Module $lifecycleModule -Force
Import-Module $sessionModule -Force
Import-Module $historyModule -Force
Import-Module $liveQueueModule -Force
Import-Module $cancelModule -Force
Import-Module $trayStatusModule -Force

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
    "HeaderStats","HeaderStatus","QueueCount","UrlInput","AddUrlButton","PasteClipboardButton","OpenListButton","RemoveQueueButton",
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
$script:liveQueuePath = $null
$script:cancelDir = $null
$script:removedUrls = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$script:runUrls = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$script:logLines = 0
$script:eventLines = 0
$script:eventCursor = @{ Offset=0L }
$script:failedUrls = [Collections.Generic.List[string]]::new()
$script:speeds = @{}
$script:pausedPids = @()
$script:isPaused = $false
$script:stopping = $false
$script:previewUrl = ""
$script:previewMemoryCache = @{}
$script:previewCacheTtlHours = 72
$script:previewCacheDir = Join-Path $env:LOCALAPPDATA "VideoDownloader\preview-cache"
$script:previewPrefetchQueue = [Collections.Generic.List[string]]::new()
$script:previewPrefetchQueued = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$script:previewPrefetchJob = $null
$script:previewPrefetchUrl = ""
New-Item -ItemType Directory -Path $script:previewCacheDir -Force | Out-Null
Remove-VdExpiredPreviewCache -CacheDir $script:previewCacheDir -TtlHours $script:previewCacheTtlHours
$script:dependencyJob = $null
$script:lastClipboardText = ""
$script:dragItem = $null
$script:dragStart = [System.Windows.Point]::new(0,0)
$script:sessionStarted = $null
$script:sessionSuccess = 0
$script:sessionFailed = 0
$script:sessionBytes = [int64]0
$script:transferredByUrl = [Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
$script:sessionTotal = 0
$script:lastFiles = @{}
$script:exitFromTray = $false
$script:restoringSession = $false
$script:sessionPath = Join-Path $env:LOCALAPPDATA "VideoDownloader\session.json"
$script:historyPath = Join-Path $env:LOCALAPPDATA "VideoDownloader\successful-downloads.jsonl"
$script:lastSessionSnapshot = ""
$script:lastSessionCheck = [DateTime]::MinValue
$script:activeEngine = $null
$script:recoveredEngine = $null
$script:lastSessionError = ""
$script:checkpointReadOnly = $false
$script:lastAddedCount = 0

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

function Import-CompletedUrlsToHistory([object[]]$Items) {
    try {
        $known = Read-VdDownloadHistory -Path $script:historyPath
        foreach ($item in $Items) {
            $url = [string]$item.Url
            if ($item.Status -eq "Completed" -and (Is-ValidUrl $url) -and -not $known.Contains($url)) {
                Add-VdSuccessfulDownloadUrl -Path $script:historyPath -Url $url -UnknownCompletionTime
                [void]$known.Add($url)
            }
        }
    } catch {
        Log-Line ("WARNING: Could not import previously completed URLs into download history: " + $_.Exception.Message)
    }
}

function Show-DuplicateDownloadDialog([string]$Url,[int]$Index,[int]$Total,$CompletedAt) {
    [xml]$xaml = Get-Content -LiteralPath $duplicateDialogXaml -Raw -Encoding UTF8
    $reader = [System.Xml.XmlNodeReader]::new($xaml)
    $dialog = [System.Windows.Markup.XamlReader]::Load($reader)
    $dialog.Owner = $window
    $dialog.Tag = "Cancel"
    $dialog.FindName("CounterLabel").Text = "Previously downloaded ($Index / $Total)"
    $dialog.FindName("DuplicateUrl").Text = $Url
    $dialog.FindName("CompletedAtLabel").Text = if ($CompletedAt) {
        "Last successful download: " + ([DateTimeOffset]$CompletedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
    } else { "Last successful download: time unavailable (legacy history)" }
    foreach ($name in @("YesButton","NoButton","YesAllButton","NoAllButton")) {
        $button = $dialog.FindName($name)
        $button.Tag = switch ($name) {
            "YesButton" { "Yes" }
            "NoButton" { "No" }
            "YesAllButton" { "YesAll" }
            "NoAllButton" { "NoAll" }
        }
        $button.Add_Click({
            param($sender,$eventArgs)
            $owner = [System.Windows.Window]::GetWindow($sender)
            $owner.Tag = [string]$sender.Tag
            $owner.DialogResult = $true
        })
    }
    [void]$dialog.ShowDialog()
    return [string]$dialog.Tag
}

function Confirm-DuplicateDownloads([string[]]$Urls) {
    $records = Read-VdDownloadHistory -Path $script:historyPath -IncludeTimestamps
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($url in $records.Keys) { [void]$known.Add($url) }
    return Resolve-VdDuplicateDownloadPlan -Urls $Urls -Known $known -LastDownloads $records -Prompt {
        param($url,$index,$total,$completedAt)
        Show-DuplicateDownloadDialog -Url $url -Index $index -Total $total -CompletedAt $completedAt
    }
}

function Get-SessionSettings {
    return [ordered]@{
        Profile = [string]$ProfileCombo.SelectedItem
        Quality = [string]$QualityCombo.SelectedItem
        Codec = [string]$CodecCombo.SelectedItem
        Container = [string]$ContainerCombo.SelectedItem
        RateLimit = [string]$RateLimitCombo.Text
        Threads = [string]$ThreadsBox.Text
        Fragments = [string]$FragmentsCombo.SelectedItem
        FilenameTemplate = [string]$FilenameTemplateBox.Text
        Output = [string]$OutputBox.Text
        Cookies = [string]$CookiesBox.Text
        SubtitleLangs = [string]$SubtitleLangsBox.Text
        WriteSubs = ($WriteSubsCheck.IsChecked -eq $true)
        AutoSubs = ($AutoSubsCheck.IsChecked -eq $true)
        EmbedSubs = ($EmbedSubsCheck.IsChecked -eq $true)
        EmbedThumbnail = ($EmbedThumbnailCheck.IsChecked -eq $true)
        EmbedMetadata = ($EmbedMetadataCheck.IsChecked -eq $true)
        EmbedChapters = ($EmbedChaptersCheck.IsChecked -eq $true)
        Archive = ($ArchiveCheck.IsChecked -eq $true)
        Sponsor = ($SponsorCheck.IsChecked -eq $true)
        AudioOnly = ($AudioOnlyCheck.IsChecked -eq $true)
        WatchClipboard = ($WatchClipboardCheck.IsChecked -eq $true)
    }
}

function Restore-SessionSettings([object]$Settings) {
    if (-not $Settings) { return }
    foreach ($entry in @(
        @($ProfileCombo,"Profile"), @($QualityCombo,"Quality"), @($CodecCombo,"Codec"),
        @($ContainerCombo,"Container"), @($FragmentsCombo,"Fragments")
    )) {
        $combo = $entry[0]
        $value = [string]$Settings[$entry[1]]
        if ($combo.Items.Contains($value)) { $combo.SelectedItem = $value }
    }
    if ($Settings.Contains("RateLimit")) { $RateLimitCombo.Text = [string]$Settings.RateLimit }
    foreach ($entry in @(
        @($ThreadsBox,"Threads"), @($FilenameTemplateBox,"FilenameTemplate"),
        @($OutputBox,"Output"), @($CookiesBox,"Cookies"), @($SubtitleLangsBox,"SubtitleLangs")
    )) {
        if ($Settings.Contains($entry[1])) { $entry[0].Text = [string]$Settings[$entry[1]] }
    }
    foreach ($entry in @(
        @($WriteSubsCheck,"WriteSubs"), @($AutoSubsCheck,"AutoSubs"),
        @($EmbedSubsCheck,"EmbedSubs"), @($EmbedThumbnailCheck,"EmbedThumbnail"),
        @($EmbedMetadataCheck,"EmbedMetadata"), @($EmbedChaptersCheck,"EmbedChapters"),
        @($ArchiveCheck,"Archive"), @($SponsorCheck,"Sponsor"),
        @($AudioOnlyCheck,"AudioOnly"), @($WatchClipboardCheck,"WatchClipboard")
    )) {
        if ($Settings.Contains($entry[1]) -and $Settings[$entry[1]] -is [bool]) {
            $entry[0].IsChecked = $Settings[$entry[1]]
        }
    }
}

function Get-SessionSnapshot {
    $items = @(
        foreach ($item in $script:queue) {
            $status = [string]$item.Status
            if ($status -notin @("Queued","Running","Completed","Failed","Interrupted")) { $status = "Queued" }
            [ordered]@{
                Url = [string]$item.Url
                Status = $status
                # Frequent progress percentages are UI-only; a recovered active item is interrupted.
                DisplayState = if ($status -eq "Running") { "Interrupted" } else { [string]$item.State }
                Path = [string]$item.Path
                OwnedPaths = @($item.OwnedPaths)
            }
        }
    )
    $engineInfo = if ($script:activeEngine) { $script:activeEngine } else { $script:recoveredEngine }
    return [ordered]@{ Queue=$items; Settings=(Get-SessionSettings); Engine=$engineInfo }
}

function Save-SessionCheckpoint {
    if ($script:restoringSession -or $script:checkpointReadOnly) { return }
    $snapshot = Get-SessionSnapshot
    $fingerprint = ConvertTo-Json -InputObject $snapshot -Compress -Depth 12
    if ($fingerprint -eq $script:lastSessionSnapshot) { return }
    try {
        Write-VdSessionCheckpoint -Path $script:sessionPath -Session $snapshot
        $script:lastSessionSnapshot = $fingerprint
        $script:lastSessionError = ""
    } catch {
        $message = "Cannot save session checkpoint: " + $_.Exception.Message
        if ($message -ne $script:lastSessionError) { Log-Line $message; $script:lastSessionError = $message }
    }
}

function Sync-RecoveredEngine {
    if (-not $script:recoveredEngine) { return $true }
    if (Test-VdSessionEngineAlive -Engine $script:recoveredEngine) { return $false }
    $checkpoint = Get-SessionSnapshot
    $checkpoint = Merge-VdSessionResultEvents -Session $checkpoint -EventsPath ([string]$script:recoveredEngine.EventsPath)
    Import-CompletedUrlsToHistory -Items @($checkpoint.Queue)
    $script:restoringSession = $true
    try {
        $script:queue.Clear()
        foreach ($item in @(Get-VdRestoredQueue -Session $checkpoint)) { $script:queue.Add($item) }
        $script:failedUrls.Clear()
        foreach ($item in $script:queue) {
            if ($item.Status -eq "Failed") { [void]$script:failedUrls.Add([string]$item.Url) }
        }
        $script:recoveredEngine = $null
        Reindex-Queue
    } finally { $script:restoringSession = $false }
    Save-SessionCheckpoint
    return $true
}

function Restore-PreviousSession {
    $checkpoint = $null
    try {
        $checkpoint = Read-VdSessionCheckpoint -Path $script:sessionPath
    } catch {
        # Retain broken checkpoints for manual recovery instead of overwriting them.
        $backup = $script:sessionPath + ".invalid-" + (Get-Date -Format "yyyyMMddHHmmss")
        $errorText = $_.Exception.Message
        try { Move-Item -LiteralPath $script:sessionPath -Destination $backup -ErrorAction Stop } catch {
            $script:checkpointReadOnly = $true
            $backup = "Could not back up corrupt checkpoint; automatic saves disabled: " + $_.Exception.Message
        }
        [System.Windows.MessageBox]::Show("Saved session cannot be read. Backup: $backup" + [Environment]::NewLine + $errorText,
            "Video Downloader", [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning) | Out-Null
        return
    }
    if (-not $checkpoint) { return }
    # One-time best-effort migration of successfully downloaded URLs from the
    # previous checkpoint, even when the user declines restoring that queue.
    Import-CompletedUrlsToHistory -Items @($checkpoint.Queue)
    $items = @(Get-VdRestoredQueue -Session $checkpoint)
    if (-not (Test-VdSessionHasPending -Queue $items)) {
        # A detached worker also needs to be guarded when there is no pending queue.
        if (Test-VdSessionEngineAlive -Engine $checkpoint.Engine) {
            $script:recoveredEngine = $checkpoint.Engine
            Log-Line "An earlier downloader is still running. Start is blocked until it finishes."
        }
        return
    }
    $answer = [System.Windows.MessageBox]::Show(
        "An unfinished session with $($items.Count) URL(s) was found. Restore its queue, statuses and download settings? Downloads will NOT start automatically.",
        "Video Downloader", [System.Windows.MessageBoxButton]::YesNo, [System.Windows.MessageBoxImage]::Question)
    if ($answer -ne [System.Windows.MessageBoxResult]::Yes) {
        if (Test-VdSessionEngineAlive -Engine $checkpoint.Engine) {
            $script:recoveredEngine = $checkpoint.Engine
            Log-Line "Queue discarded; previous downloader is still running. New downloads are blocked until it finishes."
        }
        try { Remove-Item -LiteralPath $script:sessionPath -Force -ErrorAction Stop } catch {
            $script:checkpointReadOnly = $true
            [System.Windows.MessageBox]::Show("Could not discard previous session. Automatic checkpoint saving is disabled to protect it: $($_.Exception.Message)", "Video Downloader") | Out-Null
        }
        Save-SessionCheckpoint
        return
    }
    $script:restoringSession = $true
    try {
        Restore-SessionSettings $checkpoint.Settings
        foreach ($item in $items) {
            $script:queue.Add($item)
            Queue-PreviewPrefetch -Url ([string]$item.Url)
            if ($item.Status -eq "Failed") { [void]$script:failedUrls.Add([string]$item.Url) }
        }
        $script:recoveredEngine = $checkpoint.Engine
        Reindex-Queue
    } finally { $script:restoringSession = $false }
    if (-not (Sync-RecoveredEngine)) {
        Log-Line "An engine from the previous GUI instance is still active. New downloads are blocked until it finishes."
    }
    Save-SessionCheckpoint
}

function Reindex-Queue {
    $script:failedUrls.Clear()
    foreach ($item in $script:queue) {
        if ($item.Status -eq "Failed") { [void]$script:failedUrls.Add([string]$item.Url) }
    }
    for ($i=0; $i -lt $script:queue.Count; $i++) {
        $script:queue[$i].Order = $i + 1
    }
    $QueueGrid.Items.Refresh()
    $QueueCount.Text = "$($script:queue.Count) URLs"
    Update-StartState
    Save-SessionCheckpoint
}

function Find-QueueItem([string]$Url) {
    if ([string]::IsNullOrWhiteSpace($Url)) { return $null }
    foreach ($item in $script:queue) {
        if ([string]::Equals([string]$item.Url,$Url,[StringComparison]::Ordinal)) {
            return $item
        }
    }
    return $null
}

function Set-QueueState([string]$Url,[string]$State,[string]$Status="") {
    $item = Find-QueueItem $Url
    if (-not $item) { return }
    if ([string]$item.State -eq $State -and (-not $Status -or $item.Status -eq $Status)) { return }
    $item.State = $State
    if ($Status) { $item.Status = $Status }
    $QueueGrid.Items.Refresh()
    if ($Status -in @("Completed","Failed")) { Save-SessionCheckpoint }
}

function Add-Urls([string[]]$Urls,[switch]$FromClipboard,[switch]$ShowPreview) {
    $script:lastAddedCount = 0
    $existing = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $script:queue) { [void]$existing.Add([string]$item.Url) }

    $newUrls = [Collections.Generic.List[string]]::new()
    foreach ($raw in $Urls) {
        $u = ([string]$raw).Trim()
        if ((Is-ValidUrl $u) -and $existing.Add($u)) { [void]$newUrls.Add($u) }
    }
    if ($newUrls.Count -eq 0) { return }

    $running = $script:proc -and -not $script:proc.HasExited
    $toAdd = @($newUrls.ToArray())
    if ($running) {
        # The history prompt applies to live additions too, before the engine or UI changes.
        try {
            $plan = Confirm-DuplicateDownloads -Urls $toAdd
            if ($plan.Cancelled) { return }
            $toAdd = @($plan.Selected)
            if ($toAdd.Count -eq 0) { return }
            # The dialog is modal but dispatcher timers still run; the engine
            # can finish during user confirmation. In that case retain URLs as
            # Queued for the next Start rather than writing to a deleted file.
            $running = $script:proc -and -not $script:proc.HasExited -and
                -not [string]::IsNullOrWhiteSpace($script:liveQueuePath)
            if ($running) {
                # Only the dedicated ingress is written; never edit engine queue.txt.
                Add-VdLiveQueueUrls -Path $script:liveQueuePath -Urls $toAdd
            }
        } catch {
            [System.Windows.MessageBox]::Show(
                "Could not add URLs to the active download. Queue was not changed: " + $_.Exception.Message,
                "Video Downloader", [System.Windows.MessageBoxButton]::OK,
                [System.Windows.MessageBoxImage]::Warning) | Out-Null
            return
        }
    }

    foreach ($u in $toAdd) {
        $script:queue.Add([pscustomobject]@{
            Order = $script:queue.Count + 1
            Url = $u
            State = "Queued"
            Status = "Queued"
            Path = ""
            OwnedPaths = @()
            Progress = 0.0
            Speed = ""
            Downloaded = ""
            ETA = ""
        })
        # Clipboard links should not sit behind a long backlog of older
        # previews while the engine is already downloading.
        Queue-PreviewPrefetch -Url $u -Priority ([bool]$FromClipboard -and [bool]$running)
    }
    $script:lastAddedCount = $toAdd.Count
    if ($running) {
        foreach ($u in $toAdd) { [void]$script:runUrls.Add([string]$u) }
        $script:sessionTotal += $toAdd.Count
        Update-LiveStats
        Log-Line ("Added $($toAdd.Count) URL(s) to the active download queue.")
    }
    Reindex-Queue
    if ($FromClipboard -and $ShowPreview -and $toAdd.Count -gt 0) {
        $newSelection = Find-QueueItem ([string]$toAdd[0])
        if ($newSelection) {
            $QueueGrid.SelectedItem = $newSelection
            Start-Preview ([string]$newSelection.Url)
        }
    }
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

function Remove-SelectedQueueEntries([object[]]$Items) {
    $rows = @($Items | Where-Object { $null -ne $_ -and $script:queue.Contains($_) })
    if ($rows.Count -eq 0) { return }

    $running = $script:proc -and -not $script:proc.HasExited
    $hasStarted = @($rows | Where-Object {
        $_.Status -in @("Running","Completed","Failed","Interrupted") -or $_.Path
    }).Count -gt 0
    if ($hasStarted) {
        $answer = [System.Windows.MessageBox]::Show(
            "Remove $($rows.Count) selected URL(s) from the queue? Any active downloads for these URLs will be cancelled, and their partial or finished files will be permanently deleted. Other downloads will continue.",
            "Delete downloads", [System.Windows.MessageBoxButton]::YesNo,
            [System.Windows.MessageBoxImage]::Warning)
        if ($answer -ne [System.Windows.MessageBoxResult]::Yes) { return }
    }

    # Explicit output paths are from this URL's Result event or checkpoint.
    # No wildcard/glob deletion; never remove files outside the output root.
    $rootPath = $OutputBox.Text.Trim()
    foreach ($row in $rows) {
        $url = [string]$row.Url
        $paths = @($row.OwnedPaths | Where-Object { $_ })
        if ($paths.Count -eq 0 -and $row.Status -eq "Completed" -and $row.Path) {
            # Backward compatibility: older checkpoints only recorded the media path.
            $paths = @([string]$row.Path)
        }
        try {
            if ($running -and $script:cancelDir) {
                Request-VdCancellation -Directory $script:cancelDir -Url $url
            }
            if ($paths.Count -gt 0) {
                [void](Remove-VdOwnedFiles -OutputRoot $rootPath -Paths $paths)
            }
        } catch {
            [System.Windows.MessageBox]::Show(
                "Could not cancel or delete files for this URL. The row was kept: " + $_.Exception.Message,
                "Remove download failed", [System.Windows.MessageBoxButton]::OK,
                [System.Windows.MessageBoxImage]::Warning) | Out-Null
            continue
        }

        if ($running) {
            [void]$script:removedUrls.Add($url)
            if ($script:runUrls.Remove($url)) {
                $script:sessionTotal = [Math]::Max(0,$script:sessionTotal-1)
                if ($row.Status -eq "Completed") { $script:sessionSuccess = [Math]::Max(0,$script:sessionSuccess-1) }
                if ($row.Status -eq "Failed") { $script:sessionFailed = [Math]::Max(0,$script:sessionFailed-1) }
            }
        }
        [void]$script:failedUrls.Remove($url)
        $download = Find-Download -Slot 0 -Url $url
        if ($download) { [void]$script:downloads.Remove($download) }
        [void]$script:queue.Remove($row)
        [void]$script:transferredByUrl.Remove($url)
    }
    Reindex-Queue
    Update-LiveStats
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

function Find-Download([int]$Slot,[string]$Url="") {
    foreach ($item in $script:downloads) {
        if ($Url) {
            if ([string]::Equals([string]$item.Url,$Url,[StringComparison]::Ordinal)) { return $item }
        } elseif ($item.Slot -eq "T$Slot") { return $item }
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

    $item = Find-Download -Slot $Slot -Url $Url
    $stableStatus = if ($State -eq "Done") { "Completed" }
                    elseif ($RawError -or $State -eq "Error") { "Failed" }
                    else { "Running" }
    if (-not $item) {
        $item = [pscustomobject]@{
            Slot = "T$Slot"
            State = $State
            Status = $stableStatus
            Title = $Title
            Url = $Url
            Path = $Path
            RawError = $RawError
            Progress = 0.0
            Speed = ""
            Downloaded = ""
            ETA = ""
        }
        $script:downloads.Add($item)
    } else {
        $item.State = $State
        $item | Add-Member -NotePropertyName Status -NotePropertyValue $stableStatus -Force
        $item.Slot = "T$Slot"
        if ($Title) { $item.Title = $Title }
        if ($Url) { $item.Url = $Url }
        if ($Path) { $item.Path = $Path }
        if ($RawError) { $item.RawError = $RawError }
    }
    $DownloadGrid.Items.Refresh()
}

function Update-StartState {
    $running = $script:proc -and -not $script:proc.HasExited
    $StartButton.IsEnabled = (-not $running -and (Test-VdSessionHasPending -Queue @($script:queue | ForEach-Object { $_ })))
    $StopButton.IsEnabled = [bool]$running
    $PauseButton.IsEnabled = [bool]$running
    $RetryFailedButton.IsEnabled = (-not $running -and $script:failedUrls.Count -gt 0)
}

function Set-Running([bool]$Running) {
    $StartButton.IsEnabled = (-not $Running -and (Test-VdSessionHasPending -Queue @($script:queue | ForEach-Object { $_ })))
    $StopButton.IsEnabled = $Running
    $PauseButton.IsEnabled = $Running

    foreach ($c in @(
        $OpenListButton,$ClearQueueButton,$MoveUpButton,$MoveDownButton,
        $ProfileCombo,$QualityCombo,$CodecCombo,$ContainerCombo,$RateLimitCombo,$ThreadsBox,
        $FragmentsCombo,$FilenameTemplateBox,$WriteSubsCheck,$AutoSubsCheck,$EmbedSubsCheck,$SubtitleLangsBox,
        $EmbedThumbnailCheck,$EmbedMetadataCheck,$EmbedChaptersCheck,$ArchiveCheck,$SponsorCheck,$AudioOnlyCheck,
        $OutputBox,$PickOutputButton,$CookiesBox,$PickCookiesButton
    )) {
        $c.IsEnabled = -not $Running
    }

    # Keep the queue selectable while downloading so preview/open/copy still work,
    # but freeze queue mutations until the active session finishes.
    $QueueGrid.IsEnabled = $true
    $QueueGrid.AllowDrop = -not $Running
    $queueRemove.IsEnabled = $true
    $RemoveQueueButton.IsEnabled = $true
    $queueRetry.IsEnabled = -not $Running

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
    $script:liveQueuePath = $null
    $script:cancelDir = $null
}

function Update-LiveStats {
    $sum = 0.0
    $active = 0
    foreach ($v in $script:speeds.Values) {
        $sum += [double]$v
        if ([double]$v -gt 0) { $active++ }
    }
    if ($script:isPaused) { $sum = 0; $active = 0 }
    $remaining = @($script:queue | Where-Object { $_.Status -notin @("Completed","Failed") }).Count
    if ($null -ne $tray -and -not $tray.IsDisposed) {
        $running = $script:proc -and -not $script:proc.HasExited
        $tooltip = Get-VdTrayStatusText -Success $script:sessionSuccess -Failed $script:sessionFailed `
            -Remaining $remaining -SpeedBytes $sum -Running ([bool]$running) `
            -Paused ([bool]$script:isPaused) -HasSession ($null -ne $script:sessionStarted) -Stopped ([bool]$script:stopping)
        if ($tray.Text -cne $tooltip) { $tray.Text = $tooltip }
    }
    $done = $script:sessionSuccess + $script:sessionFailed
    $HeaderStats.Text = "$active active · $(Format-Speed $sum) · $done/$($script:sessionTotal)"

    if ($script:sessionStarted) {
        $elapsed = (Get-Date) - $script:sessionStarted
        $avg = 0.0
        if ($elapsed.TotalSeconds -gt 0) { $avg = $script:sessionBytes / $elapsed.TotalSeconds }
        $SessionStats.Text = "Session: $($script:sessionSuccess) ok · $($script:sessionFailed) failed · Downloaded $(Format-Bytes $script:sessionBytes) · Speed $(Format-Speed $sum) · $($elapsed.ToString('hh\:mm\:ss')) · avg $(Format-Speed $avg)"
    }
}

function Update-TransferBytes([string]$Url,[long]$Bytes) {
    $previous = if ($script:transferredByUrl.ContainsKey($Url)) { $script:transferredByUrl[$Url] } else { 0L }
    if ($Bytes -gt $previous) {
        $script:sessionBytes += $Bytes-$previous
        $script:transferredByUrl[$Url] = $Bytes
    }
}

function Set-RowProgress([string]$Url,[double]$Percent,[string]$Speed,[string]$Downloaded,[string]$ETA) {
    $items = @((Find-QueueItem $Url),(Find-Download -Slot 0 -Url $Url))
    foreach ($item in $items) {
        if (-not $item) { continue }
        $item.Progress = [Math]::Clamp($Percent,0,100)
        $item.Speed = $Speed
        if ($Downloaded) { $item.Downloaded = $Downloaded }
        $item.ETA = $ETA
    }
    $QueueGrid.Items.Refresh()
    $DownloadGrid.Items.Refresh()
}

function Tail-Log {
    if (-not $script:runLog -or -not (Test-Path $script:runLog)) { return }
    try { $lines = @(Get-Content $script:runLog -Encoding UTF8) } catch { return }
    for ($i=$script:logLines; $i -lt $lines.Count; $i++) { Log-Line $lines[$i] }
    $script:logLines = $lines.Count
}

function Tail-Events {
    if (-not $script:runEvents -or -not (Test-Path $script:runEvents)) { return }
    try { $lines = @(Read-VdEventLines -Path $script:runEvents -Cursor $script:eventCursor) } catch { return }

    for ($i=0; $i -lt $lines.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
        try { $event = $lines[$i] | ConvertFrom-Json -ErrorAction Stop } catch { continue }

        if ($event.Url -and $null -ne $script:removedUrls -and $script:removedUrls.Contains([string]$event.Url)) {
            if ($event.Kind -eq "Result" -and $event.OwnedPaths) {
                try { [void](Remove-VdOwnedFiles -OutputRoot $OutputBox.Text.Trim() -Paths @($event.OwnedPaths)) }
                catch { Log-Line ("Could not clean late result for removed URL: " + $_.Exception.Message) }
            }
            continue
        }
        if ($event.Kind -eq "Event") {
            $slot = [int]$event.Slot
            switch ($event.EventType) {
                "Started" {
                    $u = [string]$event.Url
                    $script:lastFiles.Remove($slot)
                    $script:speeds[$slot] = 0
                    Set-Download -Slot $slot -State "Starting" -Url $u
                    Set-RowProgress -Url $u -Percent 0 -Speed "" -Downloaded "0 B" -ETA ""
                    Set-QueueState $u "Starting" "Running"
                }
                "Title" {
                    $u = [string]$event.Url
                    Set-Download -Slot $slot -State "Preparing" -Title ([string]$event.Title) -Url $u
                    Set-QueueState $u "Preparing"
                }
                "Progress" {
                    $u = [string]$event.Url
                    Set-Download -Slot $slot -State "Downloading" -Url $u
                    Set-QueueState $u ("{0:N1}%" -f [double]$event.Percent)
                    $script:speeds[$slot] = Convert-SpeedTextToBytes ([string]$event.Speed)
                    Update-TransferBytes -Url $u -Bytes ([long]$event.TransferredBytes)
                    Set-RowProgress -Url $u -Percent ([double]$event.Percent) -Speed ([string]$event.Speed) -Downloaded ("$(Format-Bytes ([long]$event.DownloadedBytes)) / $(Format-Bytes ([long]$event.TotalBytes))") -ETA ([string]$event.ETA)
                }
                "Merge" {
                    $u = [string]$event.Url
                    $script:speeds[$slot] = 0
                    Set-RowProgress -Url $u -Percent 100 -Speed "" -Downloaded "" -ETA ""
                    Set-Download -Slot $slot -State "Merging..." -Url $u
                    Set-QueueState $u "Merging"
                }
                "Retry" {
                    $u = [string]$event.Url
                    $script:speeds[$slot] = 0
                    Set-RowProgress -Url $u -Percent 0 -Speed "" -Downloaded "" -ETA ""
                    Set-Download -Slot $slot -State "Retrying..." -Url $u
                    Set-QueueState $u "Retrying"
                }
                "File" {
                    $path = [string]$event.Text
                    $u = [string]$event.Url
                    $script:speeds[$slot] = 0
                    Set-RowProgress -Url $u -Percent 100 -Speed "" -Downloaded "" -ETA ""
                    $script:lastFiles[$slot] = $path
                    Set-Download -Slot $slot -State "Finalizing..." -Url $u -Path $path
                    Set-QueueState $u "Finalizing"
                }
                "Error" {
                    $u = [string]$event.Url
                    Set-Download -Slot $slot -State "Error" -Url $u -RawError ([string]$event.Text)
                    Set-QueueState $u "Error"
                }
            }
        } elseif ($event.Kind -eq "Result") {
            if ($event.Cancelled -eq $true) { continue }
            $slot = [int]$event.Slot
            $script:speeds[$slot] = 0
            Update-TransferBytes -Url ([string]$event.Url) -Bytes ([long]$event.TransferredBytes)
            Set-RowProgress -Url ([string]$event.Url) -Percent $(if ($event.Success) {100} else {0}) -Speed "" -Downloaded "" -ETA ""

            if ([bool]$event.Success) {
                $script:sessionSuccess++
                $path = [string]$event.Path
                if (-not $path -and $script:lastFiles.ContainsKey($slot)) { $path = [string]$script:lastFiles[$slot] }
                $size = [int64]$event.FileSize
                if ($size -le 0 -and $path -and (Test-Path -LiteralPath $path -PathType Leaf)) {
                    try { $size = (Get-Item -LiteralPath $path).Length } catch {}
                }
                $u = [string]$event.Url
                Set-Download -Slot $slot -State "Done" -Url $u -Path $path
                $completedItem = Find-QueueItem $u
                if ($completedItem) {
                    $completedItem.Path = $path
                    $completedItem | Add-Member -NotePropertyName OwnedPaths -NotePropertyValue @($event.OwnedPaths) -Force
                }
                Set-QueueState $u "Done" "Completed"
            } else {
                $script:sessionFailed++
                $u = [string]$event.Url
                if ($u -and -not $script:failedUrls.Contains($u)) { [void]$script:failedUrls.Add($u) }
                $friendly = [string]$event.FriendlyError
                if (-not $friendly) { $friendly = Get-FriendlyDownloadError ([string]$event.Error) }
                Set-Download -Slot $slot -State $friendly -Url $u -RawError ([string]$event.Error)
                Set-QueueState $u $friendly "Failed"
            }

            $done = $script:sessionSuccess + $script:sessionFailed
            $pct = 0
            if ($script:sessionTotal -gt 0) { $pct = [int](100*$done/$script:sessionTotal) }
            $OverallProgress.Value = [Math]::Min(100,[Math]::Max(0,$pct))
            Update-LiveStats
        }
    }

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

    if (-not (Sync-RecoveredEngine)) {
        [System.Windows.MessageBox]::Show("The downloader from the previous session is still running. Wait until it exits before starting more downloads.","Video Downloader") | Out-Null
        return
    }
    $items = $OverrideUrls
    if ($null -eq $items) { $items = @(Get-VdResumeUrls -Queue @($script:queue | ForEach-Object { $_ })) }
    if ($items.Count -eq 0) { return }

    # Resolve decisions before mutating Queue or creating any engine temp files.
    # Cancel/X leaves the entire queue unchanged, and never starts a download.
    try {
        $plan = Confirm-DuplicateDownloads -Urls @($items)
    } catch {
        [System.Windows.MessageBox]::Show(
            "Cannot check previous downloads. Nothing was started or removed. " + $_.Exception.Message,
            "Download history error", [System.Windows.MessageBoxButton]::OK,
            [System.Windows.MessageBoxImage]::Warning) | Out-Null
        return
    }
    if ($plan.Cancelled) { return }
    foreach ($url in @($plan.Remove)) {
        $queueItem = Find-QueueItem ([string]$url)
        if ($queueItem) { [void]$script:queue.Remove($queueItem) }
    }
    if (@($plan.Remove).Count -gt 0) { Reindex-Queue }
    $items = @($plan.Selected)
    if ($items.Count -eq 0) { return }

    foreach ($u in $items) { Queue-PreviewPrefetch -Url ([string]$u) }

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

    $cookie = $CookiesBox.Text.Trim()
    if ($cookie -and -not (Test-Path $cookie -PathType Leaf)) {
        [System.Windows.MessageBox]::Show("Cookies file not found.","Video Downloader") | Out-Null
        return
    }

    Clean-Temp
    $script:runDir = Join-Path ([IO.Path]::GetTempPath()) ("ytdl-gui-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:runDir -Force | Out-Null
    Set-Content (Join-Path $script:runDir "owner.pid") $PID -Encoding ASCII

    $queuePath = Join-Path $script:runDir "queue.txt"
    $script:runLog = Join-Path $script:runDir "run.log"
    $script:runEvents = Join-Path $script:runDir "events.jsonl"
    $script:liveQueuePath = Join-Path $script:runDir "incoming.txt"
    $script:cancelDir = Join-Path $script:runDir "cancel"
    [void][IO.Directory]::CreateDirectory($script:cancelDir)
    [IO.File]::WriteAllText($script:liveQueuePath, "")
    Set-Content $queuePath $items -Encoding UTF8

    $script:logLines = 0
    $script:eventLines = 0
    $script:eventCursor = @{ Offset=0L }
    $script:failedUrls.Clear()
    $script:speeds.Clear()
    $script:lastFiles.Clear()
    $script:downloads.Clear()
    $script:removedUrls.Clear()
    $script:runUrls.Clear()
    foreach ($u in $items) { [void]$script:runUrls.Add([string]$u) }
    $LogBox.Clear()
    $script:stopping = $false
    $script:isPaused = $false
    $script:pausedPids = @()
    $PauseButton.Content = "Pause"

    $script:sessionStarted = Get-Date
    $script:sessionSuccess = 0
    $script:sessionFailed = 0
    $script:sessionBytes = [int64]0
    $script:transferredByUrl.Clear()
    $script:sessionTotal = $items.Count
    $OverallProgress.Value = 0
    # Explicit retry may refer to a Downloads row removed from Queue.
    # History confirmation has already succeeded; persist every selected URL.
    Add-Urls $items
    foreach ($u in $items) { Set-QueueState ([string]$u) "Starting" "Running" }
    Save-SessionCheckpoint
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
        "-NoProgress","-Log",$script:runLog,"-EventFile",$script:runEvents,"-LiveQueueFile",$script:liveQueuePath,"-CancelDir",$script:cancelDir,"-ResultDir",$resultDir,
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

    if ($cookie) { $args += @("-Cookies",(Resolve-Path $cookie).Path) }

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
        $script:activeEngine = [ordered]@{ Pid=$script:proc.Id; StartedUtc=$script:proc.StartTime.ToUniversalTime().ToString("o"); EventsPath=$script:runEvents }
        Save-SessionCheckpoint
        Set-Running $true
        Log-Line "Started $($items.Count) URL(s) into $dest"
    } catch {
        $script:proc = $null
        $script:activeEngine = $null
        foreach ($u in $items) { Set-QueueState ([string]$u) "Interrupted" "Interrupted" }
        Save-SessionCheckpoint
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
    $script:speeds.Clear()
    foreach ($item in @($script:queue) + @($script:downloads)) { $item.Speed = ""; $item.ETA = "" }
    $QueueGrid.Items.Refresh()
    $DownloadGrid.Items.Refresh()

    Set-Running $false
    $PauseButton.Content = "Pause"
    $script:isPaused = $false
    $script:pausedPids = @()

    Update-LiveStats
    $elapsed = if ($script:sessionStarted) { (Get-Date)-$script:sessionStarted } else { [TimeSpan]::Zero }
    $summary = "$($script:sessionSuccess) ok · $($script:sessionFailed) failed · $(Format-Bytes $script:sessionBytes) · $($elapsed.ToString('hh\:mm\:ss'))"
    $remainingQueued = @($script:queue | Where-Object { $_.Status -eq "Queued" }).Count
    if ($remainingQueued -gt 0) { $summary += " · $remainingQueued queued for next Start" }

    if ($script:stopping) {
        $HeaderStatus.Text = "● Stopped"
        $HeaderStatus.Foreground = [System.Windows.Media.Brushes]::Orange
    } elseif ($script:sessionFailed -eq 0 -and $code -eq 0) {
        $HeaderStatus.Text = if ($remainingQueued -gt 0) { "● Completed · $remainingQueued queued" } else { "● Completed" }
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
    foreach ($item in $script:queue) {
        if ($item.Status -eq "Running") { Set-QueueState ([string]$item.Url) "Interrupted" "Interrupted" }
    }
    $script:activeEngine = $null
    Save-SessionCheckpoint
    Clean-Temp
}

function Get-CachedPreview([string]$Url) {
    if ([string]::IsNullOrWhiteSpace($Url)) { return $null }

    if ($script:previewMemoryCache.ContainsKey($Url)) {
        $entry = $script:previewMemoryCache[$Url]
        try {
            $savedAt = ([DateTimeOffset]$entry.SavedAtUtc).UtcDateTime

            if (([DateTime]::UtcNow - $savedAt).TotalHours -lt $script:previewCacheTtlHours) {
                return $entry
            }
        } catch {}

        $script:previewMemoryCache.Remove($Url)
    }

    $entry = Read-VdPreviewCache -CacheDir $script:previewCacheDir -Url $Url -TtlHours $script:previewCacheTtlHours
    if ($entry) {
        $script:previewMemoryCache[$Url] = $entry
        return $entry
    }

    return $null
}

function Save-CachedPreview([string]$Url,[object]$Preview) {
    if ([string]::IsNullOrWhiteSpace($Url) -or -not $Preview) { return }
    try {
        $record = Write-VdPreviewCache -CacheDir $script:previewCacheDir -Url $Url -Preview $Preview
        if ($record) { $script:previewMemoryCache[$Url] = $record }
    } catch {
        Log-Line ("Preview cache write failed: " + $_.Exception.Message)
    }
}

function Show-PreviewResult([object]$Result,[bool]$FromCache=$false) {
    if (-not $Result) { return }

    $PreviewTitle.Text = if ($Result.Title) { [string]$Result.Title } else { "Untitled" }

    $duration = ""
    if ($Result.Duration -gt 0) {
        $ts = [TimeSpan]::FromSeconds([double]$Result.Duration)
        $duration = if ($ts.TotalHours -ge 1) { $ts.ToString("hh\:mm\:ss") } else { $ts.ToString("mm\:ss") }
    }

    $meta = @()
    if ($Result.PlaylistTitle) { $meta += ("Playlist: " + [string]$Result.PlaylistTitle) }
    if ($Result.Uploader) { $meta += [string]$Result.Uploader }
    if ($duration) { $meta += $duration }
    if ($Result.Extractor) { $meta += [string]$Result.Extractor }
    if ($FromCache) { $meta += "cache" }
    $PreviewMeta.Text = $meta -join " · "

    $formatParts = @()
    if ($Result.MaxHeight -gt 0) {
        $res = [string]$Result.MaxHeight + "p"
        if ($Result.MaxFps -gt 0) { $res += ("{0:N0}" -f [double]$Result.MaxFps) }
        $formatParts += $res
    }
    if ($Result.Codecs) { $formatParts += ("Codecs: " + [string]$Result.Codecs) }
    if ($Result.DynamicRange) { $formatParts += ("HDR: " + [string]$Result.DynamicRange) }
    $PreviewFormats.Text = $formatParts -join " · "

    if ($Result.ThumbnailBase64) {
        $stream = $null
        try {
            $bytes = [Convert]::FromBase64String([string]$Result.ThumbnailBase64)
            $stream = [IO.MemoryStream]::new($bytes,$false)

            $bitmap = [System.Windows.Media.Imaging.BitmapImage]::new()
            $bitmap.BeginInit()
            $bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
            $bitmap.CreateOptions = [System.Windows.Media.Imaging.BitmapCreateOptions]::IgnoreColorProfile
            $bitmap.DecodePixelWidth = 720
            $bitmap.StreamSource = $stream
            $bitmap.EndInit()
            $bitmap.Freeze()

            $PreviewImage.Source = $bitmap
        } catch {
            $PreviewImage.Source = $null
            Log-Line ("Preview thumbnail decode failed: " + $_.Exception.Message)
        } finally {
            if ($stream) { $stream.Dispose() }
        }
    } else {
        $PreviewImage.Source = $null
        if ($Result.ThumbnailError) {
            Log-Line ("Preview thumbnail unavailable: " + [string]$Result.ThumbnailError)
        }
    }
}

function Queue-PreviewPrefetch {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [bool]$Priority = $false
    )

    if (-not (Is-ValidUrl $Url)) { return }
    if (Get-CachedPreview $Url) { return }
    if ([string]::Equals($script:previewPrefetchUrl,$Url,[StringComparison]::Ordinal)) { return }

    if ($script:previewPrefetchQueued.Contains($Url)) {
        if ($Priority) {
            [void]$script:previewPrefetchQueue.Remove($Url)
            $script:previewPrefetchQueue.Insert(0,$Url)
        }
        return
    }

    if ($Priority) { $script:previewPrefetchQueue.Insert(0,$Url) }
    else { [void]$script:previewPrefetchQueue.Add($Url) }

    [void]$script:previewPrefetchQueued.Add($Url)
    Start-NextPreviewPrefetch
}

function Start-NextPreviewPrefetch {
    # A completed/failed job is still owned by Complete-PreviewPrefetch until its
    # result is received and cached. Never skip over it here.
    if ($script:previewPrefetchJob) { return }

    while ($script:previewPrefetchQueue.Count -gt 0) {
        $url = [string]$script:previewPrefetchQueue[0]
        $script:previewPrefetchQueue.RemoveAt(0)
        [void]$script:previewPrefetchQueued.Remove($url)

        if (Get-CachedPreview $url) { continue }

        $cookie = $CookiesBox.Text.Trim()
        $exe = $ytDlp
        $modulePath = $previewCacheModule
        $script:previewPrefetchUrl = $url
        $script:previewPrefetchJob = Start-ThreadJob -ArgumentList $modulePath,$exe,$url,$cookie -ScriptBlock {
            param($modulePath,$exe,$url,$cookie)
            Import-Module $modulePath -Force
            Get-VdPreviewData -YtDlpPath $exe -Url $url -CookiePath $cookie
        }
        break
    }
}

function Complete-PreviewPrefetch {
    if (-not $script:previewPrefetchJob) {
        Start-NextPreviewPrefetch
        return
    }

    $state = [string]$script:previewPrefetchJob.State
    if ($state -eq "NotStarted" -or $state -eq "Running") { return }

    $url = [string]$script:previewPrefetchUrl
    try {
        if ($state -ne "Completed") {
            $reason = $script:previewPrefetchJob.JobStateInfo.Reason
            if ($reason) { throw $reason }
            throw "Preview prefetch ended with state: $state"
        }

        $r = Receive-Job $script:previewPrefetchJob -ErrorAction Stop | Select-Object -Last 1
        if (-not $r) { throw "Preview prefetch returned no result" }

        if ([bool]$r.Success) {
            Save-CachedPreview -Url $url -Preview $r
            if ([string]::Equals($script:previewUrl,$url,[StringComparison]::Ordinal)) {
                $cached = Get-CachedPreview $url
                if ($cached) { Show-PreviewResult -Result $cached -FromCache $true }
            }
        } else {
            $message = [string]$r.Error
            if ([string]::IsNullOrWhiteSpace($message)) { $message = "yt-dlp could not prefetch preview" }
            Log-Line ("Preview prefetch failed for ${url}: " + $message)
            if ([string]::Equals($script:previewUrl,$url,[StringComparison]::Ordinal)) {
                $PreviewTitle.Text = "Preview unavailable"
                $PreviewMeta.Text = $message
                $PreviewFormats.Text = ""
                $PreviewImage.Source = $null
            }
        }
    } catch {
        $message = $_.Exception.Message
        Log-Line ("Preview prefetch failed for ${url}: " + $message)
        if ([string]::Equals($script:previewUrl,$url,[StringComparison]::Ordinal)) {
            $PreviewTitle.Text = "Preview unavailable"
            $PreviewMeta.Text = $message
            $PreviewFormats.Text = ""
            $PreviewImage.Source = $null
        }
    } finally {
        try { Remove-Job $script:previewPrefetchJob -Force -ErrorAction SilentlyContinue } catch {}
        $script:previewPrefetchJob = $null
        $script:previewPrefetchUrl = ""
        Start-NextPreviewPrefetch
    }
}

function Start-Preview([string]$Url) {
    if (-not (Is-ValidUrl $Url)) { return }

    $script:previewUrl = $Url
    $cached = Get-CachedPreview $Url
    if ($cached) {
        Show-PreviewResult -Result $cached -FromCache $true
        return
    }

    $PreviewTitle.Text = "Preparing preview..."
    $PreviewMeta.Text = "Background prefetch · " + $Url
    $PreviewFormats.Text = ""
    $PreviewImage.Source = $null

    # Clicking does not launch a second metadata request. It only promotes this
    # URL inside the already-running background prefetch queue.
    Queue-PreviewPrefetch -Url $Url -Priority $true
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

function Handle-StartupArgs([string[]]$Arguments=$StartupArgs) {
    foreach ($arg in @($Arguments)) {
        if ([string]::IsNullOrWhiteSpace($arg)) { continue }

        if ($arg.StartsWith("videodownloader://",[StringComparison]::OrdinalIgnoreCase)) {
            $payload = $arg.Substring("videodownloader://".Length).TrimStart('/')
            try { $payload = [Uri]::UnescapeDataString($payload) } catch {}
            if (Is-ValidUrl $payload) { Add-Urls @($payload) }
            continue
        }

        if (Test-Path -LiteralPath $arg -PathType Leaf) {
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
$tray.Text = Get-VdTrayStatusText
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
    if ($item -and $item.Path -and (Test-Path -LiteralPath $item.Path -PathType Leaf)) { Start-Process -FilePath $item.Path }
}

function Open-SelectedFolder {
    $item = $DownloadGrid.SelectedItem
    if ($item -and $item.Path -and (Test-Path -LiteralPath $item.Path -PathType Leaf)) {
        Start-Process explorer.exe -ArgumentList @("/select," + [char]34 + $item.Path + [char]34)
    } elseif (Test-Path $OutputBox.Text) {
        Start-Process explorer.exe -ArgumentList @($OutputBox.Text)
    }
}

function Copy-SelectedPath {
    $item = $DownloadGrid.SelectedItem
    if ($item -and $item.Path) { [System.Windows.Clipboard]::SetText([string]$item.Path) }
}

$PasteClipboardButton.Add_Click({
    try {
        if (-not [System.Windows.Clipboard]::ContainsText()) {
            $ClipboardHint.Text = "Clipboard contains no text."
            return
        }
        $urls = @(Extract-Urls ([System.Windows.Clipboard]::GetText()))
        if ($urls.Count -eq 0) {
            $ClipboardHint.Text = "Clipboard contains no valid HTTP/HTTPS URLs."
            return
        }
        Add-Urls $urls -FromClipboard -ShowPreview
        $ClipboardHint.Text = if ($script:lastAddedCount -gt 0) {
            "Added $($script:lastAddedCount) URL(s) from clipboard."
        } else { "No new URLs added." }
    } catch {
        [System.Windows.MessageBox]::Show("Cannot read the clipboard: $($_.Exception.Message)", "Video Downloader") | Out-Null
    }
})

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

$RemoveQueueButton.Add_Click({ Remove-SelectedQueueEntries -Items @($QueueGrid.SelectedItems) })

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
    $items = @($script:queue | Where-Object { $_.Status -eq "Failed" } | ForEach-Object { [string]$_.Url })
    if ($items.Count -gt 0) { Start-Download $items }
})

$QueueGrid.Add_SelectionChanged({
    $item = $QueueGrid.SelectedItem
    if ($item -and $item.Url -and $item.Url -ne $script:previewUrl) { Start-Preview ([string]$item.Url) }
})

$QueueGrid.Add_PreviewMouseLeftButtonDown({
    param($s,$e)
    if ($script:proc -and -not $script:proc.HasExited) {
        $script:dragItem = $null
        return
    }
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
    if ($script:proc -and -not $script:proc.HasExited) {
        $e.Handled = $true
        return
    }
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
    if ($script:proc -and -not $script:proc.HasExited) {
        $e.Handled = $true
        return
    }
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
$queueRemove.Add_Click({
    $item=$QueueGrid.SelectedItem
    if ($item) { Remove-SelectedQueueEntries -Items @($item) }
})

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
$trayExit.Add_Click({
    $script:exitFromTray = $true
    try { $window.Close() } finally { $script:exitFromTray = $false }
})
$TrayButton.Add_Click({ $window.Hide() })
$window.Add_StateChanged({ if ($window.WindowState -eq [System.Windows.WindowState]::Minimized) { $window.Hide() } })

$timer = [System.Windows.Threading.DispatcherTimer]::new()
$timer.Interval = [TimeSpan]::FromMilliseconds(350)
$timer.Add_Tick({
    while ($null -ne ($message = $script:ipcServer.Take())) {
        try {
            $arguments = @(ConvertFrom-Json -InputObject $message -ErrorAction Stop)
            $window.Show()
            $window.WindowState = [System.Windows.WindowState]::Normal
            [void]$window.Activate()
            Handle-StartupArgs -Arguments $arguments
        } catch { Log-Line ("Could not add forwarded URLs: " + $_.Exception.Message) }
    }
    Tail-Events
    Tail-Log
    Complete-PreviewPrefetch
    Complete-DependencyJob

    if ($script:proc) {
        try { if ($script:proc.HasExited) { Finish-Download } } catch {}
    }
    if (((Get-Date) - $script:lastSessionCheck).TotalSeconds -ge 1) {
        $script:lastSessionCheck = Get-Date
        [void](Sync-RecoveredEngine)
        Save-SessionCheckpoint
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
                Add-Urls $values -FromClipboard
                $ClipboardHint.Text = if ($script:lastAddedCount -gt 0) {
                    "Auto-added $($script:lastAddedCount) URL(s) from clipboard."
                } else { "No new URLs added from clipboard." }
            } else {
                $ClipboardHint.Text = "URL detected in clipboard. Enable Watch clipboard to auto-add."
            }
        }
    } catch {}
})

$window.Add_Closing({
    param($s,$e)
    if (-not $script:exitFromTray) {
        $choice = [System.Windows.MessageBox]::Show(
            "Hide Video Downloader in the tray?`n`nYes — hide in tray`nNo — exit application`nCancel — keep window open",
            "Video Downloader", [System.Windows.MessageBoxButton]::YesNoCancel, [System.Windows.MessageBoxImage]::Question)
        if ($choice -eq [System.Windows.MessageBoxResult]::Yes) {
            $e.Cancel = $true
            $window.Hide()
            return
        }
        if ($choice -ne [System.Windows.MessageBoxResult]::No) {
            $e.Cancel = $true
            return
        }
    }
    # The engine may have finished since the last timer tick; collect its final
    # Result events before saving the last checkpoint and deleting temp files.
    if ($script:proc) { Tail-Events }
    if ($script:proc -and -not $script:proc.HasExited) {
        $answer = [System.Windows.MessageBox]::Show("A download is running. Stop it and exit?","Video Downloader",[System.Windows.MessageBoxButton]::YesNo,[System.Windows.MessageBoxImage]::Warning)
        if ($answer -ne [System.Windows.MessageBoxResult]::Yes) {
            $e.Cancel = $true
            return
        }
        if ($script:isPaused) { Resume-ProcessTree }
        Tail-Events
        try { $script:proc.Kill($true); [void]$script:proc.WaitForExit(1500) } catch {}
        Tail-Events
    }

    foreach ($item in $script:queue) {
        if ($item.Status -eq "Running") { Set-QueueState ([string]$item.Url) "Interrupted" "Interrupted" }
    }
    # If a failed kill left the worker alive, keep its PID guard and event file.
    $workerSurvived = ($script:proc -and -not $script:proc.HasExited)
    if ($workerSurvived) { $script:recoveredEngine = $script:activeEngine }
    $script:activeEngine = $null
    Save-SessionCheckpoint

    try {
        if ($script:previewPrefetchJob) { Stop-Job $script:previewPrefetchJob -ErrorAction SilentlyContinue; Remove-Job $script:previewPrefetchJob -Force -ErrorAction SilentlyContinue }
        if ($script:dependencyJob) { Stop-Job $script:dependencyJob -ErrorAction SilentlyContinue; Remove-Job $script:dependencyJob -Force -ErrorAction SilentlyContinue }
    } catch {}

    $tray.Visible = $false
    $tray.Dispose()
    $timer.Stop()
    $clipboardTimer.Stop()
    if (-not $workerSurvived) { Clean-Temp }
})

$script:restoringSession = $true
Reindex-Queue
$script:restoringSession = $false
Restore-PreviousSession
Refresh-Dependencies
Handle-StartupArgs
$timer.Start()
$clipboardTimer.Start()
try { Start-VdGuiMessageLoop -Window $window }
finally {
    if ($script:ipcServer) { $script:ipcServer.Dispose() }
    if ($script:guiMutex) {
        try { $script:guiMutex.ReleaseMutex() } catch {}
        $script:guiMutex.Dispose()
    }
}
