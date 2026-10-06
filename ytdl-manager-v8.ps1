# ytdl-manager-v8.ps1
# PowerShell 7+
# yt-dlp parallel download manager
#
# Required near this script or in PATH:
#   yt-dlp.exe
#   ffmpeg.exe
#   ffprobe.exe
#
# Examples:
#   .\ytdl-manager-v8.ps1
#   .\ytdl-manager-v8.ps1 -In ".\arg.txt" -Out ".\cats" -Threads 8
#   .\ytdl-manager-v8.ps1 -Archive -Out "D:\Downloads"
#   .\ytdl-manager-v8.ps1 -Cookies ".\cookies.txt"
#   .\ytdl-manager-v8.ps1 -SponsorBlock
#   .\ytdl-manager-v8.ps1 -AudioOnly
#   .\ytdl-manager-v8.ps1 -NoProgress
#   .\ytdl-manager-v8.ps1 -DryRun
#   .\ytdl-manager-v8.ps1 -Log ".\run.log"
#   .\ytdl-manager-v8.ps1 -Threads 4 -Fragments 3
#   .\ytdl-manager-v8.ps1 -AutoFragments

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
    [string]$ResultDir
)

$ListFile = $In

$InputDir = Split-Path -Path $ListFile -Parent
$InputLeaf = Split-Path -Path $ListFile -Leaf
$InputBase = [System.IO.Path]::GetFileNameWithoutExtension($InputLeaf)

if ([string]::IsNullOrWhiteSpace($InputDir)) {
    $InputDir = "."
}

$ResultBaseDir = $InputDir
if (-not [string]::IsNullOrWhiteSpace($ResultDir)) {
    $ResultBaseDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultDir)
    if (-not (Test-Path $ResultBaseDir)) {
        New-Item -ItemType Directory -Path $ResultBaseDir -Force | Out-Null
    }
}

$SuccessFile = Join-Path $ResultBaseDir ("{0}-success.txt" -f $InputBase)
$ErrorFile   = Join-Path $ResultBaseDir ("{0}-error.txt" -f $InputBase)

if ($Threads -lt 1) {
    $Threads = 1
}

if ($Fragments -lt 1) {
    $Fragments = 1
}

if ($Fragments -gt 4) {
    $Fragments = 4
}

if ($WatchInterval -lt 1) {
    $WatchInterval = 1
}

$FragmentsWasSpecified = $PSBoundParameters.ContainsKey("Fragments")

$MaxParallel = $Threads
$Qualities   = @(2160, 1440, 1080, 720)

