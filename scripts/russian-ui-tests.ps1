#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
$assembly = Join-Path $root "src/VideoDownloader/bin/$Configuration/net8.0-windows/win-x64/VideoDownloader.dll"
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Build the native WPF application first: $assembly" }
[void][Reflection.Assembly]::LoadFrom($assembly)
$ids = @('Universal','4K archive','MP4 compatibility','Music MP3','Subtitles archive')
$titles = @('Универсальный','Архив 4K','Совместимый MP4','Музыка MP3','Архив с субтитрами')
Assert (([VideoDownloader.Native.ProfileCatalog]::DisplayNames -join '|') -ceq ($titles -join '|')) 'Russian native profile order changed.'
for ($i=0; $i -lt $ids.Count; $i++) {
    Assert ([VideoDownloader.Native.ProfileCatalog]::Display($ids[$i]) -ceq $titles[$i]) "English checkpoint restore: $($ids[$i])"
    Assert ([VideoDownloader.Native.ProfileCatalog]::ToId($titles[$i]) -ceq $ids[$i]) "Russian selection must persist canonical ID: $($ids[$i])"
    Assert ([VideoDownloader.Native.ProfileCatalog]::ToId($ids[$i]) -ceq $ids[$i]) 'Existing profile identifiers must remain stable.'
}
[xml]$xaml = Get-Content -LiteralPath (Join-Path $root 'src/VideoDownloader/UI/Views/MainWindow.xaml') -Raw -Encoding utf8
$nsm = [Xml.XmlNamespaceManager]::new($xaml.NameTable)
$nsm.AddNamespace('w','http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$nsm.AddNamespace('x','http://schemas.microsoft.com/winfx/2006/xaml')
foreach ($id in @('RetryFailedButton','OpenOutputDirectoryButton','RegisterProtocolButton','OpenAppSettingsButton')) {
    Assert ($null -ne $xaml.SelectSingleNode("//w:Button[@x:Name='$id']", $nsm)) "WPF markup must define $id"
}
Write-Host 'Native Russian profile mapping and folder controls: PASS (handlers are exercised by the real UI tests).'
