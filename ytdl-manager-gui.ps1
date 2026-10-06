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

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$engine = Join-Path $root "ytdl-manager-v8.ps1"
$ytDlp = Join-Path $root "yt-dlp.exe"

function Remove-StaleGuiTempDirs {
    param([int]$OlderThanHours = 24)

    $cutoff = (Get-Date).AddHours(-$OlderThanHours)
    $tempRoot = [IO.Path]::GetTempPath()

    foreach ($dir in @(Get-ChildItem -Path $tempRoot -Directory -Filter "ytdl-gui-*" -ErrorAction SilentlyContinue)) {
        if ($dir.LastWriteTime -gt $cutoff) { continue }

        try {
            Remove-Item -Path $dir.FullName -Recurse -Force -ErrorAction Stop
        } catch {
            # A stale directory can still be locked by another process. Ignore it.
        }
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
$script:stopping = $false

$bg = [Drawing.Color]::FromArgb(22,24,29)
$panel = [Drawing.Color]::FromArgb(31,34,41)
$field = [Drawing.Color]::FromArgb(39,43,52)
$fg = [Drawing.Color]::FromArgb(235,238,245)
$muted = [Drawing.Color]::FromArgb(157,164,178)
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
        $handle = [NativeUi]::CreateRoundRectRgn(
            0, 0, $control.Width + 1, $control.Height + 1, $radius, $radius
        )

        if ($handle -eq [IntPtr]::Zero) { return }

        $region = [Drawing.Region]::FromHrgn($handle)
        if ($control.Region) { $control.Region.Dispose() }
        $control.Region = $region
        [void][NativeUi]::DeleteObject($handle)
    } catch {}
}

function Make-Rounded($control, $radius=14) {
    Set-RoundedRegion $control $radius
    $control.Add_SizeChanged({
        Set-RoundedRegion $this $radius
    })
}

function LogLine($line, $color=$muted) {
    if ([string]::IsNullOrWhiteSpace($line)) { return }
    $log.SelectionStart = $log.TextLength
    $log.SelectionColor = $color
    $log.AppendText($line + [Environment]::NewLine)
    $log.ScrollToCaret()
}

function Slot($slot, $state, $value, $color) {
    $name = "T$slot"
    $row = $null
    foreach ($r in $grid.Rows) {
        if ($r.Cells[0].Value -eq $name) { $row = $r; break }
    }
    if ($null -eq $row) {
        $i = $grid.Rows.Add($name,$state,$value)
        $row = $grid.Rows[$i]
    } else {
        $row.Cells[1].Value = $state
        $row.Cells[2].Value = $value
    }
    $row.Cells[1].Style.ForeColor = $color
}

function SlotState($slot, $state, $color) {
    $name = "T$slot"
    $row = $null
    foreach ($r in $grid.Rows) {
        if ($r.Cells[0].Value -eq $name) { $row = $r; break }
    }
    if ($null -eq $row) {
        $i = $grid.Rows.Add($name,$state,"")
        $row = $grid.Rows[$i]
    } else {
        $row.Cells[1].Value = $state
    }
    $row.Cells[1].Style.ForeColor = $color
}

function SlotTitle($slot, $title) {
    $name = "T$slot"
    foreach ($r in $grid.Rows) {
        if ($r.Cells[0].Value -eq $name) {
            $r.Cells[2].Value = $title
            return
        }
    }
    [void]$grid.Rows.Add($name,"Подготовка",$title)
}

function Progress {
    if ($script:total -le 0) {
        $bar.Value = 0
        $progressText.Text = "0 / 0"
        return
    }
    $p = [int](100 * $script:done / $script:total)
    $p = [Math]::Max(0,[Math]::Min(100,$p))
    $bar.Value = $p
    $progressText.Text = "$($script:done) / $($script:total)   $p%"
}

