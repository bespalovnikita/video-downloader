#requires -Version 7.4
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
foreach ($module in @('Session','DownloadHistory','Cancellation','Profiles')) {
    Import-Module (Join-Path $root "lib/VideoDownloader.$module.psm1") -Force
}
function Assert([bool]$Value,[string]$Message) {if(-not $Value){throw $Message}}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('vd-cross-runtime-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
try {
    # Compile the actual persistence/model/profile/filesystem sources. Only the
    # WPF notification boundary is stubbed; Windows CI also tests the real GUI.
    $platform=Join-Path $temp 'Platform.cs'
    @'
global using System;
global using System.IO;
global using System.Linq;
global using System.Collections.Generic;
global using System.Threading;
namespace System.Windows {
 public class Application { public static Application? Current=>null; public Dispatcher Dispatcher=>new(); }
 public class Dispatcher { public bool CheckAccess()=>true; public object BeginInvoke(Action action){action();return new();} }
}
'@ | Set-Content -LiteralPath $platform
    $sources=@('Models','Persistence','ProfileCatalog','OwnedFiles','DownloadCleanup') | ForEach-Object {Join-Path $root "native/$_.cs"}
    Add-Type -Path (@($platform)+$sources) -CompilerOptions '/nullable:enable'
    $native=[VideoDownloader.Native.AppPersistence]::new((Join-Path $temp 'profile'))
    $seed='{"Version":1,"Url":"https://example.com/старый"}'
    [IO.File]::WriteAllText($native.HistoryPath,$seed,[Text.UTF8Encoding]::new($false))
    $native.AppendSuccess('https://example.com/native')
    Add-VdSuccessfulDownloadUrl -Path $native.HistoryPath -Url 'https://example.com/powershell'
    Assert ((Read-VdDownloadHistory -Path $native.HistoryPath).Count -eq 3 -and $native.ReadHistory().Count -eq 3) 'Both runtimes must append compatible history after an unterminated record'

    $output=Join-Path $temp 'output';$run=Join-Path $temp 'run'
    $stage=Join-Path $run ([guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage);[void][IO.Directory]::CreateDirectory($output)
    $owned=Join-Path $output 'owned.mp4';[IO.File]::WriteAllText($owned,'OWNED')
    [IO.File]::WriteAllText((Join-Path $stage 'partial'),'PARTIAL')
    $session=@{Queue=@(
        @{Url='https://example.com/cleanup';Status='Interrupted';PendingRemoval=$true;AttemptFinished=$true;
          CleanupError='Locked file';CleanupPaths=@($owned);StageDir=$stage;RunDir=$run;OutputRoot=$output},
        @{Url='https://example.com/readded';Status='Queued';DeferredToNextStart=$true}
    );Settings=@{Output=(Join-Path $temp 'different-output')};Engine=$null}
    Write-VdSessionCheckpoint -Path $native.SessionPath -Session $session
    $loaded=$native.Restore();$native.Save($loaded.Queue,$loaded.Settings)
    $legacy=Read-VdSessionCheckpoint -Path $native.SessionPath
    $rows=@(Get-VdRestoredQueue -Session $legacy)
    Assert ($rows[0].PendingRemoval -and $rows[0].CleanupError -and $rows[0].StageDir -eq $stage -and $rows[0].CleanupPaths[0] -eq $owned -and $rows[0].RunDir -eq $run -and $rows[0].OutputRoot -eq $output) 'PowerShell -> C# -> PowerShell must preserve every cleanup field'
    Assert ($rows[1].DeferredToNextStart -and @(Get-VdResumeUrls -Queue $rows).Count -eq 1) 'Only the deferred new attempt may resume'
    Remove-VdStagingDirectory -StageDir $rows[0].StageDir -RunDir $rows[0].RunDir
    Assert (-not [IO.Directory]::Exists($stage)) 'PowerShell must clean C# staging names after migration'
    $legacyStage=Join-Path $run ('download-1-'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($legacyStage)
    $loaded.Queue[0].StageDir=$legacyStage
    [VideoDownloader.Native.DownloadCleanup]::Retry($loaded.Queue[0])
    Assert (-not [IO.Directory]::Exists($legacyStage) -and -not [IO.File]::Exists($owned)) 'C# must clean PowerShell staging and owned paths using original roots'

    $outside=Join-Path $temp 'outside.mp4';[IO.File]::WriteAllText($outside,'PRESERVE');[IO.File]::WriteAllText($owned,'OWNED')
    $refused=$false
    try { Remove-VdOwnedFiles -OutputRoot $output -Paths @($owned,$outside) | Out-Null } catch {$refused=$true}
    Assert ($refused -and [IO.File]::Exists($owned) -and [IO.File]::ReadAllText($outside) -eq 'PRESERVE') 'PowerShell must validate the whole cleanup batch before deleting files'

    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'ytdl-manager-gui.ps1'),[ref]$tokens,[ref]$errors)
    $profile=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Apply-Profile'},$true)
    . ([scriptblock]::Create($profile.Extent.Text))
    foreach($name in @('QualityCombo','CodecCombo','ContainerCombo')) {Set-Variable $name ([pscustomobject]@{SelectedItem=''})}
    foreach($name in @('AudioOnlyCheck','ArchiveCheck','WriteSubsCheck','AutoSubsCheck','EmbedSubsCheck','EmbedThumbnailCheck','EmbedMetadataCheck','EmbedChaptersCheck')) {Set-Variable $name ([pscustomobject]@{IsChecked=$false})}
    $ids=@('Universal','4K archive','MP4 compatibility','Music MP3','Subtitles archive')
    foreach($previous in $ids) {foreach($target in $ids) {
        Apply-Profile $previous;Apply-Profile $target
        $options=[VideoDownloader.Native.DownloadOptions]::new()
        [VideoDownloader.Native.ProfileCatalog]::Apply($options,$previous)
        [VideoDownloader.Native.ProfileCatalog]::Apply($options,$target)
        Assert ($QualityCombo.SelectedItem -ceq $options.Quality -and $CodecCombo.SelectedItem -ceq $options.Codec -and $ContainerCombo.SelectedItem -ceq $options.Container) "Profile format differs across runtimes: $previous -> $target"
        foreach($field in @('AudioOnly','Archive','WriteSubs','AutoSubs','EmbedSubs','EmbedThumbnail','EmbedMetadata','EmbedChapters')) {
            Assert ((Get-Variable ($field+'Check') -ValueOnly).IsChecked -eq $options.$field) "Profile $field differs across runtimes: $previous -> $target"
        }
    }}
    Write-Host 'Cross-runtime history, checkpoint, cleanup and all profile transitions: PASS'
} finally {Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue}
