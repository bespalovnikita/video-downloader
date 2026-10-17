#requires -Version 7.0
# Separate append-only ingress file: unlike the engine's working queue.txt,
# this file is never rewritten by the downloader while processing results.
$ErrorActionPreference = "Stop"

function Invoke-VdLiveQueueLocked {
    param([Parameter(Mandatory)][scriptblock]$Action)
    $mutex = [Threading.Mutex]::new($false, "Local\VideoDownloader.LiveQueue")
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds(10)) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw "Timed out waiting for the live queue lock." }
        & $Action
    } finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Test-VdLiveQueueUrl([string]$Url) {
    if ([string]::IsNullOrWhiteSpace($Url) -or $Url -match '[\r\n]') { return $false }
    $uri = $null
    return [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -and
        $uri.Scheme -in @("http", "https") -and -not [string]::IsNullOrWhiteSpace($uri.Host)
}

function Add-VdLiveQueueUrls {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Urls
    )
    $distinct = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($url in $Urls) {
        if (-not (Test-VdLiveQueueUrl $url)) { throw "Invalid URL in live queue." }
        if ($seen.Add($url)) { [void]$distinct.Add($url) }
    }
    if ($distinct.Count -eq 0) { return }
    Invoke-VdLiveQueueLocked {
        $dir = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
        [void][IO.Directory]::CreateDirectory($dir)
        $stream = [IO.FileStream]::new($Path,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try {
            $data = [Text.UTF8Encoding]::new($false).GetBytes(($distinct.ToArray() -join "`n") + "`n")
            $stream.Write($data,0,$data.Length)
            $stream.Flush($true)
        } finally { $stream.Dispose() }
    }
}

function Read-VdLiveQueueUrls {
    param([Parameter(Mandatory)][string]$Path)
    Invoke-VdLiveQueueLocked {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
        foreach ($line in [IO.File]::ReadAllLines($Path,[Text.Encoding]::UTF8)) {
            if (-not (Test-VdLiveQueueUrl $line)) { throw "Live queue contains an invalid or incomplete URL." }
            $line
        }
    }
}

Export-ModuleMember -Function Add-VdLiveQueueUrls,Read-VdLiveQueueUrls