function ParseLine($line) {
    if ($line -match 'T(?<slot>\d+) START #(?<idx>\d+)/(?<total>\d+) (?<value>.+)$') {
        $script:total = [Math]::Max($script:total,[int]$matches.total)
        Slot ([int]$matches.slot) "Скачивается" $matches.value $accent
        LogLine $line
        Progress
        return
    }
    if ($line -match 'T(?<slot>\d+) DONE #(?<idx>\d+)/(?<total>\d+) (?<value>.+)$') {
        $script:total = [Math]::Max($script:total,[int]$matches.total)
        $script:done++
        Slot ([int]$matches.slot) "Готово" $matches.value $ok
        LogLine $line $ok
        Progress
        return
    }
    if ($line -match 'T(?<slot>\d+) ERROR #(?<idx>\d+)/(?<total>\d+) (?<value>.+)$') {
        $script:total = [Math]::Max($script:total,[int]$matches.total)
        $script:done++
        Slot ([int]$matches.slot) "Ошибка" $matches.value $danger
        LogLine $line $danger
        Progress
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
        if ($event.Kind -ne "Event") { continue }

        switch ($event.EventType) {
            "Title" {
                if ($event.Title) { SlotTitle ([int]$event.Slot) ([string]$event.Title) }
            }
            "Progress" {
                $pct = "{0:N1}%" -f [double]$event.Percent
                $parts = @($pct)
                if ($event.Speed) { $parts += [string]$event.Speed }
                if ($event.ETA) { $parts += ("ETA " + [string]$event.ETA) }
                SlotState ([int]$event.Slot) ($parts -join "  ·  ") $accent
            }
            "Merge" {
                SlotState ([int]$event.Slot) "Склейка дорожек…" $warn
            }
        }
    }

    $script:eventLines = $lines.Count
}

