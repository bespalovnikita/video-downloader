#requires -Version 7.0
param(
    [string]$In = ".\list.txt",
    [string]$Out,
    [int]$Threads = 4,
    [int]$Fragments = 1,
    [switch]$AutoFragments,
    [switch]$Watch,
    [int]$WatchInterval = 2,
    [switch]$Archive,
    [string]$Cookies,
    [switch]$SponsorBlock,
    [switch]$AudioOnly,
    [switch]$NoProgress,
    [switch]$DryRun,
    [string]$Log,
    [string]$EventFile,
    [string]$ResultDir,
    [ValidateSet("Best","2160","1440","1080","720")][string]$Quality = "Best",
    [string]$RateLimit,
    [ValidateSet("Auto","MP4","MKV","WebM")][string]$Container = "Auto",
    [ValidateSet("Auto","H264","VP9","AV1")][string]$VideoCodec = "Auto",
    [switch]$WriteSubtitles,
    [switch]$WriteAutoSubtitles,
    [string]$SubtitleLangs = "all,-live_chat",
    [switch]$EmbedSubtitles,
    [switch]$EmbedThumbnail,
    [switch]$EmbedMetadata,
    [switch]$EmbedChapters,
    [string]$FilenameTemplate = "%(title)s [%(id)s].%(ext)s",
    [ValidateRange(0,10)][int]$Retries = 3,
    [ValidateRange(1,120)][int]$RetryDelaySeconds = 5
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$coreModule = Join-Path $root "lib\VideoDownloader.Core.psm1"
if (-not (Test-Path $coreModule)) { throw "Missing core module: $coreModule" }
Import-Module $coreModule -Force

$ytDlpExe = Join-Path $root "yt-dlp.exe"
if (-not (Test-Path $ytDlpExe)) { throw "Missing yt-dlp.exe: $ytDlpExe" }

$ListFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($In)
$InputDir = Split-Path -Path $ListFile -Parent
$InputBase = [IO.Path]::GetFileNameWithoutExtension((Split-Path $ListFile -Leaf))
if ([string]::IsNullOrWhiteSpace($InputDir)) { $InputDir = $root }
if (-not (Test-Path $ListFile)) { throw "Missing input list: $ListFile" }

if ($Threads -lt 1) { $Threads = 1 }
if ($Fragments -lt 1) { $Fragments = 1 }
if ($Fragments -gt 8) { $Fragments = 8 }
if ($WatchInterval -lt 1) { $WatchInterval = 1 }
$FragmentsWasSpecified = $PSBoundParameters.ContainsKey("Fragments")

if ($Out) {
    $ResolvedOut = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
} else {
    $ResolvedOut = (Get-Location).Path
}
if ($Archive) { $ResolvedOut = Join-Path $ResolvedOut (Get-Date -Format "yyyy-MM-dd") }
New-Item -ItemType Directory -Path $ResolvedOut -Force | Out-Null

$ResultBaseDir = $InputDir
if ($ResultDir) {
    $ResultBaseDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultDir)
    New-Item -ItemType Directory -Path $ResultBaseDir -Force | Out-Null
}
$SuccessFile = Join-Path $ResultBaseDir ("{0}-success.txt" -f $InputBase)
$ErrorFile = Join-Path $ResultBaseDir ("{0}-error.txt" -f $InputBase)

if ($Cookies) {
    $ResolvedCookies = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Cookies)
    if (-not (Test-Path $ResolvedCookies)) { throw "Missing cookies file: $ResolvedCookies" }
} else {
    $ResolvedCookies = ""
}

if ($Log) {
    $Log = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Log)
    $dir = Split-Path $Log -Parent
    if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content $Log "" -Encoding UTF8
}

if ($EventFile) {
    $EventFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EventFile)
    $dir = Split-Path $EventFile -Parent
    if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content $EventFile "" -Encoding UTF8
}

$OutputTemplate = Join-Path $ResolvedOut $FilenameTemplate
$Qualities = switch ($Quality) {
    "2160" { @(2160,1440,1080,720) }
    "1440" { @(1440,1080,720) }
    "1080" { @(1080,720) }
    "720"  { @(720) }
    default { @("best") }
}

function NowText { Get-Date -Format "HH:mm:ss" }

