#requires -Version 7.0
param([string]$YtDlpPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
function Assert([bool]$value,[string]$message) { if (-not $value) { throw $message } }
. (Join-Path $PSScriptRoot 'fixtures/OfflineMediaServer.ps1')
$dir=Join-Path ([IO.Path]::GetTempPath()) ('vd-engine-integration-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dir)
$oldLocalAppData=$env:LOCALAPPDATA
$server=$null
try {
    $env:LOCALAPPDATA=Join-Path $dir 'profile'
    if (-not $YtDlpPath) { $YtDlpPath=Join-Path $root 'yt-dlp.exe' }
    if (-not (Test-Path -LiteralPath $YtDlpPath -PathType Leaf)) { throw "Missing integration executable: $YtDlpPath" }
    Copy-Item -LiteralPath (Join-Path $root 'ytdl-manager-v8.ps1') -Destination $dir
    Copy-Item -LiteralPath $YtDlpPath -Destination (Join-Path $dir 'yt-dlp.exe')
    Copy-Item (Join-Path $root 'lib') (Join-Path $dir 'lib') -Recurse
    # Loopback media exercises the real executable without external requests or
    # depending on ambient config (which the engine deliberately ignores).
    $server=Start-VdOfflineMediaServer -Root $dir
    function Get-FixtureUrl([string]$Path) {
        return ([Uri]::new([Uri]$server.BaseUrl,[Uri]::EscapeDataString([IO.Path]::GetFileName($Path)))).AbsoluteUri
    }
    $sources=@()
    foreach ($name in @('first','second')) {
        $path=Join-Path $dir "$name.mp4"
        [IO.File]::WriteAllBytes($path,[byte[]]::new(2MB))
        $sources+=Get-FixtureUrl $path
    }
    $queue=Join-Path $dir 'queue.txt'; Set-Content $queue $sources
    $events=Join-Path $dir 'events.jsonl'
    $out=Join-Path $dir 'downloads'
    & pwsh -NoProfile -File (Join-Path $dir 'ytdl-manager-v8.ps1') -In $queue -Out $out -Threads 1 -NoProgress -Retries 0 -EventFile $events
    Assert ($LASTEXITCODE -eq 0) 'Offline engine download failed'
    $messages=@(Get-Content -LiteralPath $events | Where-Object { $_ } | ConvertFrom-Json)
    $results=@($messages|Where-Object Kind -eq Result)
    Assert ($results.Count -eq 2 -and @($results|Where-Object { -not $_.Success }).Count -eq 0) 'Engine must complete both URLs'
    foreach ($result in $results) {
        $progress=@($messages|Where-Object { $_.EventType -eq 'Progress' -and $_.Url -ceq $result.Url })
        Assert ($progress.Count -gt 1) 'Engine must emit real live progress despite --print/quiet'
        Assert (($progress|Select-Object -Last 1).Percent -eq 100) 'Progress must reach 100%'
        Assert ($result.FileSize -eq 2MB -and (Test-Path -LiteralPath $result.Path)) 'Default bracket filename must preserve path and file size'
        Assert ($result.TransferredBytes -eq 2MB) 'Repeated final progress must not double-count transferred bytes'
    }
    $parallelSources=@()
    foreach ($index in 1..8) {
        $path=Join-Path $dir "parallel$index.mp4"
        [IO.File]::WriteAllBytes($path,[byte[]]::new(256KB))
        $parallelSources+=Get-FixtureUrl $path
    }
    Set-Content $queue $parallelSources
    $parallelEvents=Join-Path $dir 'parallel.jsonl'
    $itemFolders=Join-Path $dir 'item-folders.json'
    @{Version=1;Items=@(@{Url=$parallelSources[0];Folder='123'},@{Url=$parallelSources[1];Folder='Канал 456'})} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $itemFolders -Encoding utf8
    & pwsh -NoProfile -File (Join-Path $dir 'ytdl-manager-v8.ps1') -In $queue -Out $out -Threads 8 -RateLimit 100K -NoProgress -Retries 0 -EventFile $parallelEvents -ItemFoldersFile $itemFolders
    Assert ($LASTEXITCODE -eq 0) 'Eight-worker download failed'
    $parallelResults=@(Get-Content $parallelEvents | Where-Object {$_} | ConvertFrom-Json | Where-Object Kind -eq Result)
    Assert ($parallelResults.Count -eq 8 -and @($parallelResults | Where-Object { -not $_.Success }).Count -eq 0) 'Every scheduled URL must complete above the default ThreadJob throttle'
    foreach($result in $parallelResults) {
        $expectedRoot=if($result.Url -ceq $parallelSources[0]){Join-Path $out '123'}elseif($result.Url -ceq $parallelSources[1]){Join-Path $out 'Канал 456'}else{$out}
        Assert ($result.OutputRoot -eq $expectedRoot -and [IO.Path]::GetDirectoryName($result.Path) -eq $expectedRoot -and (Test-Path -LiteralPath $result.Path)) 'A mixed queue must route each source into its own folder and keep ordinary URLs at the base'
    }
    Assert (@(Get-Content $queue | Where-Object {$_}).Count -eq 0) 'Successful engine exit must leave no pending URLs'
    Import-Module (Join-Path $dir 'lib/VideoDownloader.DownloadHistory.psm1') -Force
    $history=Read-VdDownloadHistory -Path (Join-Path $env:LOCALAPPDATA 'VideoDownloader/successful-downloads.jsonl')
    Assert ($history.Count -eq 10) 'Every completed loopback fixture must enter only the isolated test history'

    # Cancel one of two active native yt-dlp processes, leaving the other running.
    Import-Module (Join-Path $dir 'lib/VideoDownloader.Cancellation.psm1') -Force
    $cancelSource=Join-Path $dir 'to-cancel.mp4'
    $keepSource=Join-Path $dir 'to-keep.mp4'
    [IO.File]::WriteAllBytes($cancelSource,[byte[]]::new(2MB))
    [IO.File]::WriteAllBytes($keepSource,[byte[]]::new(128KB))
    $cancelUrl=Get-FixtureUrl $cancelSource
    $keepUrl=Get-FixtureUrl $keepSource
    $cancelQueue=Join-Path $dir 'cancel-queue.txt'
    Set-Content -LiteralPath $cancelQueue @($cancelUrl,$keepUrl)
    $cancelEvents=Join-Path $dir 'cancel-events.jsonl'
    $cancelSignals=Join-Path $dir 'cancel-markers'
    [void][IO.Directory]::CreateDirectory($cancelSignals)
    $cancelOutput=Join-Path $dir 'cancel-outputs'
    $cancelFolders=Join-Path $dir 'cancel-folders.json'
    @{Version=1;Items=@(@{Url=$cancelUrl;Folder='Отменяемая'},@{Url=$keepUrl;Folder='Соседняя'})} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $cancelFolders -Encoding utf8
    $cancelRoot=Join-Path $cancelOutput 'Отменяемая';$keepRoot=Join-Path $cancelOutput 'Соседняя'
    [void][IO.Directory]::CreateDirectory($cancelRoot)
    $sentinel=Join-Path $cancelRoot 'existing.txt';[IO.File]::WriteAllText($sentinel,'KEEP EXISTING')
    $testEngine=Join-Path $dir 'ytdl-manager-v8.ps1'
    $run=Start-ThreadJob -ArgumentList $testEngine,$cancelQueue,$cancelOutput,$cancelEvents,$cancelSignals,$cancelFolders -ScriptBlock {
        param($engine,$queueFile,$outDir,$eventsFile,$signals,$foldersFile)
        & pwsh -NoProfile -File $engine -In $queueFile -Out $outDir -Threads 2 -NoProgress -Retries 0 -RateLimit 100K -EventFile $eventsFile -CancelDir $signals -ItemFoldersFile $foldersFile
        if ($LASTEXITCODE -ne 0) { throw "Live cancellation engine exited with $LASTEXITCODE" }
    }
    try {
        $observed=$false
        $deadline=(Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and $run.State -in @('NotStarted','Running')) {
            if (Test-Path -LiteralPath $cancelEvents) {
                $seen=@(Get-Content $cancelEvents -ErrorAction SilentlyContinue | Where-Object { $_ -match '"EventType":"Started"' -and $_ -match 'to-cancel' })
                if ($seen.Count -gt 0) { $observed=$true; break }
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not $observed) {
            $jobOutput=@(Receive-Job $run -Keep -ErrorAction SilentlyContinue | Out-String)
            $jobError=@($run.ChildJobs | ForEach-Object { $_.Error | Out-String })
            $seenEvents=if (Test-Path $cancelEvents) { (Get-Content -LiteralPath $cancelEvents -Raw) } else { '(missing events file)' }
            throw ("Engine did not begin targeted download. Job state: $($run.State). Output: $jobOutput; Error: $jobError; Events: $seenEvents")
        }
        Request-VdCancellation -Directory $cancelSignals -Url $cancelUrl
        [void]($run | Wait-Job -Timeout 90)
        Assert ($run.State -eq 'Completed') 'Cancelling one URL should not stall the engine'
        Receive-Job $run -ErrorAction Stop | Out-Null
        $cancelResults=@(Get-Content $cancelEvents | Where-Object { $_ } | ConvertFrom-Json | Where-Object Kind -eq Result)
        Assert ($cancelResults.Count -eq 2) 'Expected a terminal result for both jobs'
        $target=@($cancelResults | Where-Object { $_.Url -ceq $cancelUrl })[0]
        $other=@($cancelResults | Where-Object { $_.Url -ceq $keepUrl })[0]
        Assert ($target.Cancelled -eq $true -and -not $target.Success) 'Targeted URL must report cancellation, not failure/success'
        Assert ($other.Success -eq $true -and -not $other.Cancelled) 'Neighbouring URL must finish despite cancellation'
        Assert ($target.OutputRoot -eq $cancelRoot -and $other.OutputRoot -eq $keepRoot -and [IO.Path]::GetDirectoryName($other.Path) -eq $keepRoot) 'Cancellation and successful Results must retain their separate subscription output roots'
        Assert (-not $target.Path -and @($target.OwnedPaths).Count -eq 0) 'Cancelled target must own no finished files'
        Assert (Test-Path -LiteralPath $other.Path) 'Uncancelled file must exist in output directory'
        Assert (@($other.OwnedPaths).Count -gt 0) 'Successful cancellable job must report owned output paths'
        $cancelStageDirs=@(Get-ChildItem -LiteralPath (Split-Path -Parent $cancelSignals) -Directory -Filter 'download-*')
        Assert ($cancelStageDirs.Count -eq 0) 'Cancelled and completed staged job directories must be removed'
        [void](Remove-VdOwnedFiles -OutputRoot $other.OutputRoot -Paths @($other.OwnedPaths))
        Assert (-not (Test-Path $other.Path)) 'Explicit remove after completion must delete only that URL files'
        Assert ([IO.File]::ReadAllText($sentinel) -ceq 'KEEP EXISTING') 'Cancelling or removing one subscription must preserve existing files in sibling folders'
    } finally {
        if ($run.State -eq 'Running') { Stop-Job $run -ErrorAction SilentlyContinue }
        Remove-Job $run -Force -ErrorAction SilentlyContinue
    }
} finally {
    if ($server) { $server.Dispose() }
    $env:LOCALAPPDATA=$oldLocalAppData
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Offline real yt-dlp engine integration tests passed.'