try {
    [Console]::InputEncoding  = [System.Text.Encoding]::UTF8
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch {}

$env:PYTHONIOENCODING = "utf-8"
$env:PYTHONUTF8 = "1"
$env:PYTHONLEGACYWINDOWSSTDIO = "0"

# Make native Write-Progress use the wider classic layout when supported.
try {
    $PSStyle.Progress.View = "Classic"
    $PSStyle.Progress.MaxWidth = [Math]::Max(80, [Console]::WindowWidth - 1)
} catch {}

if ($Log) {
    $logDir = Split-Path -Path $Log -Parent
    if (-not [string]::IsNullOrWhiteSpace($logDir) -and -not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }

    Set-Content -Path $Log -Value "" -Encoding UTF8
}

if ($EventFile) {
    $eventDir = Split-Path -Path $EventFile -Parent
    if (-not [string]::IsNullOrWhiteSpace($eventDir) -and -not (Test-Path $eventDir)) {
        New-Item -ItemType Directory -Path $eventDir -Force | Out-Null
    }

    Set-Content -Path $EventFile -Value "" -Encoding UTF8
}

if ($Out) {
    $ResolvedOut = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
}
else {
    $ResolvedOut = (Get-Location).Path
}

if ($Archive) {
    $ResolvedOut = Join-Path $ResolvedOut (Get-Date -Format "yyyy-MM-dd")
}

if (-not (Test-Path $ResolvedOut)) {
    New-Item -ItemType Directory -Path $ResolvedOut -Force | Out-Null
}

if ($AudioOnly) {
    $OutputTemplate = Join-Path $ResolvedOut "%(title)s [%(id)s].%(ext)s"
}
else {
    $OutputTemplate = Join-Path $ResolvedOut "%(title)s [%(id)s].%(ext)s"
}

function NowText {
    return Get-Date -Format "HH:mm:ss"
}

function Shorten {
    param(
        [Parameter(Mandatory=$false)][AllowNull()][string]$Text,
        [int]$Max = 120
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ""
    }

    $t = $Text.Trim()

    if ($t.Length -le $Max) {
        return $t
    }

    return $t.Substring(0, [Math]::Max(0, $Max - 3)) + "..."
}

function Clean-DisplayTitle {
    param(
        [Parameter(Mandatory=$false)][AllowNull()][string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ""
    }

    $t = $Text.Trim()

    # Remove directory part if yt-dlp printed full Destination path.
    try {
        $leaf = Split-Path -Path $t -Leaf
        if (-not [string]::IsNullOrWhiteSpace($leaf)) {
            $t = $leaf
        }
    } catch {}

    # Remove common yt-dlp temporary/final extension tails:
    #   title [VIDEO_ID].f399.mp4
    #   title [VIDEO_ID].f400.webm
    #   title [VIDEO_ID].mp4
    #   title [VIDEO_ID].webm
    $t = $t -replace '\s+\[[A-Za-z0-9_-]{6,}\]\.f\d+\.[^.\\/:]+$', ''
    $t = $t -replace '\s+\[[A-Za-z0-9_-]{6,}\]\.[^.\\/:]+$', ''

    # Remove residual format extension if still present.
    $t = $t -replace '\.f\d+\.[^.\\/:]+$', ''
    $t = $t -replace '\.(mp4|webm|mkv|m4a|mp3|opus|flv|mov)$', ''

    return $t.Trim()
}

function Write-Log {
    param(
        [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Gray
    )

    $line = "[{0}] {1}" -f (NowText), $Message

    $old = [Console]::ForegroundColor
    [Console]::ForegroundColor = $Color
    Write-Host $line
    [Console]::ForegroundColor = $old

    if ($script:Log) {
        Add-Content -Path $script:Log -Value $line -Encoding UTF8
    }
}

function Save-RemainingList {
    param(
        [string[]]$Urls,
        [System.Collections.Generic.HashSet[string]]$Completed,
        [string]$Path
    )

    # Re-read file to avoid overwriting URLs appended while the script is running.
    $current = @()
    if (Test-Path $Path) {
        $current = @(Get-Content $Path | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    $combined = [System.Collections.Generic.List[string]]::new()

    foreach ($u in $Urls) {
        if (-not $Completed.Contains($u) -and -not $combined.Contains($u)) {
            [void]$combined.Add($u)
        }
    }

    foreach ($u in $current) {
        if (-not $Completed.Contains($u) -and -not $combined.Contains($u)) {
            [void]$combined.Add($u)
        }
    }

    Set-Content -Path $Path -Value $combined.ToArray() -Encoding UTF8
}

function Handle-Message {
    param(
        [object]$Message,
        [System.Collections.ArrayList]$Results,
        [System.Collections.Generic.HashSet[string]]$Completed,
        [string[]]$Urls,
        [hashtable]$Titles,
        [switch]$NoProgress
    )

    if (-not $Message) {
        return
    }

    if ($script:EventFile) {
        try {
            $json = $Message | ConvertTo-Json -Compress -Depth 5
            Add-Content -Path $script:EventFile -Value $json -Encoding UTF8
        }
        catch {}
    }

    if ($Message.Kind -eq "Event") {
        switch ($Message.EventType) {
            "Progress" {
                if (-not $NoProgress) {
                    $activityTitle = if ($Titles.ContainsKey($Message.Slot)) {
                        $Titles[$Message.Slot]
                    }
                    else {
                        "Downloading"
                    }

                    # Fixed title column: max 100 chars, pad with spaces if shorter.
                    $titleWidth = 200
                    $titleText = Shorten -Text $activityTitle -Max $titleWidth
                    $titleText = $titleText.PadRight($titleWidth)

                    # Re-apply width dynamically in case terminal was resized.
                    try {
                        $PSStyle.Progress.MaxWidth = [Math]::Max(80, [Console]::WindowWidth - 1)
                    } catch {}

                    Write-Progress `
                        -Id $Message.Slot `
                        -Activity ("T{0} | {1}" -f $Message.Slot, $titleText) `
                        -Status ("{0,5:N1}% | {1,12} | ETA {2,-8} | q<={3}p" -f `
                            [double]$Message.Percent,
                            [string]$Message.Speed,
                            [string]$Message.ETA,
                            [string]$Message.Height) `
                        -PercentComplete ([int][Math]::Min(100, [Math]::Max(0, $Message.Percent)))
                }

                return
            }

            "Title" {
                $cleanTitle = Clean-DisplayTitle -Text $Message.Title
                $Titles[$Message.Slot] = Shorten -Text $cleanTitle -Max 200
                return
            }

            "Done" {
                if (-not $NoProgress) {
                    Write-Progress -Id $Message.Slot -Activity ("T{0}" -f $Message.Slot) -Completed
                }

                Write-Log $Message.Text Green
                return
            }

            "Error" {
                if (-not $NoProgress) {
                    Write-Progress -Id $Message.Slot -Activity ("T{0}" -f $Message.Slot) -Completed
                }

                Write-Log $Message.Text Red
                return
            }

            "Fallback" {
                Write-Log $Message.Text Yellow
                return
            }

            "Merge" {
                if (-not $NoProgress) {
                    Write-Progress `
                        -Id $Message.Slot `
                        -Activity ("T{0} Merging" -f $Message.Slot) `
                        -Status "Merging formats..." `
                        -PercentComplete 100
                }

                return
            }

            default {
                return
            }
        }
    }

    if ($Message.Kind -eq "Result") {
        [void]$Results.Add($Message)
        [void]$Completed.Add($Message.Url)

        if ($Message.Success) {
            Add-Content -Path $SuccessFile -Value $Message.Url -Encoding UTF8
        }
        else {
            Add-Content -Path $ErrorFile -Value $Message.Url -Encoding UTF8
            Add-Content -Path $ErrorFile -Value ("ERROR: " + $Message.Error) -Encoding UTF8
            Add-Content -Path $ErrorFile -Value "" -Encoding UTF8
        }

        Save-RemainingList -Urls $Urls -Completed $Completed -Path $ListFile
        return
    }
}

if (-not (Test-Path $ListFile)) {
    Write-Log "Missing $ListFile" Red
    exit 1
}

if (-not (Test-Path ".\yt-dlp.exe")) {
    Write-Log "Missing .\yt-dlp.exe" Red
    exit 1
}

if ($Cookies) {
    $ResolvedCookies = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Cookies)
    if (-not (Test-Path $ResolvedCookies)) {
        Write-Log "Missing cookies file: $Cookies" Red
        exit 1
    }
}
else {
    $ResolvedCookies = ""
}

if (-not (Test-Path ".\ffmpeg.exe")) {
    Write-Log "Warning: .\ffmpeg.exe not found" Yellow
}

if (-not (Test-Path ".\ffprobe.exe")) {
    Write-Log "Warning: .\ffprobe.exe not found" Yellow
}

$urls = @(Get-Content $ListFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

if ($urls.Count -eq 0 -and -not $Watch) {
    Write-Log "No URLs in $ListFile" Yellow
    exit 0
}

Write-Log "Started. Total URLs: $($urls.Count). Parallel: $MaxParallel. Qualities: $($Qualities -join ' -> ')" Cyan
Write-Log "Fragments: $Fragments" Cyan

if ($AutoFragments) {
    if ($FragmentsWasSpecified) {
        Write-Log "AutoFragments: ON, but ignored because -Fragments was specified" Yellow
    }
    else {
        Write-Log "AutoFragments: ON" Cyan
    }
}

Write-Log "Input file: $ListFile" Cyan
Write-Log "Success file: $SuccessFile" Cyan
Write-Log "Error file: $ErrorFile" Cyan
Write-Log "Output directory: $ResolvedOut" Cyan

if ($Archive) {
    Write-Log "Archive mode: ON" Cyan
}

if ($Cookies) {
    Write-Log "Cookies: $ResolvedCookies" Cyan
}

if ($SponsorBlock) {
    Write-Log "SponsorBlock: ON" Cyan
}

if ($AudioOnly) {
    Write-Log "AudioOnly: ON" Cyan
}

if ($NoProgress) {
    Write-Log "NoProgress: ON" Cyan
}

if ($Watch) {
    Write-Log "Watch: ON, interval=${WatchInterval}s" Cyan
}

if ($DryRun) {
    Write-Log "DryRun: ON" Yellow
    Write-Log "Nothing will be downloaded." Yellow

    $i = 1
    foreach ($url in $urls) {
        Write-Log ("#{0}/{1} {2}" -f $i, $urls.Count, $url) DarkGray
        $i++
    }

    Write-Log "DryRun finished." Cyan
    exit 0
}

Remove-Item $SuccessFile,$ErrorFile -ErrorAction SilentlyContinue

$pendingQueue = [System.Collections.Generic.Queue[string]]::new()
$knownUrls = [System.Collections.Generic.HashSet[string]]::new()

foreach ($u in $urls) {
    if ($knownUrls.Add($u)) {
        $pendingQueue.Enqueue($u)
    }
}

$freeSlots = [System.Collections.Generic.Queue[int]]::new()
foreach ($slot in 1..$MaxParallel) {
    $freeSlots.Enqueue($slot)
}

$jobs = @()
$startedCount = 0
$totalUrls = $knownUrls.Count
$lastWatchCheck = Get-Date
$completed = [System.Collections.Generic.HashSet[string]]::new()
$results = [System.Collections.ArrayList]::new()
$titles = @{}

while ($pendingQueue.Count -gt 0 -or $jobs.Count -gt 0 -or $Watch) {

    if ($Watch -and ((Get-Date) - $lastWatchCheck).TotalSeconds -ge $WatchInterval) {
        $lastWatchCheck = Get-Date

        $currentUrls = @(Get-Content $ListFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

        foreach ($newUrl in $currentUrls) {
            if ($knownUrls.Add($newUrl)) {
                $pendingQueue.Enqueue($newUrl)
                $urls += $newUrl
                $totalUrls = $knownUrls.Count
                Write-Log ("NEW URL queued: {0}" -f (Shorten -Text $newUrl -Max 100)) Cyan
            }
        }
    }

    while ($pendingQueue.Count -gt 0 -and $freeSlots.Count -gt 0) {
        $slot = $freeSlots.Dequeue()
        $url = $pendingQueue.Dequeue()
        $startedCount++
        $index = $startedCount

        $titles[$slot] = Shorten -Text $url -Max 200

        Write-Log ("T{0} START #{1}/{2} {3}" -f $slot, $index, $totalUrls, (Shorten -Text $url -Max 100)) Cyan

        if (-not $NoProgress) {
            $queuedTitleWidth = 200
            $queuedTitle = (Shorten -Text $url -Max $queuedTitleWidth).PadRight($queuedTitleWidth)

            try {
                $PSStyle.Progress.MaxWidth = [Math]::Max(80, [Console]::WindowWidth - 1)
            } catch {}

            Write-Progress `
                -Id $slot `
                -Activity ("T{0} | {1}" -f $slot, $queuedTitle) `
                -Status "Queued" `
                -PercentComplete 0
        }

        $jobArgs = @(
            $slot,
            $index,
            $totalUrls,
            $url,
            $Qualities,
            $OutputTemplate,
            $ResolvedCookies,
            [bool]$SponsorBlock,
            [bool]$AudioOnly,
            $Fragments,
            [bool]($AutoFragments -and -not $FragmentsWasSpecified)
        )

        $jobs += Start-ThreadJob -Name "$slot" -ArgumentList $jobArgs -ScriptBlock {
            param(
                $slot,
                $index,
                $totalUrls,
                $url,
                $qualities,
                $outputTemplate,
                $cookies,
                [bool]$sponsorBlock,
                [bool]$audioOnly,
                [int]$fragments,
                [bool]$autoFragments
            )

            function ShortenInner {
                param(
                    [Parameter(Mandatory=$false)][AllowNull()][string]$Text,
                    [int]$Max = 120
                )

                if ([string]::IsNullOrWhiteSpace($Text)) { return "" }
                $t = $Text.Trim()
                if ($t.Length -le $Max) { return $t }
                return $t.Substring(0, [Math]::Max(0, $Max - 3)) + "..."
            }

            function Clean-DisplayTitleInner {
                param(
                    [Parameter(Mandatory=$false)][AllowNull()][string]$Text
                )

                if ([string]::IsNullOrWhiteSpace($Text)) {
                    return ""
                }

                $t = $Text.Trim()

                try {
                    $leaf = Split-Path -Path $t -Leaf
                    if (-not [string]::IsNullOrWhiteSpace($leaf)) {
                        $t = $leaf
                    }
                } catch {}

                $t = $t -replace '\s+\[[A-Za-z0-9_-]{6,}\]\.f\d+\.[^.\\/:]+$', ''
                $t = $t -replace '\s+\[[A-Za-z0-9_-]{6,}\]\.[^.\\/:]+$', ''
                $t = $t -replace '\.f\d+\.[^.\\/:]+$', ''
                $t = $t -replace '\.(mp4|webm|mkv|m4a|mp3|opus|flv|mov)$', ''

                return $t.Trim()
            }

            function Emit-Event {
                param(
                    [string]$EventType,
                    [string]$Text,
                    [double]$Percent = 0,
                    [string]$Speed = "",
                    [string]$Size = "",
                    [string]$ETA = "",
                    [string]$Height = "",
                    [string]$Title = ""
                )

                [pscustomobject]@{
                    Kind = "Event"
                    EventType = $EventType
                    Slot = $slot
                    Text = $Text
                    Percent = $Percent
                    Speed = $Speed
                    Size = $Size
                    ETA = $ETA
                    Height = $Height
                    Title = $Title
                }
            }

            function Invoke-YtDlp {
                param(
                    [string[]]$Args
                )

                & .\yt-dlp.exe @Args 2>&1
                return $LASTEXITCODE
            }


            function Convert-SizeTextToBytes {
                param([string]$Text)

                if ([string]::IsNullOrWhiteSpace($Text)) {
                    return 0
                }

                if ($Text -match '([0-9.]+)\s*([KMGT]?i?)?B') {
                    $value = [double]$matches[1]
                    $unit = $matches[2]

                    switch -Regex ($unit) {
                        '^K' { return [int64]($value * 1024) }
                        '^M' { return [int64]($value * 1024 * 1024) }
                        '^G' { return [int64]($value * 1024 * 1024 * 1024) }
                        '^T' { return [int64]($value * 1024 * 1024 * 1024 * 1024) }
                        default { return [int64]$value }
                    }
                }

                return 0
            }

            function Get-AutoFragments {
                param(
                    [string]$Url,
                    [string]$Cookies
                )

                $args = @(
                    "--encoding", "utf-8",
                    "--impersonate", "chrome",
                    "--print", "%(filesize,filesize_approx)s"
                )

                if ($Cookies) {
                    $args += @("--cookies", $Cookies)
                }

                $args += $Url

                $sizeText = ""
                try {
                    $sizeText = (& .\yt-dlp.exe @args 2>$null | Select-Object -First 1)
                }
                catch {
                    return [pscustomobject]@{
                        Fragments = 1
                        SizeText = "unknown"
                    }
                }

                if ([string]::IsNullOrWhiteSpace($sizeText) -or $sizeText -eq "NA" -or $sizeText -eq "None") {
                    return [pscustomobject]@{
                        Fragments = 1
                        SizeText = "unknown"
                    }
                }

                $bytes = 0

                if ($sizeText -match '^\d+$') {
                    $bytes = [int64]$sizeText
                    $display = "{0:N1}MiB" -f ($bytes / 1024 / 1024)
                }
                else {
                    $bytes = Convert-SizeTextToBytes -Text $sizeText
                    $display = $sizeText
                }

                if ($bytes -le 0) {
                    return [pscustomobject]@{
                        Fragments = 1
                        SizeText = $display
                    }
                }

                $mb = $bytes / 1024 / 1024

                if ($mb -lt 100) {
                    $n = 1
                }
                elseif ($mb -lt 500) {
                    $n = 2
                }
                else {
                    $n = 4
                }

                return [pscustomobject]@{
                    Fragments = $n
                    SizeText = $display
                }
            }

            try {
                $lastError = ""
                $title = ""
                $finalHeight = ""
                $success = $false
                $effectiveFragments = $fragments

                if ($autoFragments) {
                    $auto = Get-AutoFragments -Url $url -Cookies $cookies
                    $effectiveFragments = [int]$auto.Fragments

                    if ($effectiveFragments -lt 1) { $effectiveFragments = 1 }
                    if ($effectiveFragments -gt 4) { $effectiveFragments = 4 }

                    Emit-Event -EventType "Fallback" -Text ("T{0} AUTO-FRAGMENTS size={1} -> N={2}" -f $slot, $auto.SizeText, $effectiveFragments)
                }

                if ($audioOnly) {
                    $finalHeight = "audio"

                    $args = @(
                        "--encoding", "utf-8",
                        "--newline",
                        "--impersonate", "chrome",
                        "-N", "$effectiveFragments",
                        "-o", $outputTemplate,
                        "-x",
                        "--audio-format", "mp3"
                    )

                    if ($cookies) {
                        $args += @("--cookies", $cookies)
                    }

                    if ($sponsorBlock) {
                        $args += @("--sponsorblock-remove", "sponsor")
                    }

                    $args += $url

                    $allLines = [System.Collections.Generic.List[string]]::new()

                    & .\yt-dlp.exe @args 2>&1 |
                    ForEach-Object {
                        $line = $_.ToString()
                        [void]$allLines.Add($line)

                        if ($line -match 'Destination:\s+(.+)$') {
                            $title = Clean-DisplayTitleInner -Text ($matches[1].Trim())
                            Emit-Event -EventType "Title" -Text "" -Title $title -Height "audio"
                        }
                        elseif ($line -match '\[download\]\s+([0-9.]+)%\s+of\s+(.+?)\s+at\s+(.+?)\s+ETA\s+(.+)$') {
                            $pct = [double]$matches[1]
                            $size = $matches[2].Trim()
                            $speed = $matches[3].Trim()
                            $eta = $matches[4].Trim()

                            Emit-Event `
                                -EventType "Progress" `
                                -Text "" `
                                -Percent $pct `
                                -Speed $speed `
                                -Size $size `
                                -ETA $eta `
                                -Height "audio"
                        }
                        elseif ($line -match 'ExtractAudio|Deleting original file|Destination') {
                            Emit-Event -EventType "Merge" -Text "" -Height "audio"
                        }
                        elseif ($line -match '^ERROR:\s*(.+)$') {
                            $lastError = $matches[1].Trim()
                        }
                    }

                    $exit = $LASTEXITCODE

                    if ($exit -eq 0) {
                        $success = $true
                    }
                    elseif (-not $lastError) {
                        $lastError = "yt-dlp exit code $exit"
                    }
                }
                else {
                    foreach ($height in $qualities) {
                        $finalHeight = "$height"
                        $fmt = "bv*[height<=$height]+ba/b[height<=$height]"
                        $allLines = [System.Collections.Generic.List[string]]::new()

                        $args = @(
                            "--encoding", "utf-8",
                            "--newline",
                            "--impersonate", "chrome",
                            "-N", "$effectiveFragments",
                            "-o", $outputTemplate,
                            "-f", $fmt
                        )

                        if ($cookies) {
                            $args += @("--cookies", $cookies)
                        }

                        if ($sponsorBlock) {
                            $args += @("--sponsorblock-remove", "sponsor")
                        }

                        $args += $url

                        & .\yt-dlp.exe @args 2>&1 |
                        ForEach-Object {
                            $line = $_.ToString()
                            [void]$allLines.Add($line)

                            if ($line -match 'Destination:\s+(.+)$') {
                                $title = Clean-DisplayTitleInner -Text ($matches[1].Trim())

                                Emit-Event `
                                    -EventType "Title" `
                                    -Text "" `
                                    -Title $title `
                                    -Height "$height"
                            }
                            elseif ($line -match '\[download\]\s+([0-9.]+)%\s+of\s+(.+?)\s+at\s+(.+?)\s+ETA\s+(.+)$') {
                                $pct = [double]$matches[1]
                                $size = $matches[2].Trim()
                                $speed = $matches[3].Trim()
                                $eta = $matches[4].Trim()

                                Emit-Event `
                                    -EventType "Progress" `
                                    -Text "" `
                                    -Percent $pct `
                                    -Speed $speed `
                                    -Size $size `
                                    -ETA $eta `
                                    -Height "$height"
                            }
                            elseif ($line -match 'Merging formats|Merger') {
                                Emit-Event -EventType "Merge" -Text "" -Height "$height"
                            }
                            elseif ($line -match '^ERROR:\s*(.+)$') {
                                $lastError = $matches[1].Trim()
                            }
                        }

                        $exit = $LASTEXITCODE
                        $combined = $allLines -join "`n"

                        if ($exit -eq 0) {
                            $success = $true
                            break
                        }

                        if (-not $lastError) {
                            $lastError = "yt-dlp exit code $exit"
                        }

                        if ($combined -match 'Requested format is not available' -and $height -ne $qualities[-1]) {
                            Emit-Event -EventType "Fallback" -Text ("T{0} FALLBACK <= {1}p -> next quality" -f $slot, $height) -Height "$height"
                            continue
                        }

                        break
                    }
                }

                if ($success) {
                    Emit-Event -EventType "Done" -Text ("T{0} DONE #{1}/{2} q<={3} N={4} {5}" -f $slot, $index, $totalUrls, $finalHeight, $effectiveFragments, (ShortenInner -Text (Clean-DisplayTitleInner -Text $title) -Max 200)) -Height "$finalHeight"

                    [pscustomobject]@{
                        Kind = "Result"
                        Slot = $slot
                        Url = $url
                        Success = $true
                        Error = ""
                        Height = $finalHeight
                    }

                    return
                }

                Emit-Event -EventType "Error" -Text ("T{0} ERROR #{1}/{2} {3}" -f $slot, $index, $totalUrls, $lastError) -Height "$finalHeight"

                [pscustomobject]@{
                    Kind = "Result"
                    Slot = $slot
                    Url = $url
                    Success = $false
                    Error = $lastError
                    Height = $finalHeight
                }
            }
            catch {
                $message = $_.Exception.Message

                Emit-Event -EventType "Error" -Text ("T{0} EXCEPTION #{1}/{2} {3}" -f $slot, $index, $totalUrls, $message)

                [pscustomobject]@{
                    Kind = "Result"
                    Slot = $slot
                    Url = $url
                    Success = $false
                    Error = "ThreadJob exception: $message"
                    Height = ""
                }
            }
        }
    }

    Start-Sleep -Milliseconds 300

    foreach ($j in @($jobs)) {
        $jobErr = $null
        $messages = @(Receive-Job $j -ErrorVariable jobErr -ErrorAction SilentlyContinue)

        if ($jobErr) {
            foreach ($e in $jobErr) {
                Write-Log ("T{0} JOB-ERROR {1}" -f $j.Name, $e.ToString()) Red
            }
        }

        foreach ($msg in $messages) {
            Handle-Message -Message $msg -Results $results -Completed $completed -Urls $urls -Titles $titles -NoProgress:$NoProgress
        }
    }

    $done = @($jobs | Where-Object { $_.State -ne "Running" })

    foreach ($j in $done) {
        $slot = [int]$j.Name

        $messages = @(Receive-Job $j -ErrorAction SilentlyContinue)
        foreach ($msg in $messages) {
            Handle-Message -Message $msg -Results $results -Completed $completed -Urls $urls -Titles $titles -NoProgress:$NoProgress
        }

        if (-not $NoProgress) {
            Write-Progress -Id $slot -Activity ("T{0}" -f $slot) -Completed
        }

        Remove-Job $j -Force
        $jobs = @($jobs | Where-Object { $_.Id -ne $j.Id })
        $freeSlots.Enqueue($slot)
        $titles.Remove($slot)
    }
}

if (-not $NoProgress) {
    foreach ($slot in 1..$MaxParallel) {
        Write-Progress -Id $slot -Activity ("T{0}" -f $slot) -Completed
    }
}

$successFinal = ($results | Where-Object { $_.Success }).Count
$errorFinal = ($results | Where-Object { -not $_.Success }).Count
$remainingCount = 0

if (Test-Path $ListFile) {
    $remainingCount = @(Get-Content $ListFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
}

Write-Log ("Finished. Success={0}, Errors={1}, Remaining={2}" -f $successFinal, $errorFinal, $remainingCount) Cyan