function Shorten([AllowNull()][string]$Text,[int]$Max=120) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return "" }
    $t = $Text.Trim()
    if ($t.Length -le $Max) { return $t }
    return $t.Substring(0,[Math]::Max(0,$Max-3)) + "..."
}

function Write-Log([string]$Message,[ConsoleColor]$Color=[ConsoleColor]::Gray) {
    $line = "[{0}] {1}" -f (NowText),$Message
    try {
        $old = [Console]::ForegroundColor
        [Console]::ForegroundColor = $Color
        Write-Host $line
        [Console]::ForegroundColor = $old
    } catch { Write-Host $line }

    if ($script:Log) { Add-Content $script:Log $line -Encoding UTF8 }
}

function Save-RemainingList {
    param(
        [string[]]$Urls,
        [Collections.Generic.HashSet[string]]$Completed,
        [string]$Path
    )
    $current = @()
    if (Test-Path $Path) {
        $current = @(Get-Content $Path -Encoding UTF8 | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    $remaining = [Collections.Generic.List[string]]::new()
    foreach ($u in @($Urls + $current)) {
        if ($u -and -not $Completed.Contains($u) -and -not $remaining.Contains($u)) {
            [void]$remaining.Add($u)
        }
    }
    Set-Content $Path $remaining.ToArray() -Encoding UTF8
}

function Write-EventFile([object]$Message) {
    if (-not $script:EventFile -or -not $Message) { return }
    try { Add-Content $script:EventFile ($Message | ConvertTo-Json -Compress -Depth 8) -Encoding UTF8 } catch {}
}

function Handle-Message {
    param(
        [object]$Message,
        [Collections.ArrayList]$Results,
        [Collections.Generic.HashSet[string]]$Completed,
        [string[]]$Urls,
        [hashtable]$Titles
    )

    if (-not $Message) { return }
    Write-EventFile $Message

    if ($Message.Kind -eq "Event") {
        switch ($Message.EventType) {
            "Title" {
                if ($Message.Title) { $Titles[$Message.Slot] = [string]$Message.Title }
            }
            "Progress" {
                if (-not $NoProgress) {
                    $title = if ($Titles.ContainsKey($Message.Slot)) { $Titles[$Message.Slot] } else { "Downloading" }
                    $activity = "T{0} | {1}" -f $Message.Slot,(Shorten $title 100)
                    $state = "{0:N1}% | {1} | ETA {2}" -f [double]$Message.Percent,[string]$Message.Speed,[string]$Message.ETA
                    Write-Progress -Id $Message.Slot -Activity $activity -Status $state -PercentComplete ([int][Math]::Min(100,[Math]::Max(0,[double]$Message.Percent)))
                }
            }
            "Retry" { Write-Log $Message.Text Yellow }
            "Fallback" { Write-Log $Message.Text Yellow }
            "Error" { Write-Log $Message.Text Red }
            "Done" {
                if (-not $NoProgress) { Write-Progress -Id $Message.Slot -Activity ("T{0}" -f $Message.Slot) -Completed }
                Write-Log $Message.Text Green
            }
        }
        return
    }

    if ($Message.Kind -eq "Result") {
        [void]$Results.Add($Message)
        [void]$Completed.Add([string]$Message.Url)

        if ($Message.Success) {
            Add-Content $SuccessFile ([string]$Message.Url) -Encoding UTF8
        } else {
            Add-Content $ErrorFile ([string]$Message.Url) -Encoding UTF8
            Add-Content $ErrorFile ("ERROR: " + [string]$Message.Error) -Encoding UTF8
            Add-Content $ErrorFile "" -Encoding UTF8
        }

        Save-RemainingList -Urls $Urls -Completed $Completed -Path $ListFile
    }
}

$urls = @(Get-Content $ListFile -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
Write-Log ("Started. URLs={0}; parallel={1}; quality={2}; codec={3}; container={4}" -f $urls.Count,$Threads,$Quality,$VideoCodec,$Container) Cyan
Write-Log ("Output: {0}" -f $ResolvedOut) Cyan
Write-Log ("Template: {0}" -f $FilenameTemplate) Cyan
Write-Log ("Retries: {0}; delay={1}s" -f $Retries,$RetryDelaySeconds) Cyan
if ($RateLimit) { Write-Log ("Rate limit: {0}" -f $RateLimit) Cyan }

if ($DryRun) {
    foreach ($u in $urls) { Write-Log ("DRY: " + $u) DarkGray }
    Write-Log "DryRun finished." Cyan
    exit 0
}

Remove-Item $SuccessFile,$ErrorFile -ErrorAction SilentlyContinue

$pending = [Collections.Generic.Queue[string]]::new()
$known = [Collections.Generic.HashSet[string]]::new()
foreach ($u in $urls) {
    if ($known.Add($u)) { $pending.Enqueue($u) }
}

$freeSlots = [Collections.Generic.Queue[int]]::new()
1..$Threads | ForEach-Object { $freeSlots.Enqueue($_) }

$jobs = @()
$completed = [Collections.Generic.HashSet[string]]::new()
$results = [Collections.ArrayList]::new()
$titles = @{}
$startedCount = 0
$totalUrls = $known.Count
$lastWatchCheck = Get-Date

while ($pending.Count -gt 0 -or $jobs.Count -gt 0 -or $Watch) {
    if ($Watch -and ((Get-Date)-$lastWatchCheck).TotalSeconds -ge $WatchInterval) {
        $lastWatchCheck = Get-Date
        foreach ($u in @(Get-Content $ListFile -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
            if ($known.Add($u)) {
                $pending.Enqueue($u)
                $urls += $u
                $totalUrls = $known.Count
                Write-Log ("NEW URL queued: " + (Shorten $u 100)) Cyan
            }
        }
    }

    while ($pending.Count -gt 0 -and $freeSlots.Count -gt 0) {
        $slot = $freeSlots.Dequeue()
        $url = $pending.Dequeue()
        $startedCount++
        $index = $startedCount

        $jobArgs = @(
            $slot,$index,$totalUrls,$url,$Qualities,$OutputTemplate,$ResolvedCookies,
            [bool]$SponsorBlock,[bool]$AudioOnly,$Fragments,
            [bool]($AutoFragments -and -not $FragmentsWasSpecified),
            $RateLimit,$Container,$VideoCodec,
            [bool]$WriteSubtitles,[bool]$WriteAutoSubtitles,$SubtitleLangs,[bool]$EmbedSubtitles,
            [bool]$EmbedThumbnail,[bool]$EmbedMetadata,[bool]$EmbedChapters,
            $Retries,$RetryDelaySeconds,$ytDlpExe,$coreModule
        )

        Write-Log ("T{0} START #{1}/{2} {3}" -f $slot,$index,$totalUrls,(Shorten $url 100)) Cyan

        $jobs += Start-ThreadJob -Name ([string]$slot) -ArgumentList $jobArgs -ScriptBlock {
            param(
                $slot,$index,$totalUrls,$url,$qualities,$outputTemplate,$cookies,
                [bool]$sponsorBlock,[bool]$audioOnly,[int]$fragments,[bool]$autoFragments,
                [string]$rateLimit,[string]$container,[string]$videoCodec,
                [bool]$writeSubtitles,[bool]$writeAutoSubtitles,[string]$subtitleLangs,[bool]$embedSubtitles,
                [bool]$embedThumbnail,[bool]$embedMetadata,[bool]$embedChapters,
                [int]$retries,[int]$retryDelaySeconds,[string]$ytDlpExe,[string]$coreModule
            )

            Import-Module $coreModule -Force

            function Emit {
                param(
                    [string]$Type,
                    [string]$Text="",
                    [double]$Percent=0,
                    [string]$Speed="",
                    [string]$ETA="",
                    [int64]$DownloadedBytes=0,
                    [int64]$TotalBytes=0,
                    [string]$Height="",
                    [string]$Title=""
                )
                [pscustomobject]@{
                    Kind="Event"
                    EventType=$Type
                    Slot=$slot
                    Url=$url
                    Text=$Text
                    Percent=$Percent
                    Speed=$Speed
                    ETA=$ETA
                    DownloadedBytes=$DownloadedBytes
                    TotalBytes=$TotalBytes
                    Height=$Height
                    Title=$Title
                    Timestamp=(Get-Date).ToString("o")
                }
            }

            function Get-AutoFragments {
                $a = @("--encoding","utf-8","--skip-download","--print","%(filesize,filesize_approx)s")
                if ($cookies) { $a += @("--cookies",$cookies) }
                $a += $url
                try {
                    $v = (& $ytDlpExe @a 2>$null | Select-Object -First 1)
                    $bytes = [int64]0
                    [void][int64]::TryParse(([string]$v).Trim(),[ref]$bytes)
                    if ($bytes -ge 500MB) { return 4 }
                    if ($bytes -ge 100MB) { return 2 }
                } catch {}
                return 1
            }

            function Get-FormatSelector([object]$Height) {
                $codecPrefix = Get-VideoCodecSelector -Codec $videoCodec
                if ([string]$Height -eq "best") {
                    if ($codecPrefix) { return "bv*[vcodec^=$codecPrefix]+ba/b[vcodec^=$codecPrefix]/bv*+ba/b" }
                    return "bv*+ba/b"
                }

                if ($codecPrefix) {
                    return "bv*[height<=$Height][vcodec^=$codecPrefix]+ba/b[height<=$Height][vcodec^=$codecPrefix]/bv*[height<=$Height]+ba/b[height<=$Height]"
                }
                return "bv*[height<=$Height]+ba/b[height<=$Height]"
            }

            function Build-Args([object]$Height) {
                $effectiveFragments = $fragments
                if ($autoFragments) { $effectiveFragments = Get-AutoFragments }
                if ($effectiveFragments -lt 1) { $effectiveFragments = 1 }
                if ($effectiveFragments -gt 8) { $effectiveFragments = 8 }

                $a = @(
                    "--encoding","utf-8",
                    "--newline",
                    "--no-color",
                    "--impersonate","chrome",
                    "-N",[string]$effectiveFragments,
                    "-o",$outputTemplate,
                    "--progress-template","download:__VD_PROGRESS__|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s",
                    "--print","before_dl:__VD_TITLE__:%(title)s",
                    "--print","after_move:__VD_FILE__:%(filepath)s"
                )

                if ($cookies) { $a += @("--cookies",$cookies) }
                if ($rateLimit) { $a += @("--limit-rate",$rateLimit) }
                if ($sponsorBlock) { $a += @("--sponsorblock-remove","sponsor") }

                if ($audioOnly) {
                    $a += @("-x","--audio-format","mp3")
                } else {
                    $a += @("-f",(Get-FormatSelector $Height))
                    if ($container -ne "Auto") { $a += @("--merge-output-format",$container.ToLowerInvariant()) }
                }

                if ($writeSubtitles -or $embedSubtitles) {
                    $a += "--write-subs"
                    if ($subtitleLangs) { $a += @("--sub-langs",$subtitleLangs) }
                }
                if ($writeAutoSubtitles) { $a += "--write-auto-subs" }
                if ($embedSubtitles) { $a += "--embed-subs" }
                if ($embedThumbnail) { $a += @("--write-thumbnail","--embed-thumbnail") }
                if ($embedMetadata) { $a += "--embed-metadata" }
                if ($embedChapters) { $a += "--embed-chapters" }

                $a += $url
                return ,$a
            }

            $success = $false
            $finalError = ""
            $finalPath = ""
            $finalHeight = ""
            $stopAll = $false

            foreach ($height in $qualities) {
                if ($stopAll) { break }
                $finalHeight = [string]$height

                for ($attempt=1; $attempt -le ($retries+1); $attempt++) {
                    $errorLines = [Collections.Generic.List[string]]::new()
                    $args = Build-Args $height

                    & $ytDlpExe @args 2>&1 | ForEach-Object {
                        $line = $_.ToString()

                        if ($line.StartsWith("__VD_PROGRESS__|")) {
                            $p = Parse-VdProgressLine $line
                            if ($p) {
                                Emit -Type "Progress" -Percent $p.Percent -Speed $p.Speed -ETA $p.ETA -DownloadedBytes $p.DownloadedBytes -TotalBytes $p.TotalBytes -Height $finalHeight
                            }
                            return
                        }

                        if ($line.StartsWith("__VD_TITLE__:")) {
                            $title = $line.Substring("__VD_TITLE__:".Length).Trim()
                            Emit -Type "Title" -Title $title -Height $finalHeight
                            return
                        }

                        if ($line.StartsWith("__VD_FILE__:")) {
                            $finalPath = $line.Substring("__VD_FILE__:".Length).Trim()
                            Emit -Type "File" -Text $finalPath -Height $finalHeight
                            return
                        }

                        if ($line -match '\[Merger\]|\[ffmpeg\].*merg') {
                            Emit -Type "Merge" -Text $line -Height $finalHeight
                        }

                        if ($line -match '^ERROR:|WARNING:|HTTP Error|Unable to download|timed out|connection') {
                            [void]$errorLines.Add($line)
                        }
                    }

                    $exitCode = $LASTEXITCODE
                    if ($exitCode -eq 0) {
                        $success = $true
                        $stopAll = $true
                        break
                    }

                    $finalError = ($errorLines | Select-Object -Last 12) -join " | "
                    if ([string]::IsNullOrWhiteSpace($finalError)) { $finalError = "yt-dlp exit code $exitCode" }

                    $formatUnavailable = $finalError -match 'Requested format is not available'
                    if ($formatUnavailable -and $height -ne $qualities[-1]) {
                        Emit -Type "Fallback" -Text ("T{0} FALLBACK q<={1} -> next quality" -f $slot,$height) -Height $finalHeight
                        break
                    }

                    $transient = Test-TransientDownloadError $finalError
                    if ($transient -and $attempt -le $retries) {
                        $delay = [Math]::Min(60,$retryDelaySeconds * [Math]::Pow(2,$attempt-1))
                        Emit -Type "Retry" -Text ("T{0} RETRY {1}/{2} in {3}s: {4}" -f $slot,$attempt,$retries,[int]$delay,(Get-FriendlyDownloadError $finalError)) -Height $finalHeight
                        Start-Sleep -Seconds ([int]$delay)
                        continue
                    }

                    $stopAll = $true
                    break
                }
            }

            if ($success) {
                $fileSize = [int64]0
                if ($finalPath -and (Test-Path $finalPath -PathType Leaf)) {
                    try { $fileSize = (Get-Item $finalPath).Length } catch {}
                }

                Emit -Type "Done" -Text ("T{0} DONE #{1}/{2} q={3}" -f $slot,$index,$totalUrls,$finalHeight) -Height $finalHeight
                [pscustomobject]@{
                    Kind="Result"
                    Slot=$slot
                    Url=$url
                    Success=$true
                    Error=""
                    FriendlyError=""
                    Height=$finalHeight
                    Path=$finalPath
                    FileSize=$fileSize
                }
            } else {
                $friendly = Get-FriendlyDownloadError $finalError
                Emit -Type "Error" -Text ("T{0} ERROR #{1}/{2}: {3}" -f $slot,$index,$totalUrls,$friendly) -Height $finalHeight
                [pscustomobject]@{
                    Kind="Result"
                    Slot=$slot
                    Url=$url
                    Success=$false
                    Error=$finalError
                    FriendlyError=$friendly
                    Height=$finalHeight
                    Path=""
                    FileSize=[int64]0
                }
            }
        }
    }

    Start-Sleep -Milliseconds 200

    foreach ($j in @($jobs)) {
        foreach ($msg in @(Receive-Job $j -ErrorAction SilentlyContinue)) {
            Handle-Message -Message $msg -Results $results -Completed $completed -Urls $urls -Titles $titles
        }
    }

    foreach ($j in @($jobs | Where-Object { $_.State -ne "Running" })) {
        foreach ($msg in @(Receive-Job $j -ErrorAction SilentlyContinue)) {
            Handle-Message -Message $msg -Results $results -Completed $completed -Urls $urls -Titles $titles
        }

        $slot = [int]$j.Name
        if (-not $NoProgress) { Write-Progress -Id $slot -Activity ("T{0}" -f $slot) -Completed }
        Remove-Job $j -Force
        $jobs = @($jobs | Where-Object { $_.Id -ne $j.Id })
        $freeSlots.Enqueue($slot)
        $titles.Remove($slot)
    }
}

$successCount = @($results | Where-Object { $_.Success }).Count
$errorCount = @($results | Where-Object { -not $_.Success }).Count
$remaining = 0
if (Test-Path $ListFile) { $remaining = @(Get-Content $ListFile -Encoding UTF8 | Where-Object { $_ }).Count }
Write-Log ("Finished. Success={0}, Errors={1}, Remaining={2}" -f $successCount,$errorCount,$remaining) Cyan
if ($errorCount -gt 0) { exit 2 }
exit 0
