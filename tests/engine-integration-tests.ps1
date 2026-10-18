#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
function Assert([bool]$value,[string]$message) { if (-not $value) { throw $message } }
$dir=Join-Path ([IO.Path]::GetTempPath()) ('vd-engine-integration-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dir)
$oldLocalAppData=$env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA=Join-Path $dir 'profile'
    Copy-Item (Join-Path $root 'ytdl-manager-v8.ps1'),(Join-Path $root 'yt-dlp.exe') $dir
    Copy-Item (Join-Path $root 'lib') (Join-Path $dir 'lib') -Recurse
    # Local file downloads exercise the actual bundled executable without network.
    Set-Content (Join-Path $dir 'yt-dlp.conf') '--enable-file-urls'
    $sources=@()
    foreach ($name in @('first','second')) {
        $path=Join-Path $dir "$name.mp4"
        [IO.File]::WriteAllBytes($path,[byte[]]::new(2MB))
        $sources+=([uri]$path).AbsoluteUri
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
    # The file:// fixture is intentionally outside supported history URLs.
    $parallelSources=@()
    foreach ($index in 1..8) {
        $path=Join-Path $dir "parallel$index.mp4"
        [IO.File]::WriteAllBytes($path,[byte[]]::new(256KB))
        $parallelSources+=([uri]$path).AbsoluteUri
    }
    Set-Content $queue $parallelSources
    $parallelEvents=Join-Path $dir 'parallel.jsonl'
    & pwsh -NoProfile -File (Join-Path $dir 'ytdl-manager-v8.ps1') -In $queue -Out $out -Threads 8 -RateLimit 100K -NoProgress -Retries 0 -EventFile $parallelEvents
    Assert ($LASTEXITCODE -eq 0) 'Eight-worker download failed'
    $parallelResults=@(Get-Content $parallelEvents | Where-Object {$_} | ConvertFrom-Json | Where-Object Kind -eq Result)
    Assert ($parallelResults.Count -eq 8 -and @($parallelResults | Where-Object { -not $_.Success }).Count -eq 0) 'Every scheduled URL must complete above the default ThreadJob throttle'
    Assert (@(Get-Content $queue | Where-Object {$_}).Count -eq 0) 'Successful engine exit must leave no pending URLs'
    Assert (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'VideoDownloader/successful-downloads.jsonl'))) 'Local test fixtures must not enter real URL history'

    # Cancel one of two active native yt-dlp processes, leaving the other running.
    Import-Module (Join-Path $dir 'lib/VideoDownloader.Cancellation.psm1') -Force
    $cancelSource=Join-Path $dir 'to-cancel.mp4'
    $keepSource=Join-Path $dir 'to-keep.mp4'
    [IO.File]::WriteAllBytes($cancelSource,[byte[]]::new(2MB))
    [IO.File]::WriteAllBytes($keepSource,[byte[]]::new(128KB))
    $cancelUrl=([uri]$cancelSource).AbsoluteUri
    $keepUrl=([uri]$keepSource).AbsoluteUri
    $cancelQueue=Join-Path $dir 'cancel-queue.txt'
    Set-Content -LiteralPath $cancelQueue @($cancelUrl,$keepUrl)
    $cancelEvents=Join-Path $dir 'cancel-events.jsonl'
    $cancelSignals=Join-Path $dir 'cancel-markers'
    [void][IO.Directory]::CreateDirectory($cancelSignals)
    $cancelOutput=Join-Path $dir 'cancel-outputs'
    $testEngine=Join-Path $dir 'ytdl-manager-v8.ps1'
    $run=Start-ThreadJob -ArgumentList $testEngine,$cancelQueue,$cancelOutput,$cancelEvents,$cancelSignals -ScriptBlock {
        param($engine,$queueFile,$outDir,$eventsFile,$signals)
        & pwsh -NoProfile -File $engine -In $queueFile -Out $outDir -Threads 2 -NoProgress -Retries 0 -RateLimit 100K -EventFile $eventsFile -CancelDir $signals
        if ($LASTEXITCODE -ne 0) { throw "Live cancellation engine exited with $LASTEXITCODE" }
    }
    try {
        $observed=$false
        $deadline=(Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and $run.State -eq 'Running') {
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
        Assert (-not $target.Path -and @($target.OwnedPaths).Count -eq 0) 'Cancelled target must own no finished files'
        Assert (Test-Path -LiteralPath $other.Path) 'Uncancelled file must exist in output directory'
        Assert (@($other.OwnedPaths).Count -gt 0) 'Successful cancellable job must report owned output paths'
        $cancelStageDirs=@(Get-ChildItem -LiteralPath (Split-Path -Parent $cancelSignals) -Directory -Filter 'download-*')
        Assert ($cancelStageDirs.Count -eq 0) 'Cancelled and completed staged job directories must be removed'
        [void](Remove-VdOwnedFiles -OutputRoot $cancelOutput -Paths @($other.OwnedPaths))
        Assert (-not (Test-Path $other.Path)) 'Explicit remove after completion must delete only that URL files'
    } finally {
        if ($run.State -eq 'Running') { Stop-Job $run -ErrorAction SilentlyContinue }
        Remove-Job $run -Force -ErrorAction SilentlyContinue
    }
} finally {
    $env:LOCALAPPDATA=$oldLocalAppData
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Offline real yt-dlp engine integration tests passed.'
