#requires -Version 7.0
if (-not $IsWindows) { throw "Windows only." }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class NativeUi {
    [DllImport("gdi32.dll", SetLastError=true)]
    public static extern IntPtr CreateRoundRectRgn(
        int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
        int nWidthEllipse, int nHeightEllipse);

    [DllImport("gdi32.dll", SetLastError=true)]
    public static extern bool DeleteObject(IntPtr hObject);
}
"@

[System.Windows.Forms.Application]::EnableVisualStyles()

$scriptPath = $MyInvocation.MyCommand.Path
if ($scriptPath -and [IO.Path]::GetExtension($scriptPath) -eq ".ps1") {
    $root = Split-Path -Parent $scriptPath
}
else {
    $root = [AppContext]::BaseDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar)
}

$engine = Join-Path $root "ytdl-manager-v8.ps1"
$ytDlp = Join-Path $root "yt-dlp.exe"
$iconPath = Join-Path $root "assets\app.ico"

function Remove-StaleGuiTempDirs {
    param([int]$OlderThanHours = 24)

    $cutoff = (Get-Date).AddHours(-$OlderThanHours)
    $tempRoot = [IO.Path]::GetTempPath()

    foreach ($dir in @(Get-ChildItem -Path $tempRoot -Directory -Filter "ytdl-gui-*" -ErrorAction SilentlyContinue)) {
        if ($dir.LastWriteTime -gt $cutoff) { continue }

        $ownerFile = Join-Path $dir.FullName "owner.pid"
        if (Test-Path $ownerFile) {
            try {
                $ownerPid = [int](Get-Content $ownerFile -ErrorAction Stop | Select-Object -First 1)
                if (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue) { continue }
            } catch {}
        }

        try {
            Remove-Item -Path $dir.FullName -Recurse -Force -ErrorAction Stop
        } catch {}
    }
}

Remove-StaleGuiTempDirs

$script:proc = $null
$script:runDir = $null
$script:runLog = $null
$script:runEvents = $null
$script:logLines = 0
$script:eventLines = 0
$script:total = 0
$script:done = 0
$script:successCount = 0
$script:errorCount = 0
$script:stopping = $false
$script:failedUrls = [Collections.Generic.HashSet[string]]::new()
$script:outputPaths = @{}
$script:speeds = @{}
$script:metadataPending = [Collections.Generic.Queue[string]]::new()
$script:metadataJobs = @{}
$script:updateJob = $null
$script:clipboardUrls = @()
$script:lastOutputDir = ""

$bg = [Drawing.Color]::FromArgb(18,20,25)
$panel = [Drawing.Color]::FromArgb(28,31,38)
$field = [Drawing.Color]::FromArgb(38,42,51)
$field2 = [Drawing.Color]::FromArgb(45,50,61)
$fg = [Drawing.Color]::FromArgb(238,241,247)
$muted = [Drawing.Color]::FromArgb(151,158,173)
$accent = [Drawing.Color]::FromArgb(99,102,241)
$danger = [Drawing.Color]::FromArgb(239,68,68)
$ok = [Drawing.Color]::FromArgb(34,197,94)
$warn = [Drawing.Color]::FromArgb(245,158,11)

function Label($value, $x, $y, $w=160, $size=9, $color=$fg) {
    $c = [Windows.Forms.Label]::new()
    $c.Text = $value
    $c.Location = [Drawing.Point]::new($x,$y)
    $c.Size = [Drawing.Size]::new($w,24)
    $c.Font = [Drawing.Font]::new("Segoe UI",$size)
    $c.ForeColor = $color
    $c.BackColor = [Drawing.Color]::Transparent
    return $c
}

function Button($value, $x, $y, $w=110, $color=$field) {
    $c = [Windows.Forms.Button]::new()
    $c.Text = $value
    $c.Location = [Drawing.Point]::new($x,$y)
    $c.Size = [Drawing.Size]::new($w,34)
    $c.FlatStyle = "Flat"
    $c.FlatAppearance.BorderSize = 0
    $c.BackColor = $color
    $c.ForeColor = $fg
    $c.Font = [Drawing.Font]::new("Segoe UI",9,[Drawing.FontStyle]::Semibold)
    $c.Cursor = "Hand"
    return $c
}

function StyleText($c) {
    $c.BackColor = $field
    $c.ForeColor = $fg
    $c.BorderStyle = "FixedSingle"
    $c.Font = [Drawing.Font]::new("Segoe UI",9)
}

function Set-RoundedRegion($control, $radius=14) {
    if (-not $control -or $control.Width -le 0 -or $control.Height -le 0) { return }

    try {
        $handle = [NativeUi]::CreateRoundRectRgn(0,0,$control.Width + 1,$control.Height + 1,$radius,$radius)
        if ($handle -eq [IntPtr]::Zero) { return }
        $region = [Drawing.Region]::FromHrgn($handle)
        if ($control.Region) { $control.Region.Dispose() }
        $control.Region = $region
        [void][NativeUi]::DeleteObject($handle)
    } catch {}
}

function Make-Rounded($control, $radius=14) {
    $control.Tag = $radius
    Set-RoundedRegion $control $radius
    $control.Add_SizeChanged({
        $r = if ($this.Tag) { [int]$this.Tag } else { 14 }
        Set-RoundedRegion $this $r
    })
}

function LogLine($line, $color=$muted) {
    if ([string]::IsNullOrWhiteSpace($line)) { return }
    $log.SelectionStart = $log.TextLength
    $log.SelectionColor = $color
    $log.AppendText($line + [Environment]::NewLine)
    $log.ScrollToCaret()
}

function Format-Duration($seconds) {
    if ($null -eq $seconds -or "$seconds" -eq "") { return "" }
    try {
        $ts = [TimeSpan]::FromSeconds([double]$seconds)
        if ($ts.TotalHours -ge 1) {
            return "{0}:{1:00}:{2:00}" -f [int]$ts.TotalHours,$ts.Minutes,$ts.Seconds
        }
        return "{0}:{1:00}" -f [int]$ts.TotalMinutes,$ts.Seconds
    } catch { return "" }
}

function Extract-UrlsFromText($text) {
    $result = [Collections.Generic.List[string]]::new()
    if ([string]::IsNullOrWhiteSpace($text)) { return @() }

    foreach ($m in [regex]::Matches($text, 'https?://[^\s<>"'']+')) {
        $value = $m.Value.Trim().TrimEnd([char[]]".,;)]}")
        $uri = $null
        if ([Uri]::TryCreate($value,[UriKind]::Absolute,[ref]$uri) -and ($uri.Scheme -eq "http" -or $uri.Scheme -eq "https")) {
            if (-not $result.Contains($value)) { [void]$result.Add($value) }
        }
    }

    return $result.ToArray()
}

function FindQueueRow($url) {
    foreach ($row in $queueGrid.Rows) {
        if ([string]$row.Cells["url"].Value -eq $url) { return $row }
    }
    return $null
}

