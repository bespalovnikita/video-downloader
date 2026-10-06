#requires -Version 7.0
if (-not $IsWindows) { throw "Windows only." }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeUi {
    [DllImport("gdi32.dll", SetLastError=true)]
    public static extern IntPtr CreateRoundRectRgn(int l,int t,int r,int b,int w,int h);
    [DllImport("gdi32.dll", SetLastError=true)]
    public static extern bool DeleteObject(IntPtr h);
}
"@

[Windows.Forms.Application]::EnableVisualStyles()

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$engine = Join-Path $root "ytdl-manager-v8.ps1"
$ytDlp = Join-Path $root "yt-dlp.exe"

function Remove-StaleGuiTempDirs {
    $cutoff = (Get-Date).AddHours(-24)
    foreach ($dir in @(Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter "ytdl-gui-*" -ErrorAction SilentlyContinue)) {
        if ($dir.LastWriteTime -gt $cutoff) { continue }
        $owner = Join-Path $dir.FullName "owner.pid"
        if (Test-Path $owner) {
            try {
                $pidValue = [int](Get-Content $owner -ErrorAction Stop | Select-Object -First 1)
                if (Get-Process -Id $pidValue -ErrorAction SilentlyContinue) { continue }
            } catch {}
        }
        try { Remove-Item $dir.FullName -Recurse -Force -ErrorAction Stop } catch {}
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
$script:success = 0
$script:failed = 0
$script:stopping = $false
$script:failedUrls = [Collections.Generic.List[string]]::new()
$script:speeds = @{}
$script:lastFiles = @{}
$script:previewJob = $null
$script:previewUrl = ""
$script:updateJob = $null
$script:lastClipboard = ""
$script:allowExit = $false

$bg = [Drawing.Color]::FromArgb(17,19,24)
$panel = [Drawing.Color]::FromArgb(27,30,37)
$field = [Drawing.Color]::FromArgb(37,41,50)
$field2 = [Drawing.Color]::FromArgb(45,50,62)
$fg = [Drawing.Color]::FromArgb(238,241,248)
$muted = [Drawing.Color]::FromArgb(148,156,174)
$accent = [Drawing.Color]::FromArgb(99,102,241)
$danger = [Drawing.Color]::FromArgb(239,68,68)
$ok = [Drawing.Color]::FromArgb(34,197,94)
$warn = [Drawing.Color]::FromArgb(245,158,11)

function Label($text,$x,$y,$w=160,$size=9,$color=$fg) {
    $c = [Windows.Forms.Label]::new()
    $c.Text = $text
    $c.Location = [Drawing.Point]::new($x,$y)
    $c.Size = [Drawing.Size]::new($w,24)
    $c.Font = [Drawing.Font]::new("Segoe UI",$size)
    $c.ForeColor = $color
    $c.BackColor = [Drawing.Color]::Transparent
    return $c
}
function Button($text,$x,$y,$w=110,$h=34,$color=$field2) {
    $c = [Windows.Forms.Button]::new()
    $c.Text = $text
    $c.Location = [Drawing.Point]::new($x,$y)
    $c.Size = [Drawing.Size]::new($w,$h)
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
function Set-RoundedRegion($control,$radius=14) {
    if (-not $control -or $control.Width -le 0 -or $control.Height -le 0) { return }
    try {
        $h = [NativeUi]::CreateRoundRectRgn(0,0,$control.Width+1,$control.Height+1,$radius,$radius)
        if ($h -eq [IntPtr]::Zero) { return }
        $region = [Drawing.Region]::FromHrgn($h)
        if ($control.Region) { $control.Region.Dispose() }
        $control.Region = $region
        [void][NativeUi]::DeleteObject($h)
    } catch {}
}
function Make-Rounded($control,$radius=14) {
    $control.Tag = $radius
    Set-RoundedRegion $control $radius
    $control.Add_SizeChanged({
        $r = 14
        if ($this.Tag) { $r = [int]$this.Tag }
        Set-RoundedRegion $this $r
    })
}
function Is-ValidUrl([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return $false }
    $uri = $null
    if (-not [Uri]::TryCreate($value.Trim(),[UriKind]::Absolute,[ref]$uri)) { return $false }
    return $uri.Scheme -eq "http" -or $uri.Scheme -eq "https"
}
function Extract-Urls([string]$text) {
    $found = [Collections.Generic.List[string]]::new()
    if ([string]::IsNullOrWhiteSpace($text)) { return $found.ToArray() }
    foreach ($token in ($text -split "[\r\n\t ]+")) {
        $v = $token.Trim()
        if (Is-ValidUrl $v -and -not $found.Contains($v)) { [void]$found.Add($v) }
    }
    return $found.ToArray()
}
function Update-QueueSummary {
    $queueCount.Text = "$($queueGrid.Rows.Count) URL"
    $start.Enabled = ($queueGrid.Rows.Count -gt 0 -and -not ($script:proc -and -not $script:proc.HasExited))
}
function Add-Urls([string[]]$values) {
    $rejected = 0
    $existing = @{}
    foreach ($r in $queueGrid.Rows) {
        if ($r.Cells["url"].Value) { $existing[[string]$r.Cells["url"].Value] = $true }
    }
    foreach ($raw in $values) {
        $u = ([string]$raw).Trim()
        if (-not (Is-ValidUrl $u)) { $rejected++; continue }
        if ($existing.ContainsKey($u)) { continue }
        $i = $queueGrid.Rows.Add("✓",$u,"В очереди")
        $queueGrid.Rows[$i].Cells["valid"].Style.ForeColor = $ok
        $queueGrid.Rows[$i].Cells["state"].Style.ForeColor = $muted
        $existing[$u] = $true
    }
    Update-QueueSummary
    if ($rejected -gt 0) { LogLine "Пропущено некорректных URL: $rejected" $warn }
}
function Add-FileToQueue([string]$path) {
    if (-not (Test-Path $path -PathType Leaf)) { return }
    try {
        if ([IO.Path]::GetExtension($path).ToLowerInvariant() -eq ".url") {
            $urlLine = @(Get-Content $path -Encoding UTF8 -ErrorAction Stop) | Where-Object { $_ -match '^URL=' } | Select-Object -First 1
            if ($urlLine) { Add-Urls @($urlLine.Substring(4)) }
            return
        }
        Add-Urls @(Get-Content $path -Encoding UTF8 -ErrorAction Stop)
    } catch {
        $msg = "Не удалось прочитать файл:" + [Environment]::NewLine + $path + [Environment]::NewLine + $_.Exception.Message
        [Windows.Forms.MessageBox]::Show($msg,"Video Downloader") | Out-Null
    }
}
function Friendly-Error([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return "Неизвестная ошибка" }
    $e = $text.ToLowerInvariant()
    if ($e -match "private|sign in|login|cookies") { return "Нужна авторизация / cookies" }
    if ($e -match "not available|unavailable|removed|deleted") { return "Видео недоступно" }
    if ($e -match "geo|country|region") { return "Региональное ограничение" }
    if ($e -match "429|too many requests") { return "Слишком много запросов" }
    if ($e -match "timeout|timed out|network|connection|dns|unable to download") { return "Ошибка сети" }
    if ($e -match "requested format|format") { return "Формат недоступен" }
    if ($e -match "ffmpeg|ffprobe") { return "Ошибка FFmpeg" }
    return "Ошибка загрузки"
}
function Convert-SpeedToBytes([string]$speed) {
    if ([string]::IsNullOrWhiteSpace($speed) -or $speed -notmatch '([0-9.,]+)\s*([KMGT]?i?B)/s') { return 0.0 }
    $value = 0.0
    $num = $matches[1].Replace(",",".")
    if (-not [double]::TryParse($num,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$value)) { return 0.0 }
    switch -Regex ($matches[2].ToUpperInvariant()) {
        '^K' { return $value * 1KB }
        '^M' { return $value * 1MB }
        '^G' { return $value * 1GB }
        '^T' { return $value * 1TB }
        default { return $value }
    }
}
function Format-Speed([double]$bytes) {
    if ($bytes -ge 1GB) { return ("{0:N1} GB/s" -f ($bytes/1GB)) }
    if ($bytes -ge 1MB) { return ("{0:N1} MB/s" -f ($bytes/1MB)) }
    if ($bytes -ge 1KB) { return ("{0:N0} KB/s" -f ($bytes/1KB)) }
    return "0 KB/s"
}
function Update-Stats {
    $sum = 0.0
    foreach ($v in $script:speeds.Values) { $sum += [double]$v }
    $active = 0
    foreach ($v in $script:speeds.Values) { if ([double]$v -gt 0) { $active++ } }
    $stats.Text = "$active активных   ·   $(Format-Speed $sum)   ·   $($script:done)/$($script:total) готово"
}
function LogLine($line,$color=$muted) {
    if ([string]::IsNullOrWhiteSpace($line)) { return }
    $log.SelectionStart = $log.TextLength
    $log.SelectionColor = $color
    $log.AppendText($line + [Environment]::NewLine)
    $log.ScrollToCaret()
}
function Find-DownloadRow([int]$slot) {
    foreach ($r in $downloadGrid.Rows) {
        if ([string]$r.Cells["slot"].Value -eq "T$slot") { return $r }
    }
    return $null
}
function Set-DownloadRow([int]$slot,[string]$state,[string]$value,$color,[string]$filePath="") {
    $row = Find-DownloadRow $slot
    if ($null -eq $row) {
        $i = $downloadGrid.Rows.Add("T$slot",$state,$value,$filePath)
        $row = $downloadGrid.Rows[$i]
    } else {
        $row.Cells["state"].Value = $state
        if ($value) { $row.Cells["video"].Value = $value }
        if ($filePath) { $row.Cells["file"].Value = $filePath }
    }
    $row.Cells["state"].Style.ForeColor = $color
}
function Update-Progress {
    if ($script:total -le 0) {
        $bar.Value = 0
        $progressText.Text = "0 / 0"
        Update-Stats
        return
    }
    $p = [int](100*$script:done/$script:total)
    $p = [Math]::Max(0,[Math]::Min(100,$p))
    $bar.Value = $p
    $progressText.Text = "$($script:done) / $($script:total)   $p%"
    Update-Stats
}
function TailLog {
    if (-not $script:runLog -or -not (Test-Path $script:runLog)) { return }
    try { $lines = @(Get-Content $script:runLog -Encoding UTF8 -ErrorAction Stop) } catch { return }
    for ($i=$script:logLines;$i -lt $lines.Count;$i++) {
        $line = $lines[$i]
        if ($line -match 'ERROR|EXCEPTION|JOB-ERROR') { LogLine $line $danger }
        elseif ($line -match 'FALLBACK|WARNING') { LogLine $line $warn }
        elseif ($line -match 'DONE|Finished') { LogLine $line $ok }
        else { LogLine $line }
    }
    $script:logLines = $lines.Count
}
function TailEvents {
    if (-not $script:runEvents -or -not (Test-Path $script:runEvents)) { return }
    try { $lines = @(Get-Content $script:runEvents -Encoding UTF8 -ErrorAction Stop) } catch { return }
    for ($i=$script:eventLines;$i -lt $lines.Count;$i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
        try { $event = $lines[$i] | ConvertFrom-Json -ErrorAction Stop } catch { continue }

        if ($event.Kind -eq "Event") {
            $slot = [int]$event.Slot
            switch ($event.EventType) {
                "Title" { if ($event.Title) { Set-DownloadRow $slot "Подготовка" ([string]$event.Title) $muted } }
                "Progress" {
                    $parts = @("{0:N1}%" -f [double]$event.Percent)
                    if ($event.Speed) { $parts += [string]$event.Speed }
                    if ($event.ETA) { $parts += ("ETA " + [string]$event.ETA) }
                    Set-DownloadRow $slot ($parts -join "  ·  ") "" $accent
                    $script:speeds[$slot] = Convert-SpeedToBytes ([string]$event.Speed)
                    Update-Stats
                }
                "Merge" { Set-DownloadRow $slot "Склейка дорожек…" "" $warn }
                "File" {
                    $path = [string]$event.Text
                    if ($path) {
                        $script:lastFiles[$slot] = $path
                        Set-DownloadRow $slot "Финализация…" "" $warn $path
                    }
                }
                "Error" { Set-DownloadRow $slot (Friendly-Error ([string]$event.Text)) "" $danger }
            }
        } elseif ($event.Kind -eq "Result") {
            $slot = [int]$event.Slot
            $script:speeds[$slot] = 0
            $script:done++
            if ([bool]$event.Success) {
                $script:success++
                $fp = [string]$event.FilePath
                if (-not $fp) { $fp = [string]$event.Path }
                if (-not $fp -and $script:lastFiles.ContainsKey($slot)) { $fp = [string]$script:lastFiles[$slot] }
                Set-DownloadRow $slot "Готово" "" $ok $fp
            } else {
                $script:failed++
                $u = [string]$event.Url
                if ($u -and -not $script:failedUrls.Contains($u)) { [void]$script:failedUrls.Add($u) }
                Set-DownloadRow $slot (Friendly-Error ([string]$event.Error)) "" $danger
            }
            Update-Progress
        }
    }
    $script:eventLines = $lines.Count
}
function CleanTemp {
    if ($script:runDir -and (Test-Path $script:runDir)) { Remove-Item $script:runDir -Recurse -Force -ErrorAction SilentlyContinue }
    $script:runDir = $null
    $script:runLog = $null
    $script:runEvents = $null
}
function Show-Notification([string]$title,[string]$message,[bool]$isError=$false) {
    $shown = $false
    try {
        [void][Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime]
        $st = [Security.SecurityElement]::Escape($title)
        $sm = [Security.SecurityElement]::Escape($message)
        $xml = "<toast><visual><binding template='ToastGeneric'><text>$st</text><text>$sm</text></binding></visual></toast>"
        $doc = [Windows.Data.Xml.Dom.XmlDocument]::new()
        $doc.LoadXml($xml)
        $toast = [Windows.UI.Notifications.ToastNotification]::new($doc)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier("Video Downloader").Show($toast)
        $shown = $true
    } catch {}
    if (-not $shown) {
        $tray.BalloonTipTitle = $title
        $tray.BalloonTipText = $message
        if ($isError) { $tray.BalloonTipIcon = "Error" } else { $tray.BalloonTipIcon = "Info" }
        $tray.ShowBalloonTip(5000)
    }
}
function Set-Running([bool]$value) {
    $start.Enabled = -not $value -and $queueGrid.Rows.Count -gt 0
    $stop.Enabled = $value
    $retry.Enabled = -not $value -and $script:failedUrls.Count -gt 0
    foreach ($c in @($queueGrid,$urlInput,$addUrl,$removeUrl,$clearQueue,$loadList,$quality,$rateLimit,$threads,$fragments,$archive,$sponsor,$audio,$pickOut,$pickCookies)) { $c.Enabled = -not $value }
    $cookies.ReadOnly = $value
    $out.ReadOnly = $value
    if ($value) { $status.Text = "● Работает"; $status.ForeColor = $ok }
    else { $status.Text = "● Готов"; $status.ForeColor = $muted }
}
function Get-QueueUrls {
    $items = [Collections.Generic.List[string]]::new()
    foreach ($r in $queueGrid.Rows) {
        $u = [string]$r.Cells["url"].Value
        if (Is-ValidUrl $u -and -not $items.Contains($u)) { [void]$items.Add($u) }
    }
    return $items.ToArray()
}
function StartDownload([string[]]$overrideUrls=$null) {
    if ($script:proc -and -not $script:proc.HasExited) { return }
    if (-not (Test-Path $engine) -or -not (Test-Path $ytDlp)) {
        [Windows.Forms.MessageBox]::Show("Рядом с GUI должны лежать ytdl-manager-v8.ps1 и yt-dlp.exe.","Video Downloader") | Out-Null
        return
    }

    $items = $overrideUrls
    if ($null -eq $items) { $items = @(Get-QueueUrls) }
    if ($items.Count -eq 0) {
        [Windows.Forms.MessageBox]::Show("В очереди нет корректных URL.","Video Downloader") | Out-Null
        return
    }

    $dest = $out.Text.Trim()
    if (-not $dest) { $dest = Join-Path (Join-Path $env:USERPROFILE "Downloads") "downloaded-video"; $out.Text = $dest }
    try { New-Item -ItemType Directory -Path $dest -Force | Out-Null; $dest = (Resolve-Path $dest).Path }
    catch { [Windows.Forms.MessageBox]::Show("Не удалось открыть папку: $dest","Video Downloader") | Out-Null; return }

    $cookiePath = $cookies.Text.Trim()
    if ($cookiePath -and -not (Test-Path $cookiePath)) {
        [Windows.Forms.MessageBox]::Show("Cookies-файл не найден.","Video Downloader") | Out-Null
        return
    }

    CleanTemp
    $script:runDir = Join-Path ([IO.Path]::GetTempPath()) ("ytdl-gui-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:runDir -Force | Out-Null
    Set-Content (Join-Path $script:runDir "owner.pid") $PID -Encoding ASCII
    $queue = Join-Path $script:runDir "queue.txt"
    $script:runLog = Join-Path $script:runDir "run.log"
    $script:runEvents = Join-Path $script:runDir "events.jsonl"
    Set-Content $queue $items -Encoding UTF8

    $script:logLines = 0; $script:eventLines = 0; $script:total = $items.Count
    $script:done = 0; $script:success = 0; $script:failed = 0; $script:stopping = $false
    $script:failedUrls.Clear(); $script:speeds.Clear(); $script:lastFiles.Clear()
    $downloadGrid.Rows.Clear(); $log.Clear(); Update-Progress

    $pwsh = Join-Path $PSHOME "pwsh.exe"
    if (-not (Test-Path $pwsh)) { $pwsh = "pwsh.exe" }
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $pwsh
    $psi.WorkingDirectory = $root
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $resultDir = $dest
    if ($archive.Checked) { $resultDir = Join-Path $dest (Get-Date -Format "yyyy-MM-dd") }

    $args = @("-NoProfile","-ExecutionPolicy","Bypass","-File",$engine,"-In",$queue,"-Out",$dest,"-Threads",[string][int]$threads.Value,"-NoProgress","-Log",$script:runLog,"-EventFile",$script:runEvents,"-ResultDir",$resultDir,"-Quality",[string]$quality.SelectedItem)
    $limit = $rateLimit.Text.Trim()
    if ($limit -and $limit -ne "Без лимита") { $args += @("-RateLimit",$limit) }
    foreach ($a in $args) { [void]$psi.ArgumentList.Add($a) }

    if ($fragments.SelectedIndex -eq 0) { [void]$psi.ArgumentList.Add("-AutoFragments") }
    else { [void]$psi.ArgumentList.Add("-Fragments"); [void]$psi.ArgumentList.Add([string]$fragments.SelectedItem) }
    if ($archive.Checked) { [void]$psi.ArgumentList.Add("-Archive") }
    if ($sponsor.Checked) { [void]$psi.ArgumentList.Add("-SponsorBlock") }
    if ($audio.Checked) { [void]$psi.ArgumentList.Add("-AudioOnly") }
    if ($cookiePath) { [void]$psi.ArgumentList.Add("-Cookies"); [void]$psi.ArgumentList.Add((Resolve-Path $cookiePath).Path) }

    try {
        $script:proc = [Diagnostics.Process]::Start($psi)
        Set-Running $true
        LogLine "Запуск: $($items.Count) URL · $([int]$threads.Value) потоков · качество $($quality.Text) · папка $dest" $accent
        $timer.Start()
    } catch {
        CleanTemp
        $script:proc = $null
        Set-Running $false
        [Windows.Forms.MessageBox]::Show("Ошибка запуска: $($_.Exception.Message)","Video Downloader") | Out-Null
    }
}
function StopDownload {
    if (-not $script:proc -or $script:proc.HasExited) { return }
    $script:stopping = $true
    $status.Text = "● Остановка"
    $status.ForeColor = $warn
    try { $script:proc.Kill($true) } catch { LogLine $_.Exception.Message $danger }
}
function FinishDownload {
    TailEvents; TailLog
    $code = $null
    try { $code = $script:proc.ExitCode } catch {}
    try { $script:proc.Dispose() } catch {}
    $script:proc = $null
    Set-Running $false

    if ($script:stopping) {
        $status.Text = "● Остановлено"; $status.ForeColor = $warn; LogLine "Остановлено пользователем." $warn
    } elseif ($code -eq 0 -and $script:failed -eq 0) {
        $status.Text = "● Завершено"; $status.ForeColor = $ok
        LogLine "Готово: $($script:success)/$($script:total)." $ok
        try { [Media.SystemSounds]::Asterisk.Play() } catch {}
        Show-Notification "Video Downloader" "Готово: $($script:success)/$($script:total) видео."
    } else {
        $status.Text = "● С ошибками"; $status.ForeColor = $danger
        LogLine "Завершено: успешно $($script:success), ошибок $($script:failed)." $danger
        try { [Media.SystemSounds]::Hand.Play() } catch {}
        Show-Notification "Video Downloader" "Успешно: $($script:success). Ошибок: $($script:failed)." $true
    }
    $retry.Enabled = $script:failedUrls.Count -gt 0
    $script:stopping = $false
    CleanTemp
}
function Start-Preview([string]$url) {
    if (-not (Is-ValidUrl $url) -or -not (Test-Path $ytDlp)) { return }
    if ($script:previewJob) {
        try { Stop-Job $script:previewJob -ErrorAction SilentlyContinue; Remove-Job $script:previewJob -Force -ErrorAction SilentlyContinue } catch {}
    }
    $script:previewUrl = $url
    $previewTitle.Text = "Загрузка информации…"
    $previewMeta.Text = $url
    $thumb.Image = $null
    $exe = $ytDlp
    $cookie = $cookies.Text.Trim()
    $script:previewJob = Start-ThreadJob -ArgumentList $exe,$url,$cookie -ScriptBlock {
        param($exe,$url,$cookie)
        $args = @("--dump-single-json","--skip-download","--no-warnings","--encoding","utf-8","--impersonate","chrome")
        if ($cookie -and (Test-Path $cookie)) { $args += @("--cookies",$cookie) }
        $args += $url
        $raw = & $exe @args 2>$null
        if ($LASTEXITCODE -ne 0) { throw "yt-dlp metadata failed" }
        return ($raw -join [Environment]::NewLine)
    }
}
function Complete-Preview {
    if (-not $script:previewJob -or $script:previewJob.State -eq "Running") { return }
    try {
        if ($script:previewJob.State -ne "Completed") { throw "Не удалось получить метаданные" }
        $raw = Receive-Job $script:previewJob -ErrorAction Stop
        $json = ($raw -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
        $title = [string]$json.title
        if (-not $title) { $title = "Без названия" }
        $previewTitle.Text = $title

        $duration = ""
        if ($json.duration) {
            $ts = [TimeSpan]::FromSeconds([double]$json.duration)
            if ($ts.TotalHours -ge 1) { $duration = $ts.ToString("hh\:mm\:ss") } else { $duration = $ts.ToString("mm\:ss") }
        }
        $maxHeight = 0
        foreach ($f in @($json.formats)) { if ($f.height -and [int]$f.height -gt $maxHeight) { $maxHeight = [int]$f.height } }
        $parts = [Collections.Generic.List[string]]::new()
        if ($json.uploader) { [void]$parts.Add([string]$json.uploader) }
        if ($duration) { [void]$parts.Add($duration) }
        if ($maxHeight -gt 0) { [void]$parts.Add(([string]$maxHeight + "p")) }
        if ($json.extractor_key) { [void]$parts.Add([string]$json.extractor_key) }
        $previewMeta.Text = $parts -join "  ·  "

        if ($json.thumbnail) {
            try {
                $wc = [Net.WebClient]::new()
                $bytes = $wc.DownloadData([string]$json.thumbnail)
                $wc.Dispose()
                $ms = [IO.MemoryStream]::new($bytes)
                $img = [Drawing.Image]::FromStream($ms)
                $thumb.Image = [Drawing.Bitmap]::new($img)
                $img.Dispose(); $ms.Dispose()
            } catch {}
        }
    } catch {
        $previewTitle.Text = "Предпросмотр недоступен"
        $previewMeta.Text = $_.Exception.Message
    } finally {
        try { Remove-Job $script:previewJob -Force -ErrorAction SilentlyContinue } catch {}
        $script:previewJob = $null
    }
}
function Start-YtDlpUpdate {
    if ($script:updateJob -or -not (Test-Path $ytDlp)) { return }
    $updateYt.Enabled = $false
    $updateYt.Text = "Обновление…"
    $exe = $ytDlp
    $script:updateJob = Start-ThreadJob -ArgumentList $exe -ScriptBlock {
        param($exe)
        $o = & $exe -U 2>&1
        [pscustomobject]@{ ExitCode=$LASTEXITCODE; Text=($o -join [Environment]::NewLine) }
    }
}
function Complete-YtDlpUpdate {
    if (-not $script:updateJob -or $script:updateJob.State -eq "Running") { return }
    try {
        $r = Receive-Job $script:updateJob -ErrorAction Stop | Select-Object -Last 1
        if ($r.ExitCode -eq 0) { LogLine "yt-dlp: $($r.Text)" $ok; Show-Notification "Video Downloader" "yt-dlp обновлён." }
        else { LogLine "Не удалось обновить yt-dlp: $($r.Text)" $danger }
    } catch { LogLine "Не удалось обновить yt-dlp: $($_.Exception.Message)" $danger }
    finally {
        try { Remove-Job $script:updateJob -Force -ErrorAction SilentlyContinue } catch {}
        $script:updateJob = $null
        $updateYt.Enabled = $true
        $updateYt.Text = "Обновить yt-dlp"
    }
}
function Open-SelectedFile {
    if ($downloadGrid.SelectedRows.Count -eq 0) { return }
    $path = [string]$downloadGrid.SelectedRows[0].Cells["file"].Value
    if ($path -and (Test-Path $path -PathType Leaf)) { Start-Process $path }
}
function Open-SelectedFolder {
    if ($downloadGrid.SelectedRows.Count -eq 0) {
        if (Test-Path $out.Text) { Start-Process explorer.exe -ArgumentList @($out.Text) }
        return
    }
    $path = [string]$downloadGrid.SelectedRows[0].Cells["file"].Value
    if ($path -and (Test-Path $path -PathType Leaf)) {
        Start-Process explorer.exe -ArgumentList @("/select," + [char]34 + $path + [char]34)
    } elseif (Test-Path $out.Text) { Start-Process explorer.exe -ArgumentList @($out.Text) }
}
function Copy-SelectedPath {
    if ($downloadGrid.SelectedRows.Count -eq 0) { return }
    $path = [string]$downloadGrid.SelectedRows[0].Cells["file"].Value
    if ($path) { [Windows.Forms.Clipboard]::SetText($path) }
}

$form = [Windows.Forms.Form]::new()
$form.Text = "Video Downloader"
$form.Size = [Drawing.Size]::new(1390,850)
$form.MinimumSize = [Drawing.Size]::new(1280,780)
$form.StartPosition = "CenterScreen"
$form.BackColor = $bg
$form.ForeColor = $fg
$form.Font = [Drawing.Font]::new("Segoe UI",9)
$form.KeyPreview = $true
$form.Icon = [Drawing.SystemIcons]::Application

$head = Label "Video Downloader" 24 14 350 20 $fg
$head.Font = [Drawing.Font]::new("Segoe UI",19,[Drawing.FontStyle]::Bold)
$head.Height = 36
$form.Controls.Add($head)
$form.Controls.Add((Label "yt-dlp manager · очередь · предпросмотр · параллельные загрузки" 26 50 650 9 $muted))
$stats = Label "0 активных   ·   0 KB/s   ·   0/0 готово" 650 22 440 9 $muted
$stats.TextAlign = "MiddleRight"; $stats.Anchor = "Top,Right"; $form.Controls.Add($stats)
$status = Label "● Готов" 1110 18 125 9 $muted
$status.Anchor = "Top,Right"; $status.BackColor = $field; $status.TextAlign = "MiddleCenter"; $status.Height = 30
Make-Rounded $status 16; $form.Controls.Add($status)
$minTray = Button "▁  В трей" 1248 16 105 32 $field
$minTray.Anchor = "Top,Right"; Make-Rounded $minTray 12; $form.Controls.Add($minTray)

$left = [Windows.Forms.Panel]::new()
$left.Location = [Drawing.Point]::new(22,82); $left.Size = [Drawing.Size]::new(500,700); $left.Anchor = "Top,Bottom,Left"; $left.BackColor = $panel
Make-Rounded $left 22; $form.Controls.Add($left)
$middle = [Windows.Forms.Panel]::new()
$middle.Location = [Drawing.Point]::new(538,82); $middle.Size = [Drawing.Size]::new(470,700); $middle.Anchor = "Top,Bottom,Left,Right"; $middle.BackColor = $panel
Make-Rounded $middle 22; $form.Controls.Add($middle)
$right = [Windows.Forms.Panel]::new()
$right.Location = [Drawing.Point]::new(1024,82); $right.Size = [Drawing.Size]::new(330,700); $right.Anchor = "Top,Bottom,Right"; $right.BackColor = $panel
Make-Rounded $right 22; $form.Controls.Add($right)

$left.Controls.Add((Label "Очередь URL" 16 14 180 11 $fg))
$queueCount = Label "0 URL" 395 14 85 9 $muted; $queueCount.TextAlign = "MiddleRight"; $left.Controls.Add($queueCount)
$urlInput = [Windows.Forms.TextBox]::new(); $urlInput.Location = [Drawing.Point]::new(16,48); $urlInput.Size = [Drawing.Size]::new(360,28); StyleText $urlInput; $left.Controls.Add($urlInput)
$addUrl = Button "+ Добавить" 386 46 96 32 $accent; Make-Rounded $addUrl 12; $left.Controls.Add($addUrl)
$clipButton = Button "Добавить из буфера" 16 87 466 34 $field2; $clipButton.Visible = $false; Make-Rounded $clipButton 12; $left.Controls.Add($clipButton)

$queueGrid = [Windows.Forms.DataGridView]::new()
$queueGrid.Location = [Drawing.Point]::new(16,132); $queueGrid.Size = [Drawing.Size]::new(466,455); $queueGrid.Anchor = "Top,Bottom,Left,Right"
$queueGrid.BackgroundColor = $field; $queueGrid.BorderStyle = "None"; $queueGrid.RowHeadersVisible = $false; $queueGrid.AllowUserToAddRows = $false
$queueGrid.AllowUserToResizeRows = $false; $queueGrid.ReadOnly = $true; $queueGrid.MultiSelect = $true; $queueGrid.SelectionMode = "FullRowSelect"
$queueGrid.EnableHeadersVisualStyles = $false; $queueGrid.ColumnHeadersDefaultCellStyle.BackColor = $bg; $queueGrid.ColumnHeadersDefaultCellStyle.ForeColor = $muted
$queueGrid.DefaultCellStyle.BackColor = $field; $queueGrid.DefaultCellStyle.ForeColor = $fg; $queueGrid.DefaultCellStyle.SelectionBackColor = [Drawing.Color]::FromArgb(54,60,78)
$queueGrid.DefaultCellStyle.SelectionForeColor = $fg; $queueGrid.RowTemplate.Height = 30; $queueGrid.AllowDrop = $true
[void]$queueGrid.Columns.Add("valid",""); [void]$queueGrid.Columns.Add("url","URL"); [void]$queueGrid.Columns.Add("state","Статус")
$queueGrid.Columns["valid"].Width = 32; $queueGrid.Columns["url"].AutoSizeMode = "Fill"; $queueGrid.Columns["state"].Width = 92
$left.Controls.Add($queueGrid)

$loadList = Button "Открыть .txt" 16 604 105
$removeUrl = Button "Удалить" 129 604 105
$clearQueue = Button "Очистить" 242 604 105
$retry = Button "↻ Ошибки" 355 604 127 34 $warn
$retry.Enabled = $false
foreach ($b in @($loadList,$removeUrl,$clearQueue,$retry)) { Make-Rounded $b 12; $left.Controls.Add($b) }
$dropHint = Label "Можно перетащить .txt, .url или ссылку прямо сюда" 18 650 455 8.5 $muted
$dropHint.Anchor = "Bottom,Left,Right"; $left.Controls.Add($dropHint)

$middle.Controls.Add((Label "Загрузки" 16 14 180 11 $fg))
$downloadGrid = [Windows.Forms.DataGridView]::new()
$downloadGrid.Location = [Drawing.Point]::new(16,48); $downloadGrid.Size = [Drawing.Size]::new(438,365); $downloadGrid.Anchor = "Top,Left,Right"
$downloadGrid.BackgroundColor = $field; $downloadGrid.BorderStyle = "None"; $downloadGrid.RowHeadersVisible = $false; $downloadGrid.AllowUserToAddRows = $false
$downloadGrid.ReadOnly = $true; $downloadGrid.SelectionMode = "FullRowSelect"; $downloadGrid.MultiSelect = $false; $downloadGrid.EnableHeadersVisualStyles = $false
$downloadGrid.ColumnHeadersDefaultCellStyle.BackColor = $bg; $downloadGrid.ColumnHeadersDefaultCellStyle.ForeColor = $muted
$downloadGrid.DefaultCellStyle.BackColor = $field; $downloadGrid.DefaultCellStyle.ForeColor = $fg; $downloadGrid.DefaultCellStyle.SelectionBackColor = [Drawing.Color]::FromArgb(54,60,78)
$downloadGrid.DefaultCellStyle.SelectionForeColor = $fg; $downloadGrid.RowTemplate.Height = 31
[void]$downloadGrid.Columns.Add("slot","Слот"); [void]$downloadGrid.Columns.Add("state","Статус"); [void]$downloadGrid.Columns.Add("video","Видео"); [void]$downloadGrid.Columns.Add("file","Файл")
$downloadGrid.Columns["slot"].Width = 48; $downloadGrid.Columns["state"].Width = 170; $downloadGrid.Columns["video"].AutoSizeMode = "Fill"; $downloadGrid.Columns["file"].Visible = $false
$middle.Controls.Add($downloadGrid)

$openFile = Button "Открыть файл" 16 426 130
$openFolder = Button "Папка" 154 426 130
$copyPath = Button "Копировать путь" 292 426 162
foreach ($b in @($openFile,$openFolder,$copyPath)) { Make-Rounded $b 12; $middle.Controls.Add($b) }
$middle.Controls.Add((Label "Лог" 16 478 100 10 $fg))
$log = [Windows.Forms.RichTextBox]::new()
$log.Location = [Drawing.Point]::new(16,510); $log.Size = [Drawing.Size]::new(438,170); $log.Anchor = "Top,Bottom,Left,Right"
$log.ReadOnly = $true; $log.BackColor = [Drawing.Color]::FromArgb(13,15,19); $log.ForeColor = $muted; $log.BorderStyle = "None"; $log.Font = [Drawing.Font]::new("Cascadia Mono",8.5)
$middle.Controls.Add($log)

$right.Controls.Add((Label "Предпросмотр" 16 14 180 11 $fg))
$thumb = [Windows.Forms.PictureBox]::new(); $thumb.Location = [Drawing.Point]::new(16,48); $thumb.Size = [Drawing.Size]::new(298,168)
$thumb.BackColor = $bg; $thumb.SizeMode = "Zoom"; Make-Rounded $thumb 14; $right.Controls.Add($thumb)
$previewTitle = Label "Выбери URL в очереди" 16 226 298 10 $fg; $previewTitle.AutoEllipsis = $true; $right.Controls.Add($previewTitle)
$previewMeta = Label "Название, длительность, качество и обложка появятся здесь." 16 252 298 8.5 $muted
$previewMeta.Height = 42; $previewMeta.AutoEllipsis = $true; $right.Controls.Add($previewMeta)

$right.Controls.Add((Label "Качество" 16 304 130 9 $muted))
$quality = [Windows.Forms.ComboBox]::new(); $quality.Location = [Drawing.Point]::new(174,302); $quality.Size = [Drawing.Size]::new(140,28)
$quality.DropDownStyle = "DropDownList"; $quality.BackColor = $field; $quality.ForeColor = $fg
foreach ($q in @("Best","2160","1440","1080","720")) { [void]$quality.Items.Add($q) }; $quality.SelectedIndex = 0; $right.Controls.Add($quality)

$right.Controls.Add((Label "Лимит скорости" 16 340 130 9 $muted))
$rateLimit = [Windows.Forms.ComboBox]::new(); $rateLimit.Location = [Drawing.Point]::new(174,338); $rateLimit.Size = [Drawing.Size]::new(140,28)
$rateLimit.DropDownStyle = "DropDown"; $rateLimit.BackColor = $field; $rateLimit.ForeColor = $fg
foreach ($v in @("Без лимита","1M","5M","10M","25M","50M")) { [void]$rateLimit.Items.Add($v) }; $rateLimit.SelectedIndex = 0; $right.Controls.Add($rateLimit)

$right.Controls.Add((Label "Параллельные URL" 16 376 145 9 $muted))
$threads = [Windows.Forms.NumericUpDown]::new(); $threads.Location = [Drawing.Point]::new(224,374); $threads.Size = [Drawing.Size]::new(90,27)
$threads.Minimum = 1; $threads.Maximum = 32; $threads.Value = 4; $threads.BackColor = $field; $threads.ForeColor = $fg; $right.Controls.Add($threads)

$right.Controls.Add((Label "Фрагменты" 16 412 145 9 $muted))
$fragments = [Windows.Forms.ComboBox]::new(); $fragments.Location = [Drawing.Point]::new(224,410); $fragments.Size = [Drawing.Size]::new(90,27)
$fragments.DropDownStyle = "DropDownList"; $fragments.BackColor = $field; $fragments.ForeColor = $fg
[void]$fragments.Items.Add("Авто"); 1..4 | ForEach-Object { [void]$fragments.Items.Add([string]$_) }; $fragments.SelectedIndex = 0; $right.Controls.Add($fragments)

$archive = [Windows.Forms.CheckBox]::new(); $archive.Text = "Архив по дате"; $archive.Location = [Drawing.Point]::new(16,448); $archive.Size = [Drawing.Size]::new(140,24); $archive.ForeColor = $fg; $right.Controls.Add($archive)
$sponsor = [Windows.Forms.CheckBox]::new(); $sponsor.Text = "SponsorBlock"; $sponsor.Location = [Drawing.Point]::new(166,448); $sponsor.Size = [Drawing.Size]::new(140,24); $sponsor.ForeColor = $fg; $right.Controls.Add($sponsor)
$audio = [Windows.Forms.CheckBox]::new(); $audio.Text = "Только MP3"; $audio.Location = [Drawing.Point]::new(16,476); $audio.Size = [Drawing.Size]::new(140,24); $audio.ForeColor = $fg; $right.Controls.Add($audio)

$right.Controls.Add((Label "Папка загрузки" 16 510 140 8.5 $muted))
$out = [Windows.Forms.TextBox]::new(); $out.Location = [Drawing.Point]::new(16,534); $out.Size = [Drawing.Size]::new(250,27)
$out.Text = Join-Path (Join-Path $env:USERPROFILE "Downloads") "downloaded-video"; StyleText $out; $right.Controls.Add($out)
$pickOut = Button "…" 274 533 40 29; Make-Rounded $pickOut 10; $right.Controls.Add($pickOut)

$right.Controls.Add((Label "Cookies" 16 568 140 8.5 $muted))
$cookies = [Windows.Forms.TextBox]::new(); $cookies.Location = [Drawing.Point]::new(16,592); $cookies.Size = [Drawing.Size]::new(250,27); StyleText $cookies; $right.Controls.Add($cookies)
$pickCookies = Button "…" 274 591 40 29; Make-Rounded $pickCookies 10; $right.Controls.Add($pickCookies)

$updateYt = Button "Обновить yt-dlp" 16 630 142 34; Make-Rounded $updateYt 12; $right.Controls.Add($updateYt)
$checkVersion = Label "" 168 636 146 8 $muted; $checkVersion.TextAlign = "MiddleRight"; $right.Controls.Add($checkVersion)

$bar = [Windows.Forms.ProgressBar]::new(); $bar.Location = [Drawing.Point]::new(22,794); $bar.Size = [Drawing.Size]::new(995,18); $bar.Anchor = "Bottom,Left,Right"; $form.Controls.Add($bar)
$progressText = Label "0 / 0" 1026 789 120 9 $muted; $progressText.Anchor = "Bottom,Right"; $progressText.TextAlign = "MiddleRight"; $form.Controls.Add($progressText)
$stop = Button "■  Стоп" 1155 783 90 40 $danger; $stop.Anchor = "Bottom,Right"; $stop.Enabled = $false; Make-Rounded $stop 14; $form.Controls.Add($stop)
$start = Button "▶  Начать" 1254 783 100 40 $accent; $start.Anchor = "Bottom,Right"; $start.Enabled = $false; Make-Rounded $start 14; $form.Controls.Add($start)

$folderDialog = [Windows.Forms.FolderBrowserDialog]::new()
$fileDialog = [Windows.Forms.OpenFileDialog]::new()
$trayMenu = [Windows.Forms.ContextMenuStrip]::new()
$trayShow = $trayMenu.Items.Add("Открыть"); $trayFolder = $trayMenu.Items.Add("Открыть папку загрузки"); [void]$trayMenu.Items.Add("-"); $trayExit = $trayMenu.Items.Add("Выход")
$tray = [Windows.Forms.NotifyIcon]::new(); $tray.Icon = $form.Icon; $tray.Text = "Video Downloader"; $tray.Visible = $true; $tray.ContextMenuStrip = $trayMenu

function Restore-Window { $form.Show(); $form.WindowState = "Normal"; $form.Activate() }
function Hide-ToTray {
    $form.Hide()
    $tray.BalloonTipTitle = "Video Downloader"; $tray.BalloonTipText = "Приложение продолжает работать в трее."; $tray.BalloonTipIcon = "Info"; $tray.ShowBalloonTip(1800)
}

$addUrl.Add_Click({
    $values = @(Extract-Urls $urlInput.Text)
    if ($values.Count -eq 0) { [Windows.Forms.MessageBox]::Show("В строке нет корректного http/https URL.","Video Downloader") | Out-Null; return }
    Add-Urls $values; $urlInput.Clear()
})
$urlInput.Add_KeyDown({ param($s,$e); if ($e.KeyCode -eq [Windows.Forms.Keys]::Enter) { $addUrl.PerformClick(); $e.SuppressKeyPress = $true } })
$loadList.Add_Click({ $fileDialog.Filter = "URL lists (*.txt;*.url)|*.txt;*.url|All files (*.*)|*.*"; if ($fileDialog.ShowDialog() -eq "OK") { Add-FileToQueue $fileDialog.FileName } })
$removeUrl.Add_Click({ foreach ($r in @($queueGrid.SelectedRows)) { if (-not $r.IsNewRow) { $queueGrid.Rows.Remove($r) } }; Update-QueueSummary })
$clearQueue.Add_Click({ $queueGrid.Rows.Clear(); Update-QueueSummary; $previewTitle.Text = "Выбери URL в очереди"; $previewMeta.Text = ""; $thumb.Image = $null })
$retry.Add_Click({ $items = @($script:failedUrls.ToArray()); if ($items.Count -gt 0) { StartDownload $items } })
$clipButton.Add_Click({ try { $values = @(Extract-Urls ([Windows.Forms.Clipboard]::GetText())); if ($values.Count -gt 0) { Add-Urls $values }; $clipButton.Visible = $false } catch {} })
$queueGrid.Add_SelectionChanged({ if ($queueGrid.SelectedRows.Count -eq 1) { $u = [string]$queueGrid.SelectedRows[0].Cells["url"].Value; if (Is-ValidUrl $u -and $u -ne $script:previewUrl) { Start-Preview $u } } })
$queueGrid.Add_DragEnter({
    param($s,$e)
    if ($e.Data.GetDataPresent([Windows.Forms.DataFormats]::FileDrop) -or $e.Data.GetDataPresent([Windows.Forms.DataFormats]::Text)) {
        $e.Effect = [Windows.Forms.DragDropEffects]::Copy; $queueGrid.BackgroundColor = [Drawing.Color]::FromArgb(54,60,78); $dropHint.Text = "Отпусти — добавлю содержимое в очередь"; $dropHint.ForeColor = $accent
    } else { $e.Effect = [Windows.Forms.DragDropEffects]::None }
})
$queueGrid.Add_DragLeave({ $queueGrid.BackgroundColor = $field; $dropHint.Text = "Можно перетащить .txt, .url или ссылку прямо сюда"; $dropHint.ForeColor = $muted })
$queueGrid.Add_DragDrop({
    param($s,$e)
    $queueGrid.BackgroundColor = $field; $dropHint.Text = "Можно перетащить .txt, .url или ссылку прямо сюда"; $dropHint.ForeColor = $muted
    if ($e.Data.GetDataPresent([Windows.Forms.DataFormats]::FileDrop)) { foreach ($f in @($e.Data.GetData([Windows.Forms.DataFormats]::FileDrop))) { Add-FileToQueue ([string]$f) } }
    if ($e.Data.GetDataPresent([Windows.Forms.DataFormats]::Text)) { Add-Urls @(Extract-Urls ([string]$e.Data.GetData([Windows.Forms.DataFormats]::Text))) }
})
$pickOut.Add_Click({ $folderDialog.SelectedPath = $out.Text; if ($folderDialog.ShowDialog() -eq "OK") { $out.Text = $folderDialog.SelectedPath } })
$pickCookies.Add_Click({ $fileDialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"; if ($fileDialog.ShowDialog() -eq "OK") { $cookies.Text = $fileDialog.FileName } })
$openFile.Add_Click({ Open-SelectedFile }); $openFolder.Add_Click({ Open-SelectedFolder }); $copyPath.Add_Click({ Copy-SelectedPath }); $downloadGrid.Add_CellDoubleClick({ Open-SelectedFile })
$updateYt.Add_Click({ Start-YtDlpUpdate }); $start.Add_Click({ StartDownload }); $stop.Add_Click({ StopDownload })
$minTray.Add_Click({ Hide-ToTray }); $tray.Add_DoubleClick({ Restore-Window }); $trayShow.Add_Click({ Restore-Window })
$trayFolder.Add_Click({ if (Test-Path $out.Text) { Start-Process explorer.exe -ArgumentList @($out.Text) } })
$trayExit.Add_Click({ $script:allowExit = $true; $form.Close() })
$form.Add_Resize({ if ($form.WindowState -eq "Minimized") { Hide-ToTray } })
$form.Add_KeyDown({ param($s,$e); if ($e.Control -and $e.KeyCode -eq [Windows.Forms.Keys]::Enter) { StartDownload; $e.SuppressKeyPress = $true } })

$timer = [Windows.Forms.Timer]::new(); $timer.Interval = 400
$timer.Add_Tick({
    TailEvents; TailLog; Complete-Preview; Complete-YtDlpUpdate
    if ($script:proc) {
        try {
            if ($script:proc.HasExited) { $timer.Stop(); FinishDownload; $timer.Start() }
        } catch {}
    }
})
$clipboardTimer = [Windows.Forms.Timer]::new(); $clipboardTimer.Interval = 1200
$clipboardTimer.Add_Tick({
    try {
        if (-not [Windows.Forms.Clipboard]::ContainsText()) { $clipButton.Visible = $false; return }
        $values = @(Extract-Urls ([Windows.Forms.Clipboard]::GetText()))
        if ($values.Count -gt 0) {
            $joined = $values -join [Environment]::NewLine
            if ($joined -ne $script:lastClipboard) { $script:lastClipboard = $joined; $clipButton.Text = "＋ Добавить из буфера ($($values.Count))" }
            $clipButton.Visible = $true
        } else { $clipButton.Visible = $false }
    } catch {}
})
$form.Add_Shown({
    $timer.Start(); $clipboardTimer.Start()
    try { $v = & $ytDlp --version 2>$null | Select-Object -First 1; if ($v) { $checkVersion.Text = "yt-dlp $v" } } catch {}
})
$form.Add_FormClosing({
    param($s,$e)
    if (-not $script:allowExit -and $script:proc -and -not $script:proc.HasExited) {
        $a = [Windows.Forms.MessageBox]::Show("Идёт загрузка. Остановить её и закрыть программу?","Video Downloader","YesNo","Warning")
        if ($a -ne "Yes") { $e.Cancel = $true; return }
        try { $script:proc.Kill($true) } catch {}
    }
    try {
        if ($script:previewJob) { Stop-Job $script:previewJob -ErrorAction SilentlyContinue; Remove-Job $script:previewJob -Force -ErrorAction SilentlyContinue }
        if ($script:updateJob) { Stop-Job $script:updateJob -ErrorAction SilentlyContinue; Remove-Job $script:updateJob -Force -ErrorAction SilentlyContinue }
    } catch {}
    $tray.Visible = $false; $tray.Dispose(); $timer.Stop(); $clipboardTimer.Stop(); CleanTemp
})

Update-QueueSummary
[void]$form.ShowDialog()
