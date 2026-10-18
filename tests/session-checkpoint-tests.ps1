#requires -Version 7.0
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) "lib/VideoDownloader.Session.psm1") -Force

function Assert-Vd([bool]$Value,[string]$Message) {
    if (-not $Value) { throw $Message }
}
function Assert-Throws([scriptblock]$Action,[string]$Message) {
    $threw = $false
    try { & $Action } catch { $threw = $true }
    Assert-Vd $threw $Message
}

$dir = Join-Path ([IO.Path]::GetTempPath()) ("vd-session-test-" + [guid]::NewGuid().ToString("N"))
$path = Join-Path $dir "session.json"
[void][IO.Directory]::CreateDirectory($dir)
try {
    Assert-Vd ($null -eq (Read-VdSessionCheckpoint -Path $path)) "Missing checkpoint must be null"
    $session = [ordered]@{
        Queue = @(
            [ordered]@{ Url="https://example.com/a"; Status="Completed"; DisplayState="Done"; Path="C:\a.mp4" },
            [ordered]@{ Url="https://example.com/b"; Status="Running"; DisplayState="75.3%"; Path="" },
            [ordered]@{ Url="https://example.com/c"; Status="Queued"; DisplayState="Queued"; Path="" },
            [ordered]@{ Url="https://example.com/d"; Status="Failed"; DisplayState="Network error"; Path="" }
        )
        Settings = [ordered]@{
            Quality = "1080"
            Container = "MP4"
            Output = "C:\Downloads"
            FilenameTemplate = "%(title)s.%(ext)s"
            Cookies = "C:\private cookies.txt"
            AudioOnly = $false
        }
        Engine = $null
    }
    Write-VdSessionCheckpoint -Path $path -Session $session
    $loaded = Read-VdSessionCheckpoint -Path $path
    Assert-Vd ($loaded.Version -eq 1 -and $loaded.Queue.Count -eq 4) "Checkpoint roundtrip lost items"
    Assert-Vd ($loaded.Settings.Quality -eq "1080" -and $loaded.Settings.Cookies -eq "C:\private cookies.txt") "Settings roundtrip lost values"
    Assert-Vd (Test-VdSessionHasPending -Queue $loaded.Queue) "Incomplete checkpoint not detected"
    $restored = @(Get-VdRestoredQueue -Session $loaded)
    Assert-Vd ($restored[0].Status -eq "Completed" -and $restored[0].State -eq "Done") "Completed item changed during restore"
    Assert-Vd ($restored[1].Status -eq "Interrupted" -and $restored[1].State -eq "Interrupted") "Running item must recover as interrupted"
    Assert-Vd ($restored[2].Status -eq "Queued" -and $restored[3].Status -eq "Failed") "Queued/failed statuses lost"
    $remaining = @(Get-VdResumeUrls -Queue $restored)
    Assert-Vd ($remaining.Count -eq 3 -and $remaining[0] -eq "https://example.com/b") "Already completed item selected for download"

    $events = Join-Path $dir "events.jsonl"
    $lines = @(
        '{"Kind":"Event","EventType":"Progress","Url":"https://example.com/c"}',
        '{"Kind":"Result","Url":"https://example.com/b","Success":true,"Path":"C:\\b.mp4"}',
        'truncated-{',
        '{"Kind":"Result","Url":"https://example.com/c","Success":false,"FriendlyError":"404"}',
        '{"Kind":"Result","Url":"https://unknown.invalid/","Success":true}'
    )
    [IO.File]::WriteAllLines($events,$lines)
    $merged = Merge-VdSessionResultEvents -Session $loaded -EventsPath $events
    Assert-Vd ($merged.Queue[1].Status -eq "Completed" -and $merged.Queue[1].Path -eq "C:\b.mp4") "Completed event not reconciled"
    Assert-Vd ($merged.Queue[2].Status -eq "Failed") "Failure event not reconciled"
    Assert-Vd ((@(Get-VdResumeUrls -Queue @(Get-VdRestoredQueue -Session $merged))).Count -eq 2) "Recovered results should not re-download"

    # URL paths are case-sensitive: a result must not complete the wrong row.
    $caseSession = [ordered]@{
        Queue = @(
            [ordered]@{ Url="https://example.com/Video"; Status="Queued"; DisplayState="Queued"; Path="" },
            [ordered]@{ Url="https://example.com/video"; Status="Queued"; DisplayState="Queued"; Path="" }
        )
        Settings = [ordered]@{}
        Engine = $null
    }
    [IO.File]::WriteAllText($events,'{"Kind":"Result","Url":"https://example.com/Video","Success":true}')
    $caseMerged = Merge-VdSessionResultEvents -Session $caseSession -EventsPath $events
    Assert-Vd ($caseMerged.Queue[0].Status -eq "Completed" -and $caseMerged.Queue[1].Status -eq "Queued") "Result URL matching must be case-sensitive"

    Write-VdSessionCheckpoint -Path $path -Session $merged
    Assert-Vd (@(Get-ChildItem -LiteralPath $dir -Filter "*.tmp").Count -eq 0) "Atomic write left temp files"
    $readAgain = Read-VdSessionCheckpoint -Path $path
    Assert-Vd ($readAgain.Queue.Count -eq 4) "Replacement checkpoint invalid"

    $allDone = @([pscustomobject]@{Url="https://example.com/a";Status="Completed"})
    Assert-Vd (-not (Test-VdSessionHasPending -Queue $allDone)) "Fully completed session should not trigger restore"
    Assert-Vd (-not (Test-VdSessionHasPending -Queue @())) "Empty queue should not trigger restore"
    Assert-Vd (-not (Test-VdSessionEngineAlive -Engine $null)) "Null worker must not be treated as running"
    Assert-Vd (-not (Test-VdSessionEngineAlive -Engine @{ Pid=$PID; StartedUtc="2000-01-01T00:00:00Z" })) "PID reuse must not produce false running worker"
    if ($IsWindows) {
        $selfProcess = Get-Process -Id $PID
        $selfEngine = @{ Pid=$PID; StartedUtc=$selfProcess.StartTime.ToUniversalTime().ToString("o") }
        Assert-Vd (Test-VdSessionEngineAlive -Engine $selfEngine) "Running pwsh process with matching creation timestamp must be detected"
        $session.Engine = $selfEngine
        Write-VdSessionCheckpoint -Path $path -Session $session
        $roundtrip = Read-VdSessionCheckpoint -Path $path
        Assert-Vd (Test-VdSessionEngineAlive -Engine $roundtrip.Engine) "JSON date conversion must preserve the worker PID guard across restart"
        $selfProcess.Dispose()
    }

    [IO.File]::WriteAllText($path,'{"Version":999,"Queue":[],"Settings":{}}')
    Assert-Throws { Read-VdSessionCheckpoint -Path $path | Out-Null } "Unsupported version must fail"
    [IO.File]::WriteAllText($path,'{"Version":1,"Queue":[{"Url":"file:///etc/passwd","Status":"Queued"}],"Settings":{}}')
    Assert-Throws { Read-VdSessionCheckpoint -Path $path | Out-Null } "Non-http URL must fail"
    [IO.File]::WriteAllText($path,'{"Version":1,"Queue":[{"Url":"https://example.com","Status":"Queued"},{"Url":"https://example.com","Status":"Queued"}],"Settings":{}}')
    Assert-Throws { Read-VdSessionCheckpoint -Path $path | Out-Null } "Duplicate URL must fail"
    [IO.File]::WriteAllText($path,'{"Version":1,"Queue":[],"Settings":{}}')
    Assert-Vd ($null -ne (Read-VdSessionCheckpoint -Path $path)) "Empty valid session must load"
    # Retry can start from Downloads after the user cleared Queue. Exercise the
    # same insertion/state/snapshot functions used before engine launch.
    $tokens=$null; $parseErrors=$null
    $guiAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'ytdl-manager-gui.ps1'),[ref]$tokens,[ref]$parseErrors)
    foreach ($name in @('Add-Urls','Find-QueueItem','Set-QueueState','Get-SessionSnapshot')) {
        $node=$guiAst.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
        . ([scriptblock]::Create($node.Extent.Text))
    }
    function Is-ValidUrl($url) { return $url -like 'https://*' }
    function Queue-PreviewPrefetch {}
    function Reindex-Queue {}
    function Get-SessionSettings { return @{} }
    function Save-SessionCheckpoint {}
    $gridItems=[pscustomobject]@{}; $gridItems | Add-Member ScriptMethod Refresh {}
    $QueueGrid=[pscustomobject]@{Items=$gridItems}
    $script:queue=[Collections.ObjectModel.ObservableCollection[object]]::new()
    $script:proc=$null; $script:activeEngine=$null; $script:recoveredEngine=$null
    Add-Urls @('https://example.com/retry')
    Add-Urls @('https://example.com/retry')
    Set-QueueState 'https://example.com/retry' 'Starting' 'Running'
    Write-VdSessionCheckpoint -Path $path -Session (Get-SessionSnapshot)
    $retrySession=Read-VdSessionCheckpoint -Path $path
    Assert-Vd ($retrySession.Queue.Count -eq 1 -and $retrySession.Queue[0].Status -eq 'Running') 'Retry must be checkpointed exactly once after queue removal'
    Assert-Vd (@(Get-VdResumeUrls -Queue (Get-VdRestoredQueue -Session $retrySession)).Count -eq 1) 'Retry must be resumable after restart'
    $startNode=$guiAst.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Start-Download'},$true)
    Assert-Vd ($startNode.Extent.Text.IndexOf('Add-Urls $items') -gt $startNode.Extent.Text.IndexOf('if ($plan.Cancelled) { return }')) 'Queue insertion must follow duplicate confirmation'

    [IO.File]::WriteAllText($path,'truncated json')
    Assert-Throws { Read-VdSessionCheckpoint -Path $path | Out-Null } "Corrupt checkpoint must fail, not silently reset"
}
finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Session checkpoint persistence/recovery tests passed."
