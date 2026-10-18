#requires -Version 7.0
# Durable, UI-independent session checkpoints. No WPF dependency.
$ErrorActionPreference = "Stop"

function Test-VdSessionUrl([string]$Url) {
    $uri = $null
    return [Uri]::TryCreate($Url,[UriKind]::Absolute,[ref]$uri) -and
        $uri.Scheme -in @("http","https") -and -not [string]::IsNullOrWhiteSpace($uri.Host)
}

function Read-VdSessionCheckpoint {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -gt 16MB) { throw "Session checkpoint exceeds the 16 MiB safety limit." }
    $session = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    if ($null -eq $session -or $session -isnot [System.Collections.IDictionary] -or $session.Version -ne 1) {
        throw "Unsupported or malformed session checkpoint version."
    }
    if ($session.Queue -isnot [array] -or $session.Queue.Count -gt 100000 -or
        $session.Settings -isnot [System.Collections.IDictionary]) {
        throw "Invalid session checkpoint structure."
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $session.Queue) {
        if ($item -isnot [System.Collections.IDictionary] -or
            -not (Test-VdSessionUrl ([string]$item.Url)) -or
            [string]$item.Status -notin @("Queued","Running","Completed","Failed","Interrupted") -or
            -not $seen.Add([string]$item.Url)) {
            throw "Invalid or duplicate URL/state in session checkpoint."
        }
    }
    return $session
}

function Write-VdSessionCheckpoint {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Session
    )
    $dir = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    [void][IO.Directory]::CreateDirectory($dir)
    $snapshot = [ordered]@{
        Version = 1
        SavedAtUtc = [DateTime]::UtcNow.ToString("o")
        Queue = @($Session.Queue)
        Settings = $Session.Settings
        Engine = $Session.Engine
    }
    $json = ConvertTo-Json -InputObject $snapshot -Depth 12 -Compress
    $temp = Join-Path $dir ([IO.Path]::GetFileName($Path) + "." + [guid]::NewGuid().ToString("N") + ".tmp")
    try {
        [IO.File]::WriteAllText($temp,$json,[Text.UTF8Encoding]::new($false))
        # Same-directory rename protects the previous checkpoint from partial writes.
        [IO.File]::Move($temp,$Path,$true)
    }
    finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
    }
}

function Get-VdRestoredQueue {
    param([Parameter(Mandatory)][object]$Session)
    $order = 0
    foreach ($item in @($Session.Queue)) {
        $order++
        $status = [string]$item.Status
        if ($status -eq "Running") { $status = "Interrupted" }
        $display = [string]$item.DisplayState
        if ($status -eq "Completed") { $display = "Done" }
        elseif ($status -eq "Interrupted" -or $display -match '^\d+(?:[.,]\d+)?%$') { $display = "Interrupted" }
        elseif (-not $display) { $display = $status }
        [pscustomobject]@{
            Order = $order
            Url = [string]$item.Url
            State = $display
            Status = $status
            Path = [string]$item.Path
            OwnedPaths = @($item.OwnedPaths | Where-Object { $_ })
            Progress = if ($status -eq "Completed") { 100.0 } else { 0.0 }
            Speed = ""
            Downloaded = ""
            ETA = ""
        }
    }
}

function Get-VdResumeUrls {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Queue)
    return @($Queue | Where-Object { $_.Status -ne "Completed" } | ForEach-Object { [string]$_.Url })
}

function Test-VdSessionHasPending {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Queue)
    return @($Queue | Where-Object { $_.Status -ne "Completed" }).Count -gt 0
}

function Test-VdSessionEngineAlive {
    param([object]$Engine)
    if ($null -eq $Engine -or -not $Engine.Pid -or -not $Engine.StartedUtc) { return $false }
    $proc = $null
    try {
        $proc = Get-Process -Id ([int]$Engine.Pid) -ErrorAction Stop
        if ($proc.ProcessName -ne "pwsh") { return $false }
        $recorded = ([DateTimeOffset]$Engine.StartedUtc).UtcDateTime
        return [Math]::Abs(($proc.StartTime.ToUniversalTime() - $recorded).TotalSeconds) -lt 3
    } catch { return $false }
    finally { if ($proc) { $proc.Dispose() } }
}

function Merge-VdSessionResultEvents {
    param(
        [Parameter(Mandatory)][object]$Session,
        [string]$EventsPath
    )
    if ([string]::IsNullOrWhiteSpace($EventsPath) -or -not (Test-Path -LiteralPath $EventsPath -PathType Leaf)) {
        return $Session
    }
    $byUrl = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($item in $Session.Queue) { $byUrl[[string]$item.Url] = $item }
    # Read append-only JSONL events. An incomplete tail line must not destroy the session.
    foreach ($line in [IO.File]::ReadLines($EventsPath)) {
        if (-not $line) { continue }
        try { $event = ConvertFrom-Json -InputObject $line -AsHashtable -ErrorAction Stop } catch { continue }
        if ($event.Kind -ne "Result") { continue }
        $url = [string]$event.Url
        if (-not $byUrl.ContainsKey($url)) { continue }
        $item = $byUrl[$url]
        if ($event.Success -eq $true) {
            $item.Status = "Completed"
            $item.DisplayState = "Done"
            $item.Path = [string]$event.Path
            $item.OwnedPaths = @($event.OwnedPaths | Where-Object { $_ })
        } elseif ($event.Cancelled -eq $true) {
            $item.Status = "Interrupted"
            $item.DisplayState = "Cancelled"
        } elseif ($item.Status -ne "Completed") {
            $item.Status = "Failed"
            $item.DisplayState = if ($event.FriendlyError) { [string]$event.FriendlyError } else { "Failed" }
        }
    }
    return $Session
}

Export-ModuleMember -Function Read-VdSessionCheckpoint,Write-VdSessionCheckpoint,Get-VdRestoredQueue,Get-VdResumeUrls,Test-VdSessionHasPending,Test-VdSessionEngineAlive,Merge-VdSessionResultEvents
