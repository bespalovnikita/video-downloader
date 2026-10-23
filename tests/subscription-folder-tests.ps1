#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('vd-folder-routing-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$oldLocalAppData=$env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA=Join-Path $temp 'profile'
    Copy-Item -LiteralPath (Join-Path $root 'ytdl-manager-v8.ps1') -Destination $temp
    Copy-Item -LiteralPath (Join-Path $root 'lib') -Destination (Join-Path $temp 'lib') -Recurse
    [IO.File]::WriteAllBytes((Join-Path $temp 'yt-dlp.exe'),[byte[]]@()) # DryRun must never launch it.
    $queue=Join-Path $temp 'queue.txt';$manifest=Join-Path $temp 'folders.json';$output=Join-Path $temp 'output'
    $upper='https://example.org/Video';$lower='https://example.org/video';$ordinary='https://example.org/ordinary'
    [IO.File]::WriteAllLines($queue,[string[]]@($upper,$lower,$ordinary))
    $pwsh=(Get-Process -Id $PID).Path
    function Run-Dry([string[]]$Extra=@()) {
        $messages=@(& $pwsh -NoProfile -File (Join-Path $temp 'ytdl-manager-v8.ps1') -In $queue -Out $output -DryRun @Extra 2>&1)
        return @{Code=$LASTEXITCODE;Text=($messages -join "`n")}
    }
    $valid=@{Version=1;Items=@(@{Url=$upper;Folder='123'},@{Url=$lower;Folder='Канал 456'})}
    [IO.File]::WriteAllText($manifest,($valid|ConvertTo-Json -Depth 4))
    $result=Run-Dry @('-ItemFoldersFile',$manifest)
    Assert ($result.Code -eq 0 -and $result.Text.Contains("DRY: $upper -> "+(Join-Path $output '123')) -and
        $result.Text.Contains("DRY: $lower -> "+(Join-Path $output 'Канал 456')) -and $result.Text.Contains("DRY: $ordinary -> $output")) 'Mixed folders must route exact case-distinct URLs separately and preserve ordinary output'
    $result=Run-Dry @('-Archive','-ItemFoldersFile',$manifest)
    Assert ($result.Code -eq 0 -and $result.Text.Contains("DRY: $upper -> "+(Join-Path (Join-Path $output (Get-Date -Format 'yyyy-MM-dd')) '123'))) 'Archive must keep its date prefix before the subscription folder'
    $result=Run-Dry
    Assert ($result.Code -eq 0 -and $result.Text.Contains("DRY: $upper -> $output")) 'Old CLI calls without a manifest must keep their destination'
    foreach($bad in @(
        '{"Version":2,"Items":[]}',
        '{"Version":1,"Items":[],"Extra":true}',
        '{"Version":1,"Items":[],"Version":1}',
        '{"Version":1,"Items":[{"Url":"https://example.org/Video","Folder":"../escape"}]}',
        '{"Version":1,"Items":[{"Url":"https://example.org/Video","Folder":"C:\\escape"}]}',
        '{"Version":1,"Items":[{"Url":"https://example.org/Video","Folder":"CON.txt"}]}',
        '{"Version":1,"Items":[{"Url":"https://example.org/Video","Folder":null}]}',
        '{"Version":1,"Items":[{"Url":"https://example.org/Video","Folder":""}]}',
        '{"Version":1,"Items":[{"Url":"https://example.org/Video","Folder":"123","Folder":"456"}]}',
        '{"Version":1,"Items":[{"Url":"file:///outside","Folder":"123"}]}',
        '{"Version":1,"Items":[{"Url":"https://example.org/Video","Folder":"123"},{"Url":"https://example.org/Video","Folder":"456"}]}'
    )) {
        [IO.File]::WriteAllText($manifest,$bad)
        $result=Run-Dry @('-ItemFoldersFile',$manifest)
        Assert ($result.Code -ne 0 -and -not $result.Text.Contains('DRY:')) 'Malformed or unsafe routing must fail before any job or dry-run result'
    }
    [IO.File]::WriteAllBytes($manifest,[byte[]]::new(16MB+1))
    Assert ((Run-Dry @('-ItemFoldersFile',$manifest)).Code -ne 0) 'Oversized manifests must fail before parsing'
    Assert (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'VideoDownloader/successful-downloads.jsonl'))) 'Routing DryRun must never create success history'
    Write-Host 'Subscription folder manifest, exact URL routing, Archive, malformed input and DryRun: PASS'
} finally {
    $env:LOCALAPPDATA=$oldLocalAppData
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
