#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'lib/VideoDownloader.Profiles.psm1') -Force
function Assert([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
}
$ids = @('Universal','4K archive','MP4 compatibility','Music MP3','Subtitles archive')
$titles = @('Универсальный','Архив 4K','Совместимый MP4','Музыка MP3','Архив с субтитрами')
Assert ((@(Get-VdProfileCaptions) -join '|') -ceq ($titles -join '|')) 'Legacy Russian profile order differs from C#.'
for ($i=0;$i -lt $ids.Count;$i++) {
    Assert ((Get-VdProfileCaption $ids[$i]) -ceq $titles[$i]) "English checkpoint restore $($ids[$i])"
    Assert ((Get-VdProfileId $titles[$i]) -ceq $ids[$i]) "Russian selection must persist canonical ID $($ids[$i])"
    Assert ((Get-VdProfileId $ids[$i]) -ceq $ids[$i]) 'Existing profile identifiers must remain stable.'
}
[xml]$xaml = Get-Content (Join-Path $root 'ui/MainWindow.xaml') -Raw -Encoding utf8
$nsm = [System.Xml.XmlNamespaceManager]::new($xaml.NameTable)
$nsm.AddNamespace('w','http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$nsm.AddNamespace('x','http://schemas.microsoft.com/winfx/2006/xaml')
foreach($id in @('RetryFailedButton','OpenOutputDirectoryButton','RegisterProtocolButton','OpenAppSettingsButton')) {
    $button=$xaml.SelectSingleNode("//w:Button[@x:Name='$id']",$nsm)
    Assert ($null -ne $button) "Shared WPF markup must define $id"
}
$gui = Get-Content (Join-Path $root 'ytdl-manager-gui.ps1') -Raw -Encoding utf8
Assert ($gui.Contains('$OpenOutputDirectoryButton.Add_Click')) 'Legacy GUI must wire Open output button.'
Assert ($gui.Contains('$OpenAppSettingsButton.Add_Click')) 'Legacy GUI must wire Open settings button.'
Assert ($gui.Contains('Get-VdProfileId')) 'Legacy GUI must persist canonical English profile identifiers.'
Assert ($gui.Contains('Get-VdProfileCaption')) 'Legacy GUI must restore English identifiers into Russian captions.'
Write-Host 'PowerShell/C# shared GUI controls and Russian profiles: PASS'