function Add-QueueUrls($values) {
    $existing = [Collections.Generic.HashSet[string]]::new()
    foreach ($row in $queueGrid.Rows) {
        $v = [string]$row.Cells["url"].Value
        if ($v) { [void]$existing.Add($v) }
    }

    $added = 0
    $invalid = 0

    foreach ($raw in @($values)) {
        $found = @(Extract-UrlsFromText ([string]$raw))
        if ($found.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$raw)) {
            $invalid++
            continue
        }

        foreach ($url in $found) {
            if (-not $existing.Add($url)) { continue }

            $index = $queueGrid.Rows.Add("Проверка…","",$url,"","")
            $row = $queueGrid.Rows[$index]
            $row.Tag = @{
                Thumbnail = ""
                OutputPath = ""
                RawError = ""
            }
            $row.Cells["state"].Style.ForeColor = $muted
            $script:metadataPending.Enqueue($url)
            $added++
        }
    }

    if ($added -gt 0) {
        $queueHint.Text = "В очереди: $($queueGrid.Rows.Count). Метаданные подгружаются в фоне."
        $queueHint.ForeColor = $muted
        Pump-MetadataJobs
    }

    if ($invalid -gt 0) {
        $queueHint.Text = "Добавлено: $added · пропущено некорректных строк: $invalid"
        $queueHint.ForeColor = $warn
    }

    UpdateStats
}

function Pump-MetadataJobs {
    while ($script:metadataPending.Count -gt 0 -and $script:metadataJobs.Count -lt 3) {
        $url = $script:metadataPending.Dequeue()

        if (-not (Test-Path $ytDlp)) { return }

        $job = Start-ThreadJob -ArgumentList $ytDlp,$url -ScriptBlock {
            param($exe,$targetUrl)
            try {
                $raw = (& $exe --dump-single-json --skip-download --no-warnings --no-playlist $targetUrl 2>&1 | Out-String)
                if ($LASTEXITCODE -ne 0) {
                    return [pscustomobject]@{
                        Url = $targetUrl
                        Success = $false
                        Error = $raw.Trim()
                    }
                }

                $info = $raw | ConvertFrom-Json -ErrorAction Stop
                return [pscustomobject]@{
                    Url = $targetUrl
                    Success = $true
                    Title = [string]$info.title
                    Duration = $info.duration
                    Extractor = [string]$info.extractor_key
                    Thumbnail = [string]$info.thumbnail
                }
            }
            catch {
                return [pscustomobject]@{
                    Url = $targetUrl
                    Success = $false
                    Error = $_.Exception.Message
                }
            }
        }

        $script:metadataJobs[$job.Id] = $job
    }
}

function Poll-MetadataJobs {
    foreach ($id in @($script:metadataJobs.Keys)) {
        $job = $script:metadataJobs[$id]
        if ($job.State -eq "Running" -or $job.State -eq "NotStarted") { continue }

        $result = @(Receive-Job $job -ErrorAction SilentlyContinue | Select-Object -Last 1)
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        $script:metadataJobs.Remove($id)

        if ($result.Count -gt 0) {
            $item = $result[0]
            $row = FindQueueRow ([string]$item.Url)
            if ($row) {
                if ($item.Success) {
                    $row.Cells["state"].Value = "Готово к запуску"
                    $row.Cells["state"].Style.ForeColor = $ok
                    $row.Cells["title"].Value = [string]$item.Title
                    $row.Cells["duration"].Value = Format-Duration $item.Duration
                    $row.Cells["site"].Value = [string]$item.Extractor
                    if (-not $row.Tag) { $row.Tag = @{} }
                    $row.Tag.Thumbnail = [string]$item.Thumbnail
                }
                else {
                    $row.Cells["state"].Value = "URL принят"
                    $row.Cells["state"].Style.ForeColor = $warn
                    $row.ToolTipText = [string]$item.Error
                }
            }
        }
    }

    Pump-MetadataJobs
}

function RefreshPreview {
    if ($queueGrid.SelectedRows.Count -eq 0) {
        $preview.Image = $null
        $previewTitle.Text = "Выбери видео в очереди"
        $previewMeta.Text = ""
        return
    }

    $row = $queueGrid.SelectedRows[0]
    $title = [string]$row.Cells["title"].Value
    $url = [string]$row.Cells["url"].Value
    $duration = [string]$row.Cells["duration"].Value
    $site = [string]$row.Cells["site"].Value

    $previewTitle.Text = if ($title) { $title } else { $url }
    $previewMeta.Text = (@($site,$duration) | Where-Object { $_ }) -join "  ·  "

    try {
        $preview.CancelAsync()
        $preview.Image = $null
        $thumb = if ($row.Tag) { [string]$row.Tag.Thumbnail } else { "" }
        if ($thumb) {
            $preview.ImageLocation = $thumb
            $preview.LoadAsync()
        }
    } catch {}
}

function SwapQueueRows($first,$second) {
    if ($first -lt 0 -or $second -lt 0 -or $first -ge $queueGrid.Rows.Count -or $second -ge $queueGrid.Rows.Count) { return }

    $a = $queueGrid.Rows[$first]
    $b = $queueGrid.Rows[$second]
    $values = @()
    foreach ($cell in $a.Cells) { $values += $cell.Value }
    $tag = $a.Tag
    $tooltip = $a.ToolTipText

    for ($i=0; $i -lt $a.Cells.Count; $i++) {
        $a.Cells[$i].Value = $b.Cells[$i].Value
    }
    $a.Tag = $b.Tag
    $a.ToolTipText = $b.ToolTipText

    for ($i=0; $i -lt $b.Cells.Count; $i++) {
        $b.Cells[$i].Value = $values[$i]
    }
    $b.Tag = $tag
    $b.ToolTipText = $tooltip

    $queueGrid.ClearSelection()
    $queueGrid.Rows[$second].Selected = $true
}

function RemoveSelectedQueueRows {
    $indexes = @($queueGrid.SelectedRows | ForEach-Object { $_.Index } | Sort-Object -Descending)
    foreach ($index in $indexes) { $queueGrid.Rows.RemoveAt($index) }
    UpdateStats
    RefreshPreview
}

function RemoveQueueDuplicates {
    $seen = [Collections.Generic.HashSet[string]]::new()
    $removed = 0
    for ($i=$queueGrid.Rows.Count-1; $i -ge 0; $i--) {
        $url = [string]$queueGrid.Rows[$i].Cells["url"].Value
        if (-not $seen.Add($url)) {
            $queueGrid.Rows.RemoveAt($i)
            $removed++
        }
    }
    LogLine "Удалено дублей: $removed" $(if ($removed -gt 0) { $ok } else { $muted })
    UpdateStats
}

function Read-DroppedData($data) {
    $result = [Collections.Generic.List[string]]::new()

    if ($data.GetDataPresent([Windows.Forms.DataFormats]::FileDrop)) {
        foreach ($file in @($data.GetData([Windows.Forms.DataFormats]::FileDrop))) {
            if (-not (Test-Path $file -PathType Leaf)) { continue }

            try {
                if ([IO.Path]::GetExtension($file) -ieq ".url") {
                    $line = Get-Content $file -ErrorAction Stop | Where-Object { $_ -match '^URL=(.+)$' } | Select-Object -First 1
                    if ($line -match '^URL=(.+)$') { [void]$result.Add($matches[1]) }
                }
                else {
                    $text = Get-Content $file -Raw -Encoding UTF8 -ErrorAction Stop
                    [void]$result.Add($text)
                }
            } catch {
                LogLine ("Не удалось прочитать " + $file + ": " + $_.Exception.Message) $danger
            }
        }
    }

    foreach ($format in @([Windows.Forms.DataFormats]::UnicodeText,[Windows.Forms.DataFormats]::Text)) {
        if ($data.GetDataPresent($format)) {
            $text = [string]$data.GetData($format)
            if ($text) { [void]$result.Add($text) }
            break
        }
    }

    if ($result.Count -gt 0) { Add-QueueUrls $result.ToArray() }
}