function Running($value) {
    $start.Enabled = -not $value
    $stop.Enabled = $value
    $urls.ReadOnly = $value
    $urls.AllowDrop = -not $value
    $out.ReadOnly = $value
    $threads.Enabled = -not $value
    $fragments.Enabled = -not $value
    $archive.Enabled = -not $value
    $sponsor.Enabled = -not $value
    $audio.Enabled = -not $value
    $cookies.ReadOnly = $value
    $pickOut.Enabled = -not $value
    $pickCookies.Enabled = -not $value
    $loadList.Enabled = -not $value
    if ($value) { $status.Text = "● Работает"; $status.ForeColor = $ok }
    else { $status.Text = "● Готов"; $status.ForeColor = $muted }
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
    if ($script:proc -and -not $script:proc.HasExited) { return }
    if (-not (Test-Path $engine) -or -not (Test-Path $ytDlp)) {
        [Windows.Forms.MessageBox]::Show("Рядом с GUI должны лежать ytdl-manager-v8.ps1 и yt-dlp.exe.","Video Downloader") | Out-Null
        return
    }

    $items = @($urls.Lines | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
    if ($items.Count -eq 0) {
        [Windows.Forms.MessageBox]::Show("Добавь хотя бы один URL.","Video Downloader") | Out-Null
        return
    }

    $dest = $out.Text.Trim()
    if (-not $dest) {
        $downloadsDir = Join-Path $env:USERPROFILE "Downloads"
        $dest = Join-Path $downloadsDir "downloaded-video"
        $out.Text = $dest
    }
    try {
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        $dest = (Resolve-Path $dest).Path
    } catch {
        [Windows.Forms.MessageBox]::Show("Не удалось открыть папку: " + $dest,"Video Downloader") | Out-Null
        return
    }

    $cookiePath = $cookies.Text.Trim()
    if ($cookiePath -and -not (Test-Path $cookiePath)) {
        [Windows.Forms.MessageBox]::Show("Cookies-файл не найден.","Video Downloader") | Out-Null
        return
    }

    CleanTemp
    $script:runDir = Join-Path ([IO.Path]::GetTempPath()) ("ytdl-gui-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:runDir -Force | Out-Null
    $queue = Join-Path $script:runDir "queue.txt"
    $script:runLog = Join-Path $script:runDir "run.log"
    $script:runEvents = Join-Path $script:runDir "events.jsonl"
    Set-Content $queue $items -Encoding UTF8

    $script:logLines = 0
    $script:eventLines = 0
    $script:total = $items.Count
    $script:done = 0
    $script:stopping = $false
    $grid.Rows.Clear()
    $log.Clear()
    Progress

    $pwsh = Join-Path $PSHOME "pwsh.exe"
    if (-not (Test-Path $pwsh)) { $pwsh = "pwsh.exe" }
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $pwsh
    $psi.WorkingDirectory = $root
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $resultDir = if ($archive.Checked) { Join-Path $dest (Get-Date -Format "yyyy-MM-dd") } else { $dest }

    $args = @("-NoProfile","-ExecutionPolicy","Bypass","-File",$engine,"-In",$queue,"-Out",$dest,"-Threads",[string][int]$threads.Value,"-NoProgress","-Log",$script:runLog,"-EventFile",$script:runEvents,"-ResultDir",$resultDir)
    foreach ($a in $args) { [void]$psi.ArgumentList.Add($a) }

    if ($fragments.SelectedIndex -eq 0) { [void]$psi.ArgumentList.Add("-AutoFragments") }
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
        LogLine "Запуск: $($items.Count) URL, потоков $([int]$threads.Value), папка $dest" $accent
        $timer.Start()
    } catch {
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

function FinishDownload {
    TailEvents
    TailLog
    $code = $null
    try { $code = $script:proc.ExitCode } catch {}
    try { $script:proc.Dispose() } catch {}
    $script:proc = $null
    Running $false
    if ($script:stopping) { $status.Text = "● Остановлено"; $status.ForeColor = $warn; LogLine "Остановлено пользователем." $warn }
    elseif ($code -eq 0) { $status.Text = "● Завершено"; $status.ForeColor = $ok; LogLine "Загрузка завершена." $ok }
    else { $status.Text = "● Ошибка"; $status.ForeColor = $danger; LogLine "Процесс завершился с кодом $code." $danger }
    $script:stopping = $false
    CleanTemp
}

$form = [Windows.Forms.Form]::new()
$form.Text = "Video Downloader"
$form.Size = [Drawing.Size]::new(1140,760)
$form.MinimumSize = [Drawing.Size]::new(1040,700)
$form.StartPosition = "CenterScreen"
$form.BackColor = $bg
$form.ForeColor = $fg
$form.Font = [Drawing.Font]::new("Segoe UI",9)
$form.KeyPreview = $true

$head = Label "Video Downloader" 22 15 400 18 $fg
$head.Font = [Drawing.Font]::new("Segoe UI",18,[Drawing.FontStyle]::Bold)
$head.Height = 34
$form.Controls.Add($head)
$form.Controls.Add((Label "GUI для твоего yt-dlp manager v8" 24 50 420 9 $muted))
$status = Label "● Готов" 970 20 130 9 $muted
$status.Anchor = "Top,Right"
$status.BackColor = $field
$status.TextAlign = "MiddleCenter"
$status.Height = 30
Make-Rounded $status 16
$form.Controls.Add($status)

$left = [Windows.Forms.Panel]::new()
$left.Location = [Drawing.Point]::new(22,84)
$left.Size = [Drawing.Size]::new(690,610)
$left.Anchor = "Top,Bottom,Left,Right"
$left.BackColor = $panel
Make-Rounded $left 22
$form.Controls.Add($left)

$right = [Windows.Forms.Panel]::new()
$right.Location = [Drawing.Point]::new(728,84)
$right.Size = [Drawing.Size]::new(374,610)
$right.Anchor = "Top,Bottom,Right"
$right.BackColor = $panel
Make-Rounded $right 22
$form.Controls.Add($right)

$left.Controls.Add((Label "Очередь URL" 16 12 200 10 $fg))
$loadList = Button "Открыть .txt" 550 10 120
Make-Rounded $loadList 12
$left.Controls.Add($loadList)

$urls = [Windows.Forms.TextBox]::new()
$urls.Location = [Drawing.Point]::new(16,46)
$urls.Size = [Drawing.Size]::new(654,145)
$urls.Anchor = "Top,Left,Right"
$urls.Multiline = $true
$urls.ScrollBars = "Vertical"
$urls.AllowDrop = $true
$urls.BorderStyle = "None"
StyleText $urls
$left.Controls.Add($urls)

$queueHint = Label "Перетащи сюда .txt со ссылками или вставь URL построчно" 20 171 500 8.5 $muted
$left.Controls.Add($queueHint)

$left.Controls.Add((Label "Текущие загрузки" 16 205 220 10 $fg))
$grid = [Windows.Forms.DataGridView]::new()
$grid.Location = [Drawing.Point]::new(16,238)
$grid.Size = [Drawing.Size]::new(654,165)
$grid.Anchor = "Top,Left,Right"
$grid.BackgroundColor = $field
$grid.BorderStyle = "None"
$grid.RowHeadersVisible = $false
$grid.AllowUserToAddRows = $false
$grid.ReadOnly = $true
$grid.EnableHeadersVisualStyles = $false
$grid.ColumnHeadersDefaultCellStyle.BackColor = $bg
$grid.ColumnHeadersDefaultCellStyle.ForeColor = $muted
$grid.DefaultCellStyle.BackColor = $field
$grid.DefaultCellStyle.ForeColor = $fg
$grid.DefaultCellStyle.SelectionBackColor = [Drawing.Color]::FromArgb(52,58,70)
$grid.DefaultCellStyle.SelectionForeColor = $fg
$grid.CellBorderStyle = "SingleHorizontal"
$grid.ColumnHeadersBorderStyle = "None"
$grid.RowTemplate.Height = 30
$grid.DefaultCellStyle.Padding = [Windows.Forms.Padding]::new(4,0,4,0)
[void]$grid.Columns.Add("slot","Слот")
[void]$grid.Columns.Add("state","Статус")
[void]$grid.Columns.Add("value","Видео / URL")
$grid.Columns[0].Width = 55
$grid.Columns[1].Width = 225
$grid.Columns[2].AutoSizeMode = "Fill"
$left.Controls.Add($grid)

$left.Controls.Add((Label "Лог" 16 418 100 10 $fg))
$log = [Windows.Forms.RichTextBox]::new()
$log.Location = [Drawing.Point]::new(16,450)
$log.Size = [Drawing.Size]::new(654,140)
$log.Anchor = "Top,Bottom,Left,Right"
$log.ReadOnly = $true
$log.BackColor = [Drawing.Color]::FromArgb(18,20,25)
$log.ForeColor = $muted
$log.BorderStyle = "None"
$log.Font = [Drawing.Font]::new("Cascadia Mono",8.5)
$left.Controls.Add($log)

$right.Controls.Add((Label "Настройки" 16 12 160 10 $fg))
$right.Controls.Add((Label "Папка загрузки" 16 50 180 9 $muted))
$out = [Windows.Forms.TextBox]::new()
$out.Location = [Drawing.Point]::new(16,75)
$out.Size = [Drawing.Size]::new(290,27)
$downloadsDir = Join-Path $env:USERPROFILE "Downloads"
$out.Text = Join-Path $downloadsDir "downloaded-video"
StyleText $out
$right.Controls.Add($out)
$pickOut = Button "…" 314 74 42
Make-Rounded $pickOut 10
$right.Controls.Add($pickOut)

$right.Controls.Add((Label "Параллельные URL" 16 122 180 9 $muted))
$threads = [Windows.Forms.NumericUpDown]::new()
$threads.Location = [Drawing.Point]::new(224,120)
$threads.Size = [Drawing.Size]::new(132,27)
$threads.Minimum = 1
$threads.Maximum = 32
$threads.Value = 4
$threads.BackColor = $field
$threads.ForeColor = $fg
$right.Controls.Add($threads)

$right.Controls.Add((Label "Фрагменты" 16 160 180 9 $muted))
$fragments = [Windows.Forms.ComboBox]::new()
$fragments.Location = [Drawing.Point]::new(224,158)
$fragments.Size = [Drawing.Size]::new(132,27)
$fragments.DropDownStyle = "DropDownList"
$fragments.BackColor = $field
$fragments.ForeColor = $fg
[void]$fragments.Items.Add("Авто")
1..4 | ForEach-Object { [void]$fragments.Items.Add([string]$_) }
$fragments.SelectedIndex = 0
$right.Controls.Add($fragments)

$archive = [Windows.Forms.CheckBox]::new()
$archive.Text = "Архив по дате"
$archive.Location = [Drawing.Point]::new(16,205)
$archive.Size = [Drawing.Size]::new(170,24)
$archive.ForeColor = $fg
$right.Controls.Add($archive)

$sponsor = [Windows.Forms.CheckBox]::new()
$sponsor.Text = "Удалять SponsorBlock"
$sponsor.Location = [Drawing.Point]::new(16,235)
$sponsor.Size = [Drawing.Size]::new(190,24)
$sponsor.ForeColor = $fg
$right.Controls.Add($sponsor)

$audio = [Windows.Forms.CheckBox]::new()
$audio.Text = "Только аудио (MP3)"
$audio.Location = [Drawing.Point]::new(16,265)
$audio.Size = [Drawing.Size]::new(180,24)
$audio.ForeColor = $fg
$right.Controls.Add($audio)

$right.Controls.Add((Label "Cookies (необязательно)" 16 307 210 9 $muted))
$cookies = [Windows.Forms.TextBox]::new()
$cookies.Location = [Drawing.Point]::new(16,332)
$cookies.Size = [Drawing.Size]::new(290,27)
StyleText $cookies
$right.Controls.Add($cookies)
$pickCookies = Button "…" 314 331 42
Make-Rounded $pickCookies 10
$right.Controls.Add($pickCookies)

$right.Controls.Add((Label "Общий прогресс" 16 385 180 9 $muted))
$progressText = Label "0 / 0" 245 385 111 9 $muted
$progressText.TextAlign = "MiddleRight"
$right.Controls.Add($progressText)
$bar = [Windows.Forms.ProgressBar]::new()
$bar.Location = [Drawing.Point]::new(16,414)
$bar.Size = [Drawing.Size]::new(340,18)
$right.Controls.Add($bar)

$start = Button "▶  Начать загрузку" 16 458 340 $accent
$start.Size = [Drawing.Size]::new(340,42)
Make-Rounded $start 14
$right.Controls.Add($start)
$stop = Button "■  Остановить" 16 510 164 $danger
$stop.Enabled = $false
Make-Rounded $stop 14
$right.Controls.Add($stop)
$openFolder = Button "Открыть папку" 192 510 164
Make-Rounded $openFolder 14
$right.Controls.Add($openFolder)
$right.Controls.Add((Label "Ctrl+Enter — начать загрузку" 16 566 300 8.5 $muted))

$folderDialog = [Windows.Forms.FolderBrowserDialog]::new()
$fileDialog = [Windows.Forms.OpenFileDialog]::new()

$pickOut.Add_Click({
    $folderDialog.SelectedPath = $out.Text
    if ($folderDialog.ShowDialog() -eq "OK") { $out.Text = $folderDialog.SelectedPath }
})
$pickCookies.Add_Click({
    $fileDialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
    if ($fileDialog.ShowDialog() -eq "OK") { $cookies.Text = $fileDialog.FileName }
})
$loadList.Add_Click({
    $fileDialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
    if ($fileDialog.ShowDialog() -eq "OK") { $urls.Lines = @(Get-Content $fileDialog.FileName -Encoding UTF8) }
})

$urls.Add_DragEnter({
    param($sender,$e)
    if ($e.Data.GetDataPresent([Windows.Forms.DataFormats]::FileDrop)) {
        $e.Effect = [Windows.Forms.DragDropEffects]::Copy
        $sender.BackColor = [Drawing.Color]::FromArgb(54,60,78)
        $queueHint.Text = "Отпусти файл — ссылки добавятся в очередь"
        $queueHint.ForeColor = $accent
    } else {
        $e.Effect = [Windows.Forms.DragDropEffects]::None
    }
})

$urls.Add_DragLeave({
    param($sender,$e)
    $sender.BackColor = $field
    $queueHint.Text = "Перетащи сюда .txt со ссылками или вставь URL построчно"
    $queueHint.ForeColor = $muted
})

$urls.Add_DragDrop({
    param($sender,$e)

    $sender.BackColor = $field
    $queueHint.Text = "Перетащи сюда .txt со ссылками или вставь URL построчно"
    $queueHint.ForeColor = $muted

    $files = @($e.Data.GetData([Windows.Forms.DataFormats]::FileDrop))
    if ($files.Count -eq 0) { return }

    $droppedLines = [Collections.Generic.List[string]]::new()

    foreach ($file in $files) {
        if (-not (Test-Path $file -PathType Leaf)) { continue }

        try {
            foreach ($line in @(Get-Content -Path $file -Encoding UTF8 -ErrorAction Stop)) {
                $value = $line.Trim()
                if ($value) { [void]$droppedLines.Add($value) }
            }
        } catch {
            [Windows.Forms.MessageBox]::Show(
                "Не удалось прочитать файл: " + $file + [Environment]::NewLine + $_.Exception.Message,
                "Video Downloader"
            ) | Out-Null
        }
    }

    if ($droppedLines.Count -eq 0) { return }

    $existing = @($urls.Lines | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $combined = @($existing + $droppedLines.ToArray() | Select-Object -Unique)
    $urls.Lines = $combined
})

$openFolder.Add_Click({
    if (Test-Path $out.Text) { Start-Process explorer.exe -ArgumentList @($out.Text) }
})
$start.Add_Click({ StartDownload })
$stop.Add_Click({ StopDownload })

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
    TailEvents
    TailLog
    if ($script:proc) {
        try {
            if ($script:proc.HasExited) {
                $timer.Stop()
                FinishDownload
            }
        } catch {}
    }
})

$form.Add_FormClosing({
    param($sender,$e)
    if ($script:proc -and -not $script:proc.HasExited) {
        $answer = [Windows.Forms.MessageBox]::Show("Идёт загрузка. Остановить её и закрыть программу?","Video Downloader","YesNo","Warning")
        if ($answer -ne "Yes") { $e.Cancel = $true; return }
        try { $script:proc.Kill($true) } catch {}
    }
    CleanTemp
})

[void]$form.ShowDialog()
