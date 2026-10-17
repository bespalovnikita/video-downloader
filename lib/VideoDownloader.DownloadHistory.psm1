#requires -Version 7.0
# Append-only history of successful downloads; independent of session checkpoints.
$ErrorActionPreference = "Stop"

function Test-VdHistoryUrl([string]$Url) {
    $uri = $null
    return [Uri]::TryCreate($Url,[UriKind]::Absolute,[ref]$uri) -and
        $uri.Scheme -in @("http","https") -and -not [string]::IsNullOrWhiteSpace($uri.Host)
}

function Invoke-VdHistoryLocked {
    param([Parameter(Mandatory)][scriptblock]$Action)
    $mutex = [Threading.Mutex]::new($false,"Local\VideoDownloader.SuccessHistory")
    $acquired = $false
    try {
        try {
            $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds(15))
        } catch [Threading.AbandonedMutexException] {
            $acquired = $true
        }
        if (-not $acquired) { throw "Timed out waiting for the success-history lock." }
        & $Action
    } finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Read-VdDownloadHistory {
    param([Parameter(Mandatory)][string]$Path,[switch]$IncludeTimestamps)
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $records = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    Invoke-VdHistoryLocked {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
        $lineNumber = 0
        # Always dispose reader/stream, including when parsing an invalid record.
        $stream = [IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        $reader = [IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true)
        try {
            while (-not $reader.EndOfStream) {
                $line = $reader.ReadLine()
                $lineNumber++
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                try {
                    $entry = ConvertFrom-Json -InputObject $line -AsHashtable -ErrorAction Stop
                    if ($entry.Version -ne 1 -or -not (Test-VdHistoryUrl ([string]$entry.Url))) {
                        throw "Invalid success history record"
                    }
                    [void]$known.Add([string]$entry.Url)
                    $completed = $null
                    if ($entry.CompletedAtUtc) {
                        $completed = [DateTimeOffset]$entry.CompletedAtUtc
                    }
                    $url = [string]$entry.Url
                    if (-not $records.ContainsKey($url) -or ($completed -and
                        (-not $records[$url].CompletedAtUtc -or $completed -gt $records[$url].CompletedAtUtc))) {
                        $records[$url] = [pscustomobject]@{ Url=$url; CompletedAtUtc=$completed }
                    }
                } catch {
                    # Fail closed: never silently continue with an incomplete history.
                    throw "Success history is damaged at line $lineNumber in '$Path': $($_.Exception.Message)"
                }
            }
        } finally {
            $reader.Dispose()
        }
    }
    if ($IncludeTimestamps) { return ,$records }
    return ,$known
}

function Add-VdSuccessfulDownloadUrl {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Url,[switch]$UnknownCompletionTime)
    if (-not (Test-VdHistoryUrl $Url)) { throw "Invalid successful download URL." }
    $entry = ConvertTo-Json -InputObject ([ordered]@{
        Version = 1
        Url = $Url
        CompletedAtUtc = if ($UnknownCompletionTime) { $null } else { [DateTime]::UtcNow.ToString("o") }
    }) -Compress
    Invoke-VdHistoryLocked {
        $dir = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
        [void][IO.Directory]::CreateDirectory($dir)
        # Stream the record without rewriting the accumulated history; flush to disk.
        $stream = [IO.FileStream]::new($Path,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($entry + "`n")
            $stream.Write($bytes,0,$bytes.Length)
            $stream.Flush($true)
        } finally { $stream.Dispose() }
    }
}

function Resolve-VdDuplicateDownloadPlan {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Urls,
        [Parameter(Mandatory)][Collections.Generic.HashSet[string]]$Known,
        [Parameter(Mandatory)][scriptblock]$Prompt,
        [Collections.Generic.Dictionary[string,object]]$LastDownloads
    )
    $selected = [Collections.Generic.List[string]]::new()
    $removed = [Collections.Generic.List[string]]::new()
    $matches = @($Urls | Where-Object { $Known.Contains($_) })
    $matchIndex = 0
    $apply = ""
    foreach ($url in $Urls) {
        if (-not $Known.Contains($url)) {
            [void]$selected.Add($url)
            continue
        }
        $matchIndex++
        $completedAt = if ($LastDownloads -and $LastDownloads.ContainsKey($url)) { $LastDownloads[$url].CompletedAtUtc } else { $null }
        $decision = if ($apply) { $apply } else { & $Prompt $url $matchIndex $matches.Count $completedAt }
        switch ([string]$decision) {
            "Yes" { [void]$selected.Add($url) }
            "No" { [void]$removed.Add($url) }
            "YesAll" { [void]$selected.Add($url); $apply = "Yes" }
            "NoAll" { [void]$removed.Add($url); $apply = "No" }
            "Cancel" { return [pscustomobject]@{ Cancelled=$true; Selected=@(); Remove=@() } }
            default { throw "Unexpected duplicate-download decision '$decision'." }
        }
    }
    return [pscustomobject]@{
        Cancelled = $false
        Selected = @($selected.ToArray())
        Remove = @($removed.ToArray())
    }
}

Export-ModuleMember -Function Read-VdDownloadHistory,Add-VdSuccessfulDownloadUrl,Resolve-VdDuplicateDownloadPlan
