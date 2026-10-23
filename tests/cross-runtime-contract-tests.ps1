#requires -Version 7.4
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
foreach ($module in @('Session','DownloadHistory','Cancellation','Profiles','Subscriptions','SubscriptionNames')) {
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
    $sources=@('Models','Persistence','ProfileCatalog','OwnedFiles','DownloadCleanup','SubscriptionNames','SubscriptionModels','SubscriptionStore') | ForEach-Object {Join-Path $root "native/$_.cs"}
    Add-Type -Path (@($platform)+$sources) -CompilerOptions '/nullable:enable'
    foreach($name in @('123','Канал: новый / архив','CON.txt','..','  Видео  канала.  ',('а'*79+'😀'),'LPT¹','video?')) {
        Assert ((ConvertTo-VdSubscriptionName -Name $name) -ceq [VideoDownloader.Native.SubscriptionNames]::Normalize($name)) 'Both runtimes must derive identical Windows-safe short folder names'
    }
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
        @{Url='https://example.com/readded';Status='Queued';DeferredToNextStart=$true;SubscriptionId='source-123';SubscriptionName='123'}
    );Settings=@{Output=(Join-Path $temp 'different-output')};Engine=$null}
    Write-VdSessionCheckpoint -Path $native.SessionPath -Session $session
    $loaded=$native.Restore();$native.Save($loaded.Queue,$loaded.Settings)
    $legacy=Read-VdSessionCheckpoint -Path $native.SessionPath
    $rows=@(Get-VdRestoredQueue -Session $legacy)
    Assert ($rows[0].PendingRemoval -and $rows[0].CleanupError -and $rows[0].StageDir -eq $stage -and $rows[0].CleanupPaths[0] -eq $owned -and $rows[0].RunDir -eq $run -and $rows[0].OutputRoot -eq $output) 'PowerShell -> C# -> PowerShell must preserve every cleanup field'
    Assert ($rows[1].DeferredToNextStart -and @(Get-VdResumeUrls -Queue $rows).Count -eq 1) 'Only the deferred new attempt may resume'
    Assert ($rows[1].SubscriptionId -ceq 'source-123' -and $rows[1].SubscriptionName -ceq '123' -and $loaded.Queue[1].SubscriptionName -ceq '123') 'PS -> native -> PS checkpoint must retain subscription identity and folder snapshots'
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
    # Same data must be editable from both runtimes, including success
    # provenance, one-time notices, and a removed source's retained provenance.
    $subscriptionDir=Join-Path $temp 'subscriptions-profile'
    $sourceA=Add-VdSubscription -Directory $subscriptionDir -Url 'https://example.org/channel?a=1' -DownloadExisting $false
    $mediaUrl='https://media.example.org/Video?id=Exact'
    Apply-VdSubscriptionScan -Directory $subscriptionDir -Id $sourceA.Id -Scan @{
        Title='Первый источник';FeedUrl='';Warning='';Items=@(@{Url=$mediaUrl;Title='Материал'})
    } -SuccessfulUrls @() | Out-Null
    $subscriptionStore=[VideoDownloader.Native.SubscriptionStore]::new($subscriptionDir)
    Assert ($subscriptionStore.Read().Items[0].State -eq 'Baseline') 'Native must preserve a PowerShell initial baseline'
    Assert ($subscriptionStore.Read().Subscriptions[0].ShortName -ceq 'Первый источник') 'Native must preserve the PowerShell auto-generated subscription short name'
    $subscriptionStore.SetShortName($sourceA.Id,'123')
    Assert ((Read-VdSubscriptions -Directory $subscriptionDir).Subscriptions[0].ShortName -ceq '123') 'Native rename must be visible in PowerShell'
    $null=Set-VdSubscriptionShortName -Directory $subscriptionDir -Id $sourceA.Id -Name 'Канал: архив'
    Assert ($subscriptionStore.Read().Subscriptions[0].ShortName -ceq 'Канал_ архив') 'PowerShell rename must preserve the same sanitized folder in native'
    $sourceB=$subscriptionStore.Add('https://example.org/playlist?list=other',$true,'')
    $scanItems=[Collections.Generic.List[VideoDownloader.Native.DiscoveredMedia]]::new()
    $scanItems.Add([VideoDownloader.Native.DiscoveredMedia]::new($mediaUrl,'Материал'))
    $scan=[VideoDownloader.Native.SubscriptionScan]::new('Второй источник',$scanItems,'','')
    $subscriptionStore.ApplyScan($sourceB.Id,$scan,[string[]]@()) | Out-Null
    $subscriptionStore.MarkQueued($sourceB.Id,[string[]]@($mediaUrl))
    $beforeSuccess=Read-VdSubscriptions -Directory $subscriptionDir
    Assert (@($beforeSuccess.Items|Where-Object State -eq 'Processed').Count -eq 0) 'Neither baseline nor queue acceptance may imply successful processing'
    Set-VdSubscriptionSuccessful -Directory $subscriptionDir -Url $mediaUrl | Out-Null
    Assert (@($subscriptionStore.Read().Items | Where-Object SubscriptionId -eq $sourceB.Id)[0].SuccessAttributed) 'PowerShell must persist attributable queued success for native'
    $duplicates=@((Read-VdSubscriptions -Directory $subscriptionDir).Items|Where-Object State -eq 'AlreadyProcessed')
    Assert ($duplicates.Count -eq 1 -and $duplicates[0].SubscriptionId -eq $sourceA.Id -and $duplicates[0].DuplicateSubscriptionId -eq $sourceB.Id -and -not $duplicates[0].DuplicateNotified) 'PS success must preserve exact cross-source origin for native'
    $nativeDuplicates=@($subscriptionStore.Read().Items|Where-Object State -eq 'AlreadyProcessed')
    $subscriptionStore.AcknowledgeDuplicates([VideoDownloader.Native.SubscriptionItem[]]$nativeDuplicates)
    $subscriptionStore.Remove($sourceB.Id)
    $afterRemove=Read-VdSubscriptions -Directory $subscriptionDir
    Assert ($afterRemove.Subscriptions.Count -eq 1 -and $afterRemove.Items[0].DuplicateNotified -and $afterRemove.Items[0].DuplicateSubscriptionId -eq $sourceB.Id) 'Acknowledgement and provenance must survive removing the other subscription'
    Sync-VdSubscriptionsSuccessful -Directory $subscriptionDir -SuccessfulUrls @($mediaUrl) | Out-Null
    Assert ($subscriptionStore.Read().Items[0].DuplicateNotified) 'Reconciliation must never reset an acknowledged duplicate notice'

    # A history-only result must never turn into an alleged subscription origin
    # on restart or when the other runtime discovers that exact URL later.
    $globalOnly='https://media.example.org/global-only'
    $globalSource=Add-VdSubscription -Directory $subscriptionDir -Url 'https://example.org/global-source'
    $globalScan=@{Title='Глобальный успех';FeedUrl='';Warning='';Items=@(@{Url=$globalOnly;Title='Материал'})}
    Apply-VdSubscriptionScan -Directory $subscriptionDir -Id $globalSource.Id -Scan $globalScan -SuccessfulUrls @($globalOnly) | Out-Null
    $reopened=[VideoDownloader.Native.SubscriptionStore]::new($subscriptionDir)
    $laterSource=$reopened.Add('https://example.org/later-source',$false,'')
    $globalItems=[Collections.Generic.List[VideoDownloader.Native.DiscoveredMedia]]::new()
    $globalItems.Add([VideoDownloader.Native.DiscoveredMedia]::new($globalOnly,'Материал'))
    $reopened.ApplyScan($laterSource.Id,[VideoDownloader.Native.SubscriptionScan]::new('Позже',$globalItems,'',''),[string[]]@()) | Out-Null
    $reopened.MarkSuccessful($globalOnly)
    $globalRows=@((Read-VdSubscriptions -Directory $subscriptionDir).Items | Where-Object Url -ceq $globalOnly)
    Assert ($globalRows.Count -eq 2 -and @($globalRows | Where-Object {$_.State -ne 'Processed' -or $_.SuccessAttributed -or $_.DuplicateSubscriptionId -or $_.DuplicateNotified}).Count -eq 0) 'PS global-only success must remain unattributed after native discovery and success replay'
    $lastSource=Add-VdSubscription -Directory $subscriptionDir -Url 'https://example.org/last-source'
    Apply-VdSubscriptionScan -Directory $subscriptionDir -Id $lastSource.Id -Scan $globalScan | Out-Null
    $globalRows=@($reopened.Read().Items | Where-Object Url -ceq $globalOnly)
    Assert ($globalRows.Count -eq 3 -and @($globalRows | Where-Object {$_.State -ne 'Processed' -or $_.SuccessAttributed -or $_.DuplicateSubscriptionId}).Count -eq 0) 'Native persisted global-only result must also remain unattributed when PowerShell adds another subscription'
    Write-Host 'Cross-runtime history, checkpoint, cleanup, subscriptions and all profile transitions: PASS'
} finally {Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue}