function RefreshClipboardSuggestion {
    try {
        if (-not [Windows.Forms.Clipboard]::ContainsText()) {
            $clipboardButton.Visible = $false
            return
        }

        $script:clipboardUrls = @(Extract-UrlsFromText ([Windows.Forms.Clipboard]::GetText()))
        $existing = @($script:clipboardUrls | Where-Object { -not (FindQueueRow $_) })
        $script:clipboardUrls = $existing

        if ($script:clipboardUrls.Count -gt 0) {
            $clipboardButton.Text = if ($script:clipboardUrls.Count -eq 1) { "＋ Из буфера" } else { "＋ Из буфера ($($script:clipboardUrls.Count))" }
            $clipboardButton.Visible = $true
        }
        else {
            $clipboardButton.Visible = $false
        }
    } catch {
        $clipboardButton.Visible = $false
    }
}

function FriendlyError($raw) {
    $text = [string]$raw
    switch -Regex ($text) {
        'HTTP Error 403|Forbidden' { return "Доступ запрещён (403)" }
        'HTTP Error 429|Too Many Requests' { return "Слишком много запросов (429)" }
        'Sign in|login|cookies|age-restricted|confirm your age' { return "Нужна авторизация / cookies" }
        'Video unavailable|not available|has been removed|Private video' { return "Видео недоступно" }
        'Unsupported URL' { return "Ссылка не поддерживается" }
        'geo|country|region' { return "Географическое ограничение" }
        'timed out|timeout|Connection|network|Temporary failure' { return "Ошибка сети" }
        'Requested format is not available' { return "Выбранное качество недоступно" }
        'ffmpeg|ffprobe' { return "Проблема с FFmpeg" }
        default { return "Ошибка загрузки" }
    }
}

function Convert-SpeedToBytes($text) {
    if ([string]::IsNullOrWhiteSpace([string]$text)) { return 0.0 }

    if ([string]$text -match '([0-9.]+)\s*([KMGT]?i?B)/s') {
        $value = [double]$matches[1]
        switch -Regex ($matches[2]) {
            '^K' { return $value * 1KB }
            '^M' { return $value * 1MB }
            '^G' { return $value * 1GB }
            '^T' { return $value * 1TB }
            default { return $value }
        }
    }

    return 0.0
}

function Format-Speed($bytes) {
    if ($bytes -ge 1GB) { return "{0:N1} GB/s" -f ($bytes / 1GB) }
    if ($bytes -ge 1MB) { return "{0:N1} MB/s" -f ($bytes / 1MB) }
    if ($bytes -ge 1KB) { return "{0:N0} KB/s" -f ($bytes / 1KB) }
    return "0 KB/s"
}

function UpdateStats {
    $speed = 0.0
    foreach ($value in $script:speeds.Values) { $speed += [double]$value }
    $active = $script:speeds.Count

    $liveStats.Text = "Очередь $($queueGrid.Rows.Count)  ·  Активно $active  ·  $($script:done)/$($script:total)  ·  $(Format-Speed $speed)"

    if ($script:total -le 0) {
        $bar.Value = 0
        $progressText.Text = "0 / 0"
    }
    else {
        $p = [int](100 * $script:done / $script:total)
        $p = [Math]::Max(0,[Math]::Min(100,$p))
        $bar.Value = $p
        $progressText.Text = "$($script:done) / $($script:total)   $p%"
    }
}

function Slot($slot,$state,$value,$color) {
    $name = "T$slot"
    $row = $null
    foreach ($r in $activeGrid.Rows) {
        if ([string]$r.Cells[0].Value -eq $name) { $row = $r; break }
    }

    if ($null -eq $row) {
        $index = $activeGrid.Rows.Add($name,$state,$value)
        $row = $activeGrid.Rows[$index]
    }
    else {
        $row.Cells[1].Value = $state
        if ($value) { $row.Cells[2].Value = $value }
    }

    $row.Cells[1].Style.ForeColor = $color
}

function SlotState($slot,$state,$color) {
    Slot $slot $state "" $color
}

function SlotTitle($slot,$title) {
    $name = "T$slot"
    foreach ($r in $activeGrid.Rows) {
        if ([string]$r.Cells[0].Value -eq $name) {
            $r.Cells[2].Value = $title
            return
        }
    }
    [void]$activeGrid.Rows.Add($name,"Подготовка",$title)
}

function ParseLine($line) {
    if ($line -match 'T(?<slot>\d+) START #(?<idx>\d+)/(?<total>\d+) (?<value>.+)$') {
        Slot ([int]$matches.slot) "Скачивается" $matches.value $accent
        LogLine $line
        return
    }
    if ($line -match 'T(?<slot>\d+) DONE #(?<idx>\d+)/(?<total>\d+) (?<value>.+)$') {
        Slot ([int]$matches.slot) "Готово" $matches.value $ok
        LogLine $line $ok
        return
    }
    if ($line -match 'T(?<slot>\d+) ERROR #(?<idx>\d+)/(?<total>\d+) (?<value>.+)$') {
        Slot ([int]$matches.slot) "Ошибка" $matches.value $danger
        LogLine $line $danger
        return
    }
    if ($line -match 'FALLBACK|AUTO-FRAGMENTS') { LogLine $line $warn; return }
    LogLine $line
}

function TailLog {
    if (-not $script:runLog -or -not (Test-Path $script:runLog)) { return }
    try { $lines = @(Get-Content $script:runLog -Encoding UTF8 -ErrorAction Stop) } catch { return }
    for ($i=$script:logLines; $i -lt $lines.Count; $i++) { ParseLine $lines[$i] }
    $script:logLines = $lines.Count
}

function TailEvents {
    if (-not $script:runEvents -or -not (Test-Path $script:runEvents)) { return }
    try { $lines = @(Get-Content $script:runEvents -Encoding UTF8 -ErrorAction Stop) } catch { return }

    for ($i=$script:eventLines; $i -lt $lines.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
        try { $event = $lines[$i] | ConvertFrom-Json -ErrorAction Stop } catch { continue }

        if ($event.Kind -eq "Event") {
            $url = [string]$event.Url
            $row = if ($url) { FindQueueRow $url } else { $null }

            switch ($event.EventType) {
                "Title" {
                    if ($event.Title) {
                        SlotTitle ([int]$event.Slot) ([string]$event.Title)
                        if ($row) { $row.Cells["title"].Value = [string]$event.Title }
                    }
                }
                "Progress" {
                    $pct = "{0:N1}%" -f [double]$event.Percent
                    $parts = @($pct)
                    if ($event.Speed) { $parts += [string]$event.Speed }
                    if ($event.ETA) { $parts += ("ETA " + [string]$event.ETA) }
                    SlotState ([int]$event.Slot) ($parts -join "  ·  ") $accent
                    $script:speeds[[int]$event.Slot] = Convert-SpeedToBytes ([string]$event.Speed)
                    if ($row) {
                        $row.Cells["state"].Value = $pct
                        $row.Cells["state"].Style.ForeColor = $accent
                    }
                }
                "Merge" {
                    SlotState ([int]$event.Slot) "Склейка дорожек…" $warn
                    if ($row) {
                        $row.Cells["state"].Value = "Склейка…"
                        $row.Cells["state"].Style.ForeColor = $warn
                    }
                }
                "Done" {
                    $script:speeds.Remove([int]$event.Slot)
                }
                "Error" {
                    $script:speeds.Remove([int]$event.Slot)
                }
            }

            UpdateStats
            continue
        }

        if ($event.Kind -eq "Result") {
            $url = [string]$event.Url
            $row = FindQueueRow $url
            $script:done++

            if ($event.Success) {
                $script:successCount++
                [void]$script:failedUrls.Remove($url)
                if ($event.Path) { $script:outputPaths[$url] = [string]$event.Path }

                if ($row) {
                    $row.Cells["state"].Value = "Готово"
                    $row.Cells["state"].Style.ForeColor = $ok
                    if (-not $row.Tag) { $row.Tag = @{} }
                    $row.Tag.OutputPath = [string]$event.Path
                    $row.Tag.RawError = ""
                }
            }
            else {
                $script:errorCount++
                [void]$script:failedUrls.Add($url)
                $friendly = FriendlyError ([string]$event.Error)

                if ($row) {
                    $row.Cells["state"].Value = $friendly
                    $row.Cells["state"].Style.ForeColor = $danger
                    if (-not $row.Tag) { $row.Tag = @{} }
                    $row.Tag.RawError = [string]$event.Error
                    $row.ToolTipText = [string]$event.Error
                }

                LogLine ("Ошибка: " + $url + " — " + $friendly) $danger
            }

            UpdateStats
        }
    }

    $script:eventLines = $lines.Count
}

