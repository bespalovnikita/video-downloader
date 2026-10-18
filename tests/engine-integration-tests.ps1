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
} finally {
    $env:LOCALAPPDATA=$oldLocalAppData
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Offline real yt-dlp engine integration tests passed.'
