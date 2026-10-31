#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$xamlFiles = @{
    'MainWindow.xaml' = @('QueueGrid','DownloadGrid','PasteClipboardButton','PauseButton','CodecCombo','ContainerCombo',
        'WriteSubsCheck','FilenameTemplateBox','FilenameTemplateLabel','FilenameTemplateHelpButton','DependencyStatus','OpenOutputDirectoryButton','OpenAppSettingsButton','ShowVideoTitlesCheck')
    'DuplicateDownloadDialog.xaml' = @('YesButton','NoButton','YesAllButton','NoAllButton','CancelButton','DuplicateUrl')
    'SubscriptionsWindow.xaml' = @('SubscriptionGrid','SubscriptionInboxGrid','SubscriptionCheckProgressPanel',
        'SubscriptionCheckOverallProgress','SubscriptionCheckActivityProgress','CancelSubscriptionCheckButton','QueueSubscriptionItemsButton','FullSubscriptionCheck',
        'SelectedSubscriptionUrlBox','CopySubscriptionUrlButton','SubscriptionDetailsPanel','SubscriptionDetailsSplitter',
        'SubscriptionPreviewTitle','SubscriptionPreviewUrlBox','SubscriptionPreviewInfo','SubscriptionPreviewImage',
        'SubscriptionPreviewSecondImage','SubscriptionPreviewStatus','SubscriptionPreviewError','SubscriptionOpenVideoButton','SubscriptionCopyVideoUrlButton')
}
foreach ($entry in $xamlFiles.GetEnumerator()) {
    [xml]$markup = Get-Content -LiteralPath (Join-Path $root "src/VideoDownloader/UI/Views/$($entry.Key)") -Raw -Encoding utf8
    if ($markup.DocumentElement.LocalName -ne 'Window') { throw "$($entry.Key) must have a Window root" }
    $nsm = [Xml.XmlNamespaceManager]::new($markup.NameTable)
    $nsm.AddNamespace('x','http://schemas.microsoft.com/winfx/2006/xaml')
    foreach ($name in $entry.Value) {
        if ($null -eq $markup.SelectSingleNode("//*[@x:Name='$name']", $nsm)) { throw "$($entry.Key) is missing control: $name" }
    }
}
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw "Developer script has parse errors: $($script.Name): $($errors.Message -join '; ')" }
}
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src/VideoDownloader/VideoDownloader.csproj') -Raw
if ($project.Project.PropertyGroup.OutputType -cne 'WinExe' -or $project.Project.PropertyGroup.UseWPF -cne 'true') {
    throw 'The application project must produce the native WPF WinExe.'
}
$iconPath = Join-Path $root 'assets/app.ico'
if ($IsWindows) {
    Add-Type -AssemblyName System.Drawing
    $icon = [Drawing.Icon]::new($iconPath)
    $icon.Dispose()
} else {
    # Structural checks can run on a Linux checkout; the Windows path above
    # also opens the actual resource with the platform icon decoder.
    $bytes = [IO.File]::ReadAllBytes($iconPath)
    if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes,0) -ne 0 -or
        [BitConverter]::ToUInt16($bytes,2) -ne 1 -or [BitConverter]::ToUInt16($bytes,4) -lt 1) { throw 'Invalid application icon' }
}
if (-not (Test-Path -LiteralPath (Join-Path $root 'collection-plugins/videodownloader/yt_dlp_plugins/extractor/vd_pornhub.py') -PathType Leaf)) {
    throw 'Missing bundled collection extractor'
}
Write-Host 'Native project, WPF markup, resources and developer script smoke: PASS'