function Running($value) {
    $start.Enabled = -not $value
    $stop.Enabled = $value
    $retry.Enabled = (-not $value -and $script:failedUrls.Count -gt 0)

    foreach ($control in @($urlInput,$addUrl,$loadList,$removeQueue,$clearQueue,$dedupeQueue,$moveUp,$moveDown,$out,$threads,$fragments,$quality,$rateLimit,$archive,$sponsor,$audio,$cookies,$pickOut,$pickCookies,$updateYt)) {
        if ($control -is [Windows.Forms.TextBox]) { $control.ReadOnly = $value }
        else { $control.Enabled = -not $value }
    }

    $queueGrid.AllowDrop = -not $value
    $trayStart.Enabled = -not $value

    if ($value) {
        $status.Text = "● Работает"
        $status.ForeColor = $ok
    }
    else {
        $status.Text = "● Готов"
        $status.ForeColor = $muted
    }
}

function CleanTemp {
    if ($script:runDir -and (Test-Path $script:runDir)) {
        Remove-Item $script:runDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    $script:runDir = $null
    $script:runLog = $null
    $script:runEvents = $null
}

function StartDownload {
    param([string[]]$ItemsOverride)

    if ($script:proc -and -not $script:proc.HasExited) { return }

    if (-not (Test-Path $engine) -or -not (Test-Path $ytDlp)) {
        [Windows.Forms.MessageBox]::Show("Рядом с GUI должны лежать ytdl-manager-v8.ps1 и yt-dlp.exe.","Video Downloader") | Out-Null
        return
    }

    if ($ItemsOverride -and $ItemsOverride.Count -gt 0) {
        $items = @($ItemsOverride | Select-Object -Unique)
    }
    else {
        $items = @($queueGrid.Rows | ForEach-Object { [string]$_.Cells["url"].Value } | Where-Object { $_ } | Select-Object -Unique)
    }

    if ($items.Count -eq 0) {
        [Windows.Forms.MessageBox]::Show("Добавь хотя бы один URL.","Video Downloader") | Out-Null
        return
    }

    $dest = $out.Text.Trim()
    if (-not $dest) {
        $dest = Join-Path (Join-Path $env:USERPROFILE "Downloads") "downloaded-video"
        $out.Text = $dest
    }

    try {
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        $dest = (Resolve-Path $dest).Path
        $script:lastOutputDir = $dest
    } catch {
        [Windows.Forms.MessageBox]::Show("Не удалось открыть папку: " + $dest,"Video Downloader") | Out-Null
        return
    }

    $cookiePath = $cookies.Text.Trim()
    if ($cookiePath -and -not (Test-Path $cookiePath)) {
        [Windows.Forms.MessageBox]::Show("Cookies-файл не найден.","Video Downloader") | Out-Null
        return
    }

    $rate = $rateLimit.Text.Trim()
    if ($rate -and $rate -ne "Без лимита" -and $rate -notmatch '^\d+(\.\d+)?[KMG]?$') {
        [Windows.Forms.MessageBox]::Show("Лимит скорости: например 5M, 750K или оставь «Без лимита».","Video Downloader") | Out-Null
        return
    }

    CleanTemp
    $script:runDir = Join-Path ([IO.Path]::GetTempPath()) ("ytdl-gui-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:runDir -Force | Out-Null
    Set-Content -Path (Join-Path $script:runDir "owner.pid") -Value $PID -Encoding ASCII

    $queue = Join-Path $script:runDir "queue.txt"
    $script:runLog = Join-Path $script:runDir "run.log"
    $script:runEvents = Join-Path $script:runDir "events.jsonl"
    Set-Content $queue $items -Encoding UTF8

    $script:logLines = 0
    $script:eventLines = 0
    $script:total = $items.Count
    $script:done = 0
    $script:successCount = 0
    $script:errorCount = 0
    $script:stopping = $false
    $script:failedUrls.Clear()
    $script:speeds.Clear()
    $activeGrid.Rows.Clear()
    $log.Clear()

    foreach ($item in $items) {
        $row = FindQueueRow $item
        if ($row) {
            $row.Cells["state"].Value = "В очереди"
            $row.Cells["state"].Style.ForeColor = $muted
        }
    }

    UpdateStats

    $pwsh = Join-Path $PSHOME "pwsh.exe"
    if (-not (Test-Path $pwsh)) { $pwsh = "pwsh.exe" }

    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $pwsh
    $psi.WorkingDirectory = $root
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $resultDir = if ($archive.Checked) { Join-Path $dest (Get-Date -Format "yyyy-MM-dd") } else { $dest }
    $qualityValue = if ($quality.SelectedItem -eq "Best") { "Best" } else { ([string]$quality.SelectedItem).Replace("p","") }

    $args = @(
        "-NoProfile","-ExecutionPolicy","Bypass","-File",$engine,
        "-In",$queue,
        "-Out",$dest,
        "-Threads",[string][int]$threads.Value,
        "-NoProgress",
        "-Log",$script:runLog,
        "-EventFile",$script:runEvents,
        "-ResultDir",$resultDir,
        "-Quality",$qualityValue
    )

    if ($rate -and $rate -ne "Без лимита") {
        $args += @("-RateLimit",$rate)
    }

    foreach ($arg in $args) { [void]$psi.ArgumentList.Add($arg) }

    if ($fragments.SelectedIndex -eq 0) {
        [void]$psi.ArgumentList.Add("-AutoFragments")
    }
    else {
        [void]$psi.ArgumentList.Add("-Fragments")
        [void]$psi.ArgumentList.Add([string]$fragments.SelectedItem)
    }

    if ($archive.Checked) { [void]$psi.ArgumentList.Add("-Archive") }
    if ($sponsor.Checked) { [void]$psi.ArgumentList.Add("-SponsorBlock") }
    if ($audio.Checked) { [void]$psi.ArgumentList.Add("-AudioOnly") }

    if ($cookiePath) {
        [void]$psi.ArgumentList.Add("-Cookies")
        [void]$psi.ArgumentList.Add((Resolve-Path $cookiePath).Path)
    }

    try {
        $script:proc = [Diagnostics.Process]::Start($psi)
        Running $true
        LogLine "Запуск: $($items.Count) URL · качество $qualityValue · потоков $([int]$threads.Value) · папка $dest" $accent
    }
    catch {
        CleanTemp
        $script:proc = $null
        Running $false
        [Windows.Forms.MessageBox]::Show("Ошибка запуска: " + $_.Exception.Message,"Video Downloader") | Out-Null
    }
}

function StopDownload {
    if (-not $script:proc -or $script:proc.HasExited) { return }
    $script:stopping = $true
    $status.Text = "● Остановка"
    try { $script:proc.Kill($true) } catch { LogLine $_.Exception.Message $danger }
}

function RetryFailed {
    if ($script:failedUrls.Count -eq 0) { return }
    $failed = @($script:failedUrls)
    StartDownload -ItemsOverride $failed
}

function ShowCompletionNotification($success,$errors) {
    try {
        $tray.BalloonTipTitle = "Video Downloader"

        if ($errors -eq 0) {
            $tray.BalloonTipIcon = [Windows.Forms.ToolTipIcon]::Info
            $tray.BalloonTipText = "Готово: $success видео. Нажми уведомление, чтобы открыть папку."
        }
        else {
            $tray.BalloonTipIcon = [Windows.Forms.ToolTipIcon]::Warning
            $tray.BalloonTipText = "Завершено: $success успешно, $errors с ошибками. Нажми, чтобы открыть папку."
        }

        $tray.ShowBalloonTip(7000)
    } catch {}
}

function FinishDownload {
    TailEvents
    TailLog

    $code = $null
    try { $code = $script:proc.ExitCode } catch {}
    try { $script:proc.Dispose() } catch {}
    $script:proc = $null
    $script:speeds.Clear()

    Running $false

    if ($script:stopping) {
        $status.Text = "● Остановлено"
        $status.ForeColor = $warn
        LogLine "Остановлено пользователем." $warn
    }
    elseif ($code -eq 0 -and $script:errorCount -eq 0) {
        $status.Text = "● Завершено"
        $status.ForeColor = $ok
        LogLine "Загрузка завершена. Успешно: $($script:successCount)." $ok
        try { [System.Media.SystemSounds]::Asterisk.Play() } catch {}
        ShowCompletionNotification $script:successCount 0
    }
    else {
        $status.Text = if ($script:errorCount -gt 0) { "● Есть ошибки" } else { "● Ошибка" }
        $status.ForeColor = $danger
        LogLine "Завершено. Успешно=$($script:successCount), ошибок=$($script:errorCount), exit=$code." $danger
        try { [System.Media.SystemSounds]::Hand.Play() } catch {}
        ShowCompletionNotification $script:successCount $script:errorCount
    }

    $retry.Enabled = $script:failedUrls.Count -gt 0
    $script:stopping = $false
    UpdateStats
    CleanTemp
}

function OpenSelectedFile {
    if ($queueGrid.SelectedRows.Count -eq 0) { return }
    $row = $queueGrid.SelectedRows[0]
    $path = if ($row.Tag) { [string]$row.Tag.OutputPath } else { "" }
    if ($path -and (Test-Path $path)) { Start-Process $path }
}

function RevealSelectedFile {
    if ($queueGrid.SelectedRows.Count -eq 0) { return }
    $row = $queueGrid.SelectedRows[0]
    $path = if ($row.Tag) { [string]$row.Tag.OutputPath } else { "" }

    if ($path -and (Test-Path $path)) {
        Start-Process explorer.exe -ArgumentList @("/select," + '"' + $path + '"')
    }
    elseif ($script:lastOutputDir -and (Test-Path $script:lastOutputDir)) {
        Start-Process explorer.exe -ArgumentList @($script:lastOutputDir)
    }
}

function CopySelectedPath {
    if ($queueGrid.SelectedRows.Count -eq 0) { return }
    $row = $queueGrid.SelectedRows[0]
    $path = if ($row.Tag) { [string]$row.Tag.OutputPath } else { "" }
    if ($path) { [Windows.Forms.Clipboard]::SetText($path) }
}

function Get-YtDlpVersion {
    if (-not (Test-Path $ytDlp)) {
        $ytVersion.Text = "yt-dlp: не найден"
        $ytVersion.ForeColor = $danger
        return
    }

    try {
        $version = (& $ytDlp --version 2>$null | Select-Object -First 1)
        $ytVersion.Text = "yt-dlp: $version"
        $ytVersion.ForeColor = $muted
    }
    catch {
        $ytVersion.Text = "yt-dlp: ошибка версии"
        $ytVersion.ForeColor = $danger
    }
}

function Start-YtDlpUpdate {
    if ($script:updateJob -or $script:proc) { return }
    $updateYt.Enabled = $false
    $updateYt.Text = "Обновляю…"

    $script:updateJob = Start-ThreadJob -ArgumentList $ytDlp -ScriptBlock {
        param($exe)
        try {
            $text = (& $exe -U 2>&1 | Out-String).Trim()
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = $text }
        }
        catch {
            [pscustomobject]@{ ExitCode = 1; Text = $_.Exception.Message }
        }
    }
}

function Poll-YtDlpUpdate {
    if (-not $script:updateJob) { return }
    if ($script:updateJob.State -eq "Running" -or $script:updateJob.State -eq "NotStarted") { return }

    $result = @(Receive-Job $script:updateJob -ErrorAction SilentlyContinue | Select-Object -Last 1)
    Remove-Job $script:updateJob -Force -ErrorAction SilentlyContinue
    $script:updateJob = $null
    $updateYt.Enabled = -not ($script:proc -and -not $script:proc.HasExited)
    $updateYt.Text = "Обновить"

    if ($result.Count -gt 0) {
        $item = $result[0]
        if ([int]$item.ExitCode -eq 0) {
            LogLine ([string]$item.Text) $ok
        }
        else {
            LogLine ([string]$item.Text) $danger
        }
    }

    Get-YtDlpVersion
}

$form = [Windows.Forms.Form]::new()
$form.Text = "Video Downloader"
$form.Size = [Drawing.Size]::new(1320,900)
$form.MinimumSize = [Drawing.Size]::new(1180,820)
$form.StartPosition = "CenterScreen"
$form.BackColor = $bg
$form.ForeColor = $fg
$form.Font = [Drawing.Font]::new("Segoe UI",9)
$form.KeyPreview = $true
$form.AllowDrop = $true

$appIcon = $null
try {
    if (Test-Path $iconPath) {
        $appIcon = [Drawing.Icon]::new($iconPath)
        $form.Icon = $appIcon
    }
} catch {}

$head = Label "Video Downloader" 24 16 360 18 $fg
$head.Font = [Drawing.Font]::new("Segoe UI",18,[Drawing.FontStyle]::Bold)
$head.Height = 34
$form.Controls.Add($head)

$liveStats = Label "Очередь 0  ·  Активно 0  ·  0/0  ·  0 KB/s" 26 52 650 9 $muted
$form.Controls.Add($liveStats)

$status = Label "● Готов" 1135 22 140 9 $muted
$status.Anchor = "Top,Right"
$status.BackColor = $field
$status.TextAlign = "MiddleCenter"
$status.Height = 30
Make-Rounded $status 16
$form.Controls.Add($status)

$left = [Windows.Forms.Panel]::new()
$left.Location = [Drawing.Point]::new(22,88)
$left.Size = [Drawing.Size]::new(820,742)
$left.Anchor = "Top,Bottom,Left,Right"
$left.BackColor = $panel
Make-Rounded $left 22
$form.Controls.Add($left)

$right = [Windows.Forms.Panel]::new()
$right.Location = [Drawing.Point]::new(858,88)
$right.Size = [Drawing.Size]::new(420,742)
$right.Anchor = "Top,Bottom,Right"
$right.BackColor = $panel
Make-Rounded $right 22
$form.Controls.Add($right)

$left.Controls.Add((Label "Очередь" 16 12 120 10 $fg))

$urlInput = [Windows.Forms.TextBox]::new()
$urlInput.Location = [Drawing.Point]::new(16,42)
$urlInput.Size = [Drawing.Size]::new(445,28)
$urlInput.Anchor = "Top,Left,Right"
StyleText $urlInput
$left.Controls.Add($urlInput)

$addUrl = Button "＋ Добавить" 470 40 105 $accent
$left.Controls.Add($addUrl)

$loadList = Button "Открыть файл" 583 40 105
$left.Controls.Add($loadList)

$clipboardButton = Button "＋ Из буфера" 696 40 105 $field2
$clipboardButton.Visible = $false
$left.Controls.Add($clipboardButton)

$queueGrid = [Windows.Forms.DataGridView]::new()
$queueGrid.Location = [Drawing.Point]::new(16,82)
$queueGrid.Size = [Drawing.Size]::new(785,248)
$queueGrid.Anchor = "Top,Left,Right"
$queueGrid.BackgroundColor = $field
$queueGrid.BorderStyle = "None"
$queueGrid.RowHeadersVisible = $false
$queueGrid.AllowUserToAddRows = $false
$queueGrid.AllowUserToDeleteRows = $false
$queueGrid.ReadOnly = $true
$queueGrid.MultiSelect = $true
$queueGrid.SelectionMode = "FullRowSelect"
$queueGrid.AllowDrop = $true
$queueGrid.EnableHeadersVisualStyles = $false
$queueGrid.ColumnHeadersDefaultCellStyle.BackColor = $bg
$queueGrid.ColumnHeadersDefaultCellStyle.ForeColor = $muted
$queueGrid.DefaultCellStyle.BackColor = $field
$queueGrid.DefaultCellStyle.ForeColor = $fg
$queueGrid.DefaultCellStyle.SelectionBackColor = [Drawing.Color]::FromArgb(55,60,75)
$queueGrid.DefaultCellStyle.SelectionForeColor = $fg
$queueGrid.CellBorderStyle = "SingleHorizontal"
$queueGrid.ColumnHeadersBorderStyle = "None"
$queueGrid.RowTemplate.Height = 30
[void]$queueGrid.Columns.Add("state","Статус")
[void]$queueGrid.Columns.Add("title","Название")
[void]$queueGrid.Columns.Add("url","URL")
[void]$queueGrid.Columns.Add("duration","Длина")
[void]$queueGrid.Columns.Add("site","Сайт")
$queueGrid.Columns["state"].Width = 130
$queueGrid.Columns["title"].Width = 230
$queueGrid.Columns["url"].AutoSizeMode = "Fill"
$queueGrid.Columns["duration"].Width = 70
$queueGrid.Columns["site"].Width = 90
$left.Controls.Add($queueGrid)

$queueHint = Label "Можно перетащить .txt, .url или ссылку прямо из браузера" 18 334 520 8.5 $muted
$left.Controls.Add($queueHint)

$removeQueue = Button "Удалить" 16 360 92
$clearQueue = Button "Очистить" 116 360 92
$dedupeQueue = Button "Убрать дубли" 216 360 118
$moveUp = Button "↑ Выше" 342 360 92
$moveDown = Button "↓ Ниже" 442 360 92
foreach ($b in @($removeQueue,$clearQueue,$dedupeQueue,$moveUp,$moveDown)) { Make-Rounded $b 10; $left.Controls.Add($b) }

$left.Controls.Add((Label "Активные загрузки" 16 407 200 10 $fg))

$activeGrid = [Windows.Forms.DataGridView]::new()
$activeGrid.Location = [Drawing.Point]::new(16,438)
$activeGrid.Size = [Drawing.Size]::new(785,130)
$activeGrid.Anchor = "Top,Left,Right"
$activeGrid.BackgroundColor = $field
$activeGrid.BorderStyle = "None"
$activeGrid.RowHeadersVisible = $false
$activeGrid.AllowUserToAddRows = $false
$activeGrid.ReadOnly = $true
$activeGrid.EnableHeadersVisualStyles = $false
$activeGrid.ColumnHeadersDefaultCellStyle.BackColor = $bg
$activeGrid.ColumnHeadersDefaultCellStyle.ForeColor = $muted
$activeGrid.DefaultCellStyle.BackColor = $field
$activeGrid.DefaultCellStyle.ForeColor = $fg
$activeGrid.DefaultCellStyle.SelectionBackColor = [Drawing.Color]::FromArgb(55,60,75)
$activeGrid.DefaultCellStyle.SelectionForeColor = $fg
$activeGrid.CellBorderStyle = "SingleHorizontal"
$activeGrid.ColumnHeadersBorderStyle = "None"
[void]$activeGrid.Columns.Add("slot","Слот")
[void]$activeGrid.Columns.Add("state","Статус")
[void]$activeGrid.Columns.Add("value","Видео / URL")
$activeGrid.Columns[0].Width = 55
$activeGrid.Columns[1].Width = 230
$activeGrid.Columns[2].AutoSizeMode = "Fill"
$left.Controls.Add($activeGrid)

$left.Controls.Add((Label "Лог" 16 584 100 10 $fg))
$log = [Windows.Forms.RichTextBox]::new()
$log.Location = [Drawing.Point]::new(16,613)
$log.Size = [Drawing.Size]::new(785,110)
$log.Anchor = "Top,Bottom,Left,Right"
$log.ReadOnly = $true
$log.BackColor = [Drawing.Color]::FromArgb(14,16,20)
$log.ForeColor = $muted
$log.BorderStyle = "None"
$log.Font = [Drawing.Font]::new("Cascadia Mono",8.5)
$left.Controls.Add($log)

$preview = [Windows.Forms.PictureBox]::new()
$preview.Location = [Drawing.Point]::new(16,16)
$preview.Size = [Drawing.Size]::new(388,178)
$preview.SizeMode = "Zoom"
$preview.BackColor = $bg
Make-Rounded $preview 18
$right.Controls.Add($preview)

$previewTitle = Label "Выбери видео в очереди" 18 204 384 10 $fg
$previewTitle.Height = 42
$previewTitle.AutoEllipsis = $true
$right.Controls.Add($previewTitle)

$previewMeta = Label "" 18 244 384 8.5 $muted
$right.Controls.Add($previewMeta)

$right.Controls.Add((Label "Настройки" 16 278 160 10 $fg))
$right.Controls.Add((Label "Папка" 16 310 80 9 $muted))

$out = [Windows.Forms.TextBox]::new()
$out.Location = [Drawing.Point]::new(16,334)
$out.Size = [Drawing.Size]::new(330,27)
$out.Text = Join-Path (Join-Path $env:USERPROFILE "Downloads") "downloaded-video"
StyleText $out
$right.Controls.Add($out)

$pickOut = Button "…" 354 333 48
Make-Rounded $pickOut 10
$right.Controls.Add($pickOut)

$right.Controls.Add((Label "Качество" 16 374 110 9 $muted))
$quality = [Windows.Forms.ComboBox]::new()
$quality.Location = [Drawing.Point]::new(120,371)
$quality.Size = [Drawing.Size]::new(92,27)
$quality.DropDownStyle = "DropDownList"
$quality.BackColor = $field
$quality.ForeColor = $fg
foreach ($v in @("Best","2160p","1440p","1080p","720p")) { [void]$quality.Items.Add($v) }
$quality.SelectedIndex = 0
$right.Controls.Add($quality)

$right.Controls.Add((Label "Лимит" 226 374 60 9 $muted))
$rateLimit = [Windows.Forms.ComboBox]::new()
$rateLimit.Location = [Drawing.Point]::new(284,371)
$rateLimit.Size = [Drawing.Size]::new(118,27)
$rateLimit.DropDownStyle = "DropDown"
$rateLimit.BackColor = $field
$rateLimit.ForeColor = $fg
foreach ($v in @("Без лимита","1M","5M","10M","25M","50M")) { [void]$rateLimit.Items.Add($v) }
$rateLimit.Text = "Без лимита"
$right.Controls.Add($rateLimit)

$right.Controls.Add((Label "Параллельные URL" 16 414 160 9 $muted))
$threads = [Windows.Forms.NumericUpDown]::new()
$threads.Location = [Drawing.Point]::new(170,411)
$threads.Size = [Drawing.Size]::new(75,27)
$threads.Minimum = 1
$threads.Maximum = 32
$threads.Value = 4
$threads.BackColor = $field
$threads.ForeColor = $fg
$right.Controls.Add($threads)

$right.Controls.Add((Label "Фрагменты" 260 414 90 9 $muted))
$fragments = [Windows.Forms.ComboBox]::new()
$fragments.Location = [Drawing.Point]::new(340,411)
$fragments.Size = [Drawing.Size]::new(62,27)
$fragments.DropDownStyle = "DropDownList"
$fragments.BackColor = $field
$fragments.ForeColor = $fg
[void]$fragments.Items.Add("Авто")
1..4 | ForEach-Object { [void]$fragments.Items.Add([string]$_) }
$fragments.SelectedIndex = 0
$right.Controls.Add($fragments)

$archive = [Windows.Forms.CheckBox]::new()
$archive.Text = "Архив по дате"
$archive.Location = [Drawing.Point]::new(16,451)
$archive.Size = [Drawing.Size]::new(150,24)
$archive.ForeColor = $fg
$right.Controls.Add($archive)

$sponsor = [Windows.Forms.CheckBox]::new()
$sponsor.Text = "Удалять SponsorBlock"
$sponsor.Location = [Drawing.Point]::new(170,451)
$sponsor.Size = [Drawing.Size]::new(190,24)
$sponsor.ForeColor = $fg
$right.Controls.Add($sponsor)

$audio = [Windows.Forms.CheckBox]::new()
$audio.Text = "Только аудио (MP3)"
$audio.Location = [Drawing.Point]::new(16,480)
$audio.Size = [Drawing.Size]::new(180,24)
$audio.ForeColor = $fg
$right.Controls.Add($audio)

$right.Controls.Add((Label "Cookies (необязательно)" 16 515 200 9 $muted))
$cookies = [Windows.Forms.TextBox]::new()
$cookies.Location = [Drawing.Point]::new(16,539)
$cookies.Size = [Drawing.Size]::new(330,27)
StyleText $cookies
$right.Controls.Add($cookies)

$pickCookies = Button "…" 354 538 48
Make-Rounded $pickCookies 10
$right.Controls.Add($pickCookies)

$ytVersion = Label "yt-dlp: …" 16 577 245 8.5 $muted
$right.Controls.Add($ytVersion)
$updateYt = Button "Обновить" 296 574 106 $field2
$updateYt.Height = 30
Make-Rounded $updateYt 10
$right.Controls.Add($updateYt)

$right.Controls.Add((Label "Общий прогресс" 16 615 150 9 $muted))
$progressText = Label "0 / 0" 276 615 126 9 $muted
$progressText.TextAlign = "MiddleRight"
$right.Controls.Add($progressText)

$bar = [Windows.Forms.ProgressBar]::new()
$bar.Location = [Drawing.Point]::new(16,642)
$bar.Size = [Drawing.Size]::new(386,18)
$right.Controls.Add($bar)

$start = Button "▶  Начать загрузку" 16 675 386 $accent
$start.Size = [Drawing.Size]::new(386,42)
Make-Rounded $start 14
$right.Controls.Add($start)

$stop = Button "■ Стоп" 16 724 112 $danger
$stop.Enabled = $false
Make-Rounded $stop 12
$right.Controls.Add($stop)

$retry = Button "↻ Ошибки" 136 724 112 $warn
$retry.Enabled = $false
Make-Rounded $retry 12
$right.Controls.Add($retry)

$openFolder = Button "Папка" 256 724 146
Make-Rounded $openFolder 12
$right.Controls.Add($openFolder)

$folderDialog = [Windows.Forms.FolderBrowserDialog]::new()
$fileDialog = [Windows.Forms.OpenFileDialog]::new()

$queueMenu = [Windows.Forms.ContextMenuStrip]::new()
$menuOpen = [Windows.Forms.ToolStripMenuItem]::new("Открыть файл")
$menuReveal = [Windows.Forms.ToolStripMenuItem]::new("Показать в папке")
$menuCopy = [Windows.Forms.ToolStripMenuItem]::new("Скопировать путь")
$menuError = [Windows.Forms.ToolStripMenuItem]::new("Показать техническую ошибку")
[void]$queueMenu.Items.AddRange(@($menuOpen,$menuReveal,$menuCopy,$menuError))
$queueGrid.ContextMenuStrip = $queueMenu

$tray = [Windows.Forms.NotifyIcon]::new()
$tray.Text = "Video Downloader"
$tray.Visible = $true
if ($appIcon) { $tray.Icon = $appIcon } else { $tray.Icon = [Drawing.SystemIcons]::Application }

$trayMenu = [Windows.Forms.ContextMenuStrip]::new()
$trayOpen = [Windows.Forms.ToolStripMenuItem]::new("Открыть")
$trayStart = [Windows.Forms.ToolStripMenuItem]::new("Начать загрузку")
$trayExit = [Windows.Forms.ToolStripMenuItem]::new("Выход")
[void]$trayMenu.Items.AddRange(@($trayOpen,$trayStart,$trayExit))
$tray.ContextMenuStrip = $trayMenu

$addUrl.Add_Click({
    Add-QueueUrls @($urlInput.Text)
    $urlInput.Clear()
})

$urlInput.Add_KeyDown({
    param($sender,$e)
    if ($e.KeyCode -eq [Windows.Forms.Keys]::Enter) {
        Add-QueueUrls @($urlInput.Text)
        $urlInput.Clear()
        $e.SuppressKeyPress = $true
    }
})

$loadList.Add_Click({
    $fileDialog.Filter = "URL/text files (*.txt;*.url)|*.txt;*.url|All files (*.*)|*.*"
    if ($fileDialog.ShowDialog() -eq "OK") {
        Read-DroppedData ([Windows.Forms.DataObject]::new([Windows.Forms.DataFormats]::FileDrop,@($fileDialog.FileName)))
    }
})

$clipboardButton.Add_Click({
    Add-QueueUrls $script:clipboardUrls
    $clipboardButton.Visible = $false
})

$removeQueue.Add_Click({ RemoveSelectedQueueRows })
$clearQueue.Add_Click({
    $queueGrid.Rows.Clear()
    $queueHint.Text = "Очередь очищена"
    UpdateStats
    RefreshPreview
})
$dedupeQueue.Add_Click({ RemoveQueueDuplicates })
$moveUp.Add_Click({
    if ($queueGrid.SelectedRows.Count -eq 1) {
        $i = $queueGrid.SelectedRows[0].Index
        if ($i -gt 0) { SwapQueueRows $i ($i-1) }
    }
})
$moveDown.Add_Click({
    if ($queueGrid.SelectedRows.Count -eq 1) {
        $i = $queueGrid.SelectedRows[0].Index
        if ($i -lt $queueGrid.Rows.Count-1) { SwapQueueRows $i ($i+1) }
    }
})

$queueGrid.Add_SelectionChanged({ RefreshPreview })
$queueGrid.Add_CellDoubleClick({ OpenSelectedFile })
$queueGrid.Add_CellMouseDown({
    param($sender,$e)
    if ($e.Button -eq [Windows.Forms.MouseButtons]::Right -and $e.RowIndex -ge 0) {
        $queueGrid.ClearSelection()
        $queueGrid.Rows[$e.RowIndex].Selected = $true
    }
})

$queueGrid.Add_DragEnter({
    param($sender,$e)
    if ($e.Data.GetDataPresent([Windows.Forms.DataFormats]::FileDrop) -or
        $e.Data.GetDataPresent([Windows.Forms.DataFormats]::UnicodeText) -or
        $e.Data.GetDataPresent([Windows.Forms.DataFormats]::Text)) {
        $e.Effect = [Windows.Forms.DragDropEffects]::Copy
        $queueGrid.BackgroundColor = [Drawing.Color]::FromArgb(54,60,78)
        $queueHint.Text = "Отпусти — добавлю ссылки в очередь"
        $queueHint.ForeColor = $accent
    }
})

$queueGrid.Add_DragLeave({
    $queueGrid.BackgroundColor = $field
    $queueHint.Text = "Можно перетащить .txt, .url или ссылку прямо из браузера"
    $queueHint.ForeColor = $muted
})

$queueGrid.Add_DragDrop({
    param($sender,$e)
    $queueGrid.BackgroundColor = $field
    $queueHint.Text = "Можно перетащить .txt, .url или ссылку прямо из браузера"
    $queueHint.ForeColor = $muted
    Read-DroppedData $e.Data
})

$form.Add_DragEnter({
    param($sender,$e)
    if ($e.Data.GetDataPresent([Windows.Forms.DataFormats]::FileDrop) -or
        $e.Data.GetDataPresent([Windows.Forms.DataFormats]::UnicodeText) -or
        $e.Data.GetDataPresent([Windows.Forms.DataFormats]::Text)) {
        $e.Effect = [Windows.Forms.DragDropEffects]::Copy
    }
})
$form.Add_DragDrop({ param($sender,$e) Read-DroppedData $e.Data })

$pickOut.Add_Click({
    $folderDialog.SelectedPath = $out.Text
    if ($folderDialog.ShowDialog() -eq "OK") { $out.Text = $folderDialog.SelectedPath }
})

$pickCookies.Add_Click({
    $fileDialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
    if ($fileDialog.ShowDialog() -eq "OK") { $cookies.Text = $fileDialog.FileName }
})

$start.Add_Click({ StartDownload })
$stop.Add_Click({ StopDownload })
$retry.Add_Click({ RetryFailed })
$openFolder.Add_Click({
    $path = $out.Text.Trim()
    if ($path -and (Test-Path $path)) { Start-Process explorer.exe -ArgumentList @($path) }
})
$updateYt.Add_Click({ Start-YtDlpUpdate })

$menuOpen.Add_Click({ OpenSelectedFile })
$menuReveal.Add_Click({ RevealSelectedFile })
$menuCopy.Add_Click({ CopySelectedPath })
$menuError.Add_Click({
    if ($queueGrid.SelectedRows.Count -eq 0) { return }
    $row = $queueGrid.SelectedRows[0]
    $raw = if ($row.Tag) { [string]$row.Tag.RawError } else { "" }
    if (-not $raw) { $raw = "Для этой строки технической ошибки нет." }
    [Windows.Forms.MessageBox]::Show($raw,"Техническая ошибка") | Out-Null
})

$trayOpen.Add_Click({
    $form.Show()
    $form.WindowState = "Normal"
    $form.Activate()
})
$trayStart.Add_Click({ StartDownload })
$trayExit.Add_Click({ $form.Close() })
$tray.Add_DoubleClick({
    $form.Show()
    $form.WindowState = "Normal"
    $form.Activate()
})
$tray.Add_BalloonTipClicked({
    if ($script:lastOutputDir -and (Test-Path $script:lastOutputDir)) {
        Start-Process explorer.exe -ArgumentList @($script:lastOutputDir)
    }
})

$form.Add_Resize({
    if ($form.WindowState -eq [Windows.Forms.FormWindowState]::Minimized) {
        $form.Hide()
        $tray.BalloonTipTitle = "Video Downloader"
        $tray.BalloonTipText = "Приложение продолжает работать в трее."
        $tray.BalloonTipIcon = [Windows.Forms.ToolTipIcon]::Info
        $tray.ShowBalloonTip(2500)
    }
})

$form.Add_Activated({ RefreshClipboardSuggestion })

$form.Add_KeyDown({
    param($sender,$e)
    if ($e.Control -and $e.KeyCode -eq [Windows.Forms.Keys]::Enter) {
        StartDownload
        $e.SuppressKeyPress = $true
    }
})

$timer = [Windows.Forms.Timer]::new()
$timer.Interval = 400
$timer.Add_Tick({
    Poll-MetadataJobs
    Poll-YtDlpUpdate

    if ($script:proc) {
        TailEvents
        TailLog
        try {
            if ($script:proc.HasExited) { FinishDownload }
        } catch {}
    }
})
$timer.Start()

$form.Add_Shown({
    Get-YtDlpVersion
    RefreshClipboardSuggestion
})

$form.Add_FormClosing({
    param($sender,$e)

    if ($script:proc -and -not $script:proc.HasExited) {
        $answer = [Windows.Forms.MessageBox]::Show(
            "Идёт загрузка. Остановить её и закрыть программу?",
            "Video Downloader",
            "YesNo",
            "Warning"
        )
        if ($answer -ne "Yes") {
            $e.Cancel = $true
            return
        }
        try { $script:proc.Kill($true) } catch {}
    }

    foreach ($job in @($script:metadataJobs.Values)) {
        try { Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue } catch {}
    }
    if ($script:updateJob) {
        try { Stop-Job $script:updateJob -ErrorAction SilentlyContinue; Remove-Job $script:updateJob -Force -ErrorAction SilentlyContinue } catch {}
    }

    $timer.Stop()
    $tray.Visible = $false
    $tray.Dispose()
    CleanTemp
    if ($appIcon) { $appIcon.Dispose() }
})

[void]$form.ShowDialog()
