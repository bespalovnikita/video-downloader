#requires -Version 7.0
# Offline behavior tests against the actual engine and a native process stand-in.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$pwsh=(Get-Process -Id $PID).Path
Import-Module (Join-Path $root 'lib/VideoDownloader.Cancellation.psm1') -Force
function Assert([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Expect-Refusal([scriptblock]$Action,[string]$Message) {
    $refused=$false
    try { & $Action | Out-Null } catch { $refused=$true }
    Assert $refused $Message
}
$dir=Join-Path ([IO.Path]::GetTempPath()) ('vd-engine-review-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dir)
$active=[Collections.Generic.List[Diagnostics.Process]]::new()
$links=[Collections.Generic.List[string]]::new()
try {
    $fixture=Join-Path $dir 'engine'
    [void][IO.Directory]::CreateDirectory($fixture)
    Copy-Item (Join-Path $root 'ytdl-manager-v8.ps1') $fixture
    Copy-Item (Join-Path $root 'lib') (Join-Path $fixture 'lib') -Recurse
    $fake=Join-Path $fixture 'native-fixture.ps1'
    [IO.File]::WriteAllText($fake,@'
$ErrorActionPreference='Stop'
$nativeArguments=@($args)
$state=$env:VD_REVIEW_STATE
[IO.File]::AppendAllText((Join-Path $state 'native-arguments.jsonl'),(ConvertTo-Json -InputObject $nativeArguments -Compress)+"`n")
if ($nativeArguments -contains '--skip-download') {
    [IO.File]::WriteAllText((Join-Path $state 'metadata-started'),'started')
    if ($env:VD_REVIEW_SCENARIO -eq 'metadata-cancel') { Start-Sleep -Seconds 30 }
    Write-Output '1048576'
    exit 0
}
if ($env:VD_REVIEW_SCENARIO -eq 'retry-cancel') {
    [Console]::Error.WriteLine('ERROR: HTTP Error 503: service unavailable')
    exit 1
}
$outputIndex=[Array]::IndexOf($nativeArguments,'-o')
if ($outputIndex -lt 0) { throw 'Engine did not supply an output template' }
$media=$nativeArguments[$outputIndex+1].Replace('%(title)s','fixture').Replace('%(id)s','id').Replace('%(ext)s','mp4')
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($media))
if ($env:VD_REVIEW_SCENARIO -eq 'no-media') {
    [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetDirectoryName($media)) 'orphan.en.srt'),'sidecar only')
    exit 0
}
if ($env:VD_REVIEW_SCENARIO -eq 'foreign-media') {
    Write-Output ('__VD_FILE__:'+ $env:VD_REVIEW_FOREIGN)
    exit 0
}
[IO.File]::WriteAllText($media,'MEDIA')
if ($env:VD_REVIEW_SCENARIO -eq 'cleanup-refusal') {
    $link=Join-Path ([IO.Path]::GetDirectoryName($media)) 'unsafe-link'
    $kind=if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $kind -Path $link -Target $env:VD_REVIEW_OUTSIDE | Out-Null
    [IO.File]::WriteAllText((Join-Path $state 'created-link'),$link)
}
Write-Output ('__VD_FILE__:'+ $media)
exit 0
'@)
    $native=Join-Path $fixture 'yt-dlp.exe'
    if ($IsWindows) {
        # .NET Framework's compiler ships with Windows; no SDK restore or network.
        # The wrapper preserves native Process start/kill behavior on Windows.
        $compiler=@('Framework64','Framework') | ForEach-Object { Join-Path $env:WINDIR "Microsoft.NET/$_/v4.0.30319/csc.exe" } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        Assert ([bool]$compiler) 'The offline Windows fixture needs the .NET Framework C# compiler'
        $source=Join-Path $fixture 'NativeFixture.cs'
        [IO.File]::WriteAllText($source,@'
using System;
using System.Diagnostics;
using System.Text;
class NativeFixture {
    static string Quote(string value) {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value) {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); }
            else { result.Append('\\', slashes); result.Append(c); }
            slashes = 0;
        }
        result.Append('\\', slashes * 2); result.Append('"');
        return result.ToString();
    }
    static int Main(string[] args) {
        var arguments = new StringBuilder("-NoLogo -NoProfile -File ");
        arguments.Append(Quote(Environment.GetEnvironmentVariable("VD_REVIEW_FAKE")));
        foreach (string argument in args) arguments.Append(" ").Append(Quote(argument));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("VD_REVIEW_PWSH"), arguments.ToString());
        start.UseShellExecute = false;
        using (var process = Process.Start(start)) { process.WaitForExit(); return process.ExitCode; }
    }
}
'@)
        $compilerOutput=& $compiler /nologo "/out:$native" $source 2>&1
        Assert ($LASTEXITCODE -eq 0) "Native stand-in compilation failed: $compilerOutput"
    } else {
        [IO.File]::WriteAllText($native,"#!/bin/sh`nexec `"`$VD_REVIEW_PWSH`" -NoLogo -NoProfile -File `"`$VD_REVIEW_FAKE`" `"`$@`"`n")
        & chmod +x $native
        Assert ($LASTEXITCODE -eq 0) 'Cannot make the native stand-in executable'
    }

    function Read-ReviewEvents([string]$Path) {
        if (-not (Test-Path -LiteralPath $Path)) { return }
        foreach ($line in @(Get-Content -LiteralPath $Path)) {
            if (-not $line.Trim()) { continue }
            try { $line | ConvertFrom-Json } catch { } # Active writer can have an incomplete last line.
        }
    }
    function Start-ReviewEngine([string]$Name,[string]$Scenario,[string[]]$Extra=@(),[bool]$Cancellable=$true,[int]$Retries=0) {
        $state=Join-Path $dir $Name
        [void][IO.Directory]::CreateDirectory($state)
        $output=Join-Path $state 'output'
        [void][IO.Directory]::CreateDirectory($output)
        $foreign=Join-Path $output 'foreign.mp4'
        [IO.File]::WriteAllText($foreign,'KEEP FOREIGN FILE')
        $queue=Join-Path $state 'queue.txt'
        $url="https://example.org/$Name"
        [IO.File]::WriteAllText($queue,$url+"`n")
        $events=Join-Path $state 'events.jsonl'
        $cancel=Join-Path $state 'signals'
        $profile=Join-Path $state 'profile'
        $outside=Join-Path $state 'outside'
        [void][IO.Directory]::CreateDirectory($outside)
        [IO.File]::WriteAllText((Join-Path $outside 'sentinel'),'KEEP OUTSIDE')
        $start=[Diagnostics.ProcessStartInfo]::new($pwsh)
        $start.UseShellExecute=$false
        $start.RedirectStandardOutput=$true
        $start.RedirectStandardError=$true
        $start.Environment['LOCALAPPDATA']=$profile
        $start.Environment['VD_REVIEW_PWSH']=$pwsh
        $start.Environment['VD_REVIEW_FAKE']=$fake
        $start.Environment['VD_REVIEW_STATE']=$state
        $start.Environment['VD_REVIEW_SCENARIO']=$Scenario
        $start.Environment['VD_REVIEW_FOREIGN']=$foreign
        $start.Environment['VD_REVIEW_OUTSIDE']=$outside
        $arguments=@('-NoLogo','-NoProfile','-File',(Join-Path $fixture 'ytdl-manager-v8.ps1'),'-In',$queue,'-Out',$output,'-Threads','1','-NoProgress','-Retries',[string]$Retries,'-EventFile',$events)
        if ($Cancellable) { $arguments+=@('-CancelDir',$cancel) }
        foreach ($argument in @($arguments+$Extra)) { [void]$start.ArgumentList.Add($argument) }
        $process=[Diagnostics.Process]::Start($start)
        $active.Add($process)
        return [pscustomobject]@{ Process=$process; Stdout=$process.StandardOutput.ReadToEndAsync(); Stderr=$process.StandardError.ReadToEndAsync(); State=$state; Output=$output; Foreign=$foreign; Queue=$queue; Url=$url; Events=$events; Cancel=$cancel; Profile=$profile; Outside=$outside }
    }
    function Finish-ReviewEngine($Run,[int]$TimeoutSeconds=15) {
        if (-not $Run.Process.WaitForExit($TimeoutSeconds*1000)) { throw "Engine timed out in $($Run.State)" }
        $messages=@(Read-ReviewEvents $Run.Events)
        $results=@($messages | Where-Object Kind -eq Result)
        return [pscustomobject]@{ ExitCode=$Run.Process.ExitCode; Messages=$messages; Results=$results; Diagnostic=($Run.Stdout.GetAwaiter().GetResult()+$Run.Stderr.GetAwaiter().GetResult()) }
    }
    function Assert-NoSuccess($Run,$Outcome) {
        Assert ($Outcome.ExitCode -ne 0) "Invalid media must fail the CLI: $($Outcome.Diagnostic)"
        Assert ($Outcome.Results.Count -eq 1) 'Engine must emit exactly one terminal Result'
        Assert (-not $Outcome.Results[0].Success) 'Invalid media must not report success'
        Assert (@($Outcome.Messages | Where-Object EventType -eq Done).Count -eq 0) 'Invalid media must not emit Done'
        Assert (@(Get-Content -LiteralPath $Run.Queue | Where-Object { $_ }).Count -eq 1) 'Failed URL must remain queued for a later CLI retry'
        Assert (-not (Test-Path -LiteralPath (Join-Path $Run.State 'queue-success.txt'))) 'Failed URL must not enter the current run success file'
        Assert (-not (Test-Path -LiteralPath (Join-Path $Run.Profile 'VideoDownloader/successful-downloads.jsonl'))) 'Failed URL must not enter successful history'
        Assert (([IO.File]::ReadAllText($Run.Foreign)) -ceq 'KEEP FOREIGN FILE') 'Unrelated existing output must remain unchanged'
    }

    # A zero exit alone, sidecars, or a foreign after_move path do not prove a download.
    foreach ($cancellable in @($false,$true)) {
        foreach ($scenario in @('no-media','foreign-media')) {
            if (-not $cancellable -and $scenario -eq 'foreign-media') { continue }
            $run=Start-ReviewEngine "$scenario-$cancellable" $scenario -Cancellable $cancellable
            $outcome=Finish-ReviewEngine $run
            Assert-NoSuccess $run $outcome
            if ($cancellable) { Assert (@(Get-ChildItem -LiteralPath $run.Output -File -Recurse).Count -eq 1) 'No-media staged run must not publish sidecars' }
        }
    }

    foreach ($template in @('../escaped.%(ext)s','..\escaped.%(ext)s','.. /escaped.%(ext)s','.. ./escaped.%(ext)s','/tmp/escaped.%(ext)s','C:\escaped.%(ext)s','C:escaped.%(ext)s')) {
        $run=Start-ReviewEngine ('invalid-'+[guid]::NewGuid().ToString('N')) 'media' -Extra @('-FilenameTemplate',$template)
        $outcome=Finish-ReviewEngine $run
        Assert ($outcome.ExitCode -ne 0) "Unsafe filename template must fail: $template"
        Assert (-not (Test-Path -LiteralPath (Join-Path $run.State 'native-arguments.jsonl'))) "Unsafe template reached a native process: $template"
        Assert (([IO.File]::ReadAllText($run.Foreign)) -ceq 'KEEP FOREIGN FILE') 'Template validation must preserve existing files'
    }

    $run=Start-ReviewEngine 'nested-valid' 'media' -Extra @('-FilenameTemplate','nested/%(title)s.%(ext)s','-AutoFragments')
    $outcome=Finish-ReviewEngine $run
    Assert ($outcome.ExitCode -eq 0 -and $outcome.Results.Count -eq 1 -and $outcome.Results[0].Success) "Nested relative filename must work: $($outcome.Diagnostic)"
    $result=$outcome.Results[0]
    Assert ($result.Path -eq (Join-Path $run.Output 'nested/fixture.mp4') -and (Test-Path -LiteralPath $result.Path)) 'Nested media must be published to the correct output path'
    Assert ($result.OutputRoot -eq $run.Output -and -not $result.StageDir -and -not $result.CleanupError) 'Successful Result must expose output root and no cleanup residue'
    Assert (@($result.OwnedPaths).Count -eq 1) 'Successful media must report its owned output path'
    $calls=@(Get-Content -LiteralPath (Join-Path $run.State 'native-arguments.jsonl') | ForEach-Object { ,@($_ | ConvertFrom-Json) })
    Assert ($calls.Count -eq 2) 'AutoFragments must invoke metadata and download processes'
    foreach ($call in $calls) { Assert ($call -contains '--ignore-config') 'Metadata and download processes must both ignore external yt-dlp configuration' }

    # Mark cancellation only after the actual native metadata process is alive.
    $run=Start-ReviewEngine 'metadata-cancel' 'metadata-cancel' -Extra @('-AutoFragments')
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath (Join-Path $run.State 'metadata-started')) -and -not $run.Process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 25 }
    Assert (Test-Path -LiteralPath (Join-Path $run.State 'metadata-started')) 'Native metadata process never began'
    $timer=[Diagnostics.Stopwatch]::StartNew()
    Request-VdCancellation -Directory $run.Cancel -Url $run.Url
    $outcome=Finish-ReviewEngine $run 3
    $timer.Stop()
    Assert ($timer.ElapsedMilliseconds -lt 2000) "Metadata cancellation exceeded 2 seconds: $($timer.ElapsedMilliseconds) ms"
    Assert ($outcome.Results.Count -eq 1 -and $outcome.Results[0].Cancelled -and -not $outcome.Results[0].Success) 'Metadata cancellation must produce a cancelled terminal Result'
    Assert (@(Get-Content -LiteralPath (Join-Path $run.State 'native-arguments.jsonl')).Count -eq 1) 'Cancelled metadata must not start media download'

    $run=Start-ReviewEngine 'retry-cancel' 'retry-cancel' -Retries 1 -Extra @('-RetryDelaySeconds','60')
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    while (@(Read-ReviewEvents $run.Events | Where-Object EventType -eq Retry).Count -eq 0 -and -not $run.Process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 25 }
    Assert (@(Read-ReviewEvents $run.Events | Where-Object EventType -eq Retry).Count -eq 1) 'Engine never entered retry backoff'
    $timer=[Diagnostics.Stopwatch]::StartNew()
    Request-VdCancellation -Directory $run.Cancel -Url $run.Url
    $outcome=Finish-ReviewEngine $run 3
    $timer.Stop()
    Assert ($timer.ElapsedMilliseconds -lt 2000) "Retry-backoff cancellation exceeded 2 seconds: $($timer.ElapsedMilliseconds) ms"
    Assert ($outcome.Results.Count -eq 1 -and $outcome.Results[0].Cancelled -and -not $outcome.Results[0].Success) 'Retry cancellation must produce a cancelled terminal Result'
    Assert (@(Get-Content -LiteralPath (Join-Path $run.State 'native-arguments.jsonl')).Count -eq 1) 'Cancelled backoff must not start another attempt'

    # A refused staging cleanup must be visible in Result and CLI exit status.
    $run=Start-ReviewEngine 'cleanup-refusal' 'cleanup-refusal'
    $outcome=Finish-ReviewEngine $run
    $linkRecord=Join-Path $run.State 'created-link'
    if (Test-Path -LiteralPath $linkRecord) { $links.Add([IO.File]::ReadAllText($linkRecord)) }
    Assert-NoSuccess $run $outcome
    $result=$outcome.Results[0]
    Assert ([bool]$result.CleanupError) 'Refused cleanup must expose a nonempty CleanupError'
    Assert ($result.StageDir -and (Test-Path -LiteralPath $result.StageDir)) 'Failed staging cleanup must preserve and identify the staging directory'
    Assert ($result.OutputRoot -eq $run.Output -and $null -ne $result.PSObject.Properties['CleanupPaths']) 'Cleanup failure Result must expose OutputRoot and CleanupPaths'
    Assert (([IO.File]::ReadAllText((Join-Path $run.Outside 'sentinel'))) -ceq 'KEEP OUTSIDE') 'Cleanup must never traverse a staging link'

    # Exercise the real exported cleanup helper independently of engine outcome.
    $runRoot=Join-Path $dir 'helper-run'
    $stage=Join-Path $runRoot ('download-1-'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory((Join-Path $stage 'nested'))
    [IO.File]::WriteAllText((Join-Path $stage 'nested/file.part'),'partial')
    Remove-VdStagingDirectory -StageDir $stage -RunDir $runRoot
    Assert (-not (Test-Path -LiteralPath $stage)) 'Safe helper must remove an ordinary staged directory'
    $outsideStage=Join-Path $run.Outside ('download-99-'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($outsideStage)
    [IO.File]::WriteAllText((Join-Path $outsideStage 'sentinel'),'KEEP OUTSIDE STAGE')
    Expect-Refusal { Remove-VdStagingDirectory -StageDir $outsideStage -RunDir $runRoot } 'Safe helper must refuse staging outside the run root, even with a valid staging basename'
    Assert (([IO.File]::ReadAllText((Join-Path $outsideStage 'sentinel'))) -ceq 'KEEP OUTSIDE STAGE') 'Safe helper deleted an outside stage with a valid basename'
    $stage=Join-Path $runRoot ('download-2-'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    $link=Join-Path $stage 'descendant'
    $kind=if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $kind -Path $link -Target $run.Outside | Out-Null
    $links.Add($link)
    Expect-Refusal { Remove-VdStagingDirectory -StageDir $stage -RunDir $runRoot } 'Safe helper must refuse any descendant staging symlink or junction'
    Assert (([IO.File]::ReadAllText((Join-Path $run.Outside 'sentinel'))) -ceq 'KEEP OUTSIDE') 'Safe helper changed an outside sentinel'

    # A publish can move files before a later failure; failed rollback must
    # preserve those real paths so the caller can retry cleanup safely.
    # Inject only into an isolated module copy, leaving the real helper intact.
    $rollbackStage=Join-Path $dir 'rollback-stage'
    $rollbackOutput=Join-Path $dir 'rollback-output'
    [void][IO.Directory]::CreateDirectory($rollbackStage)
    $rollbackSource=Join-Path $rollbackStage 'payload.mp4'
    $rollbackPublished=Join-Path $rollbackOutput 'payload.mp4'
    [IO.File]::WriteAllText($rollbackSource,'OWNED PUBLISHED FILE')
    $rollbackModulePath=Join-Path $dir 'RollbackFixture.psm1'
    Copy-Item (Join-Path $root 'lib/VideoDownloader.Cancellation.psm1') $rollbackModulePath
    $rollbackModule=$null
    try {
        $rollbackModule=Import-Module $rollbackModulePath -Prefix ReviewRollback -PassThru
        & $rollbackModule {
            function script:Remove-VdOwnedFiles {
                param([string]$OutputRoot,[string[]]$Paths)
                throw 'Injected rollback removal failure'
            }
        }
        $ownershipFailure=$null
        try {
            Publish-ReviewRollbackVdStagedFiles -StageDir $rollbackStage -OutputRoot $rollbackOutput -MediaPath (Join-Path $rollbackStage 'missing.mp4') | Out-Null
        } catch { $ownershipFailure=$_.Exception }
        Assert ($ownershipFailure -is [IO.IOException]) 'Failed publish rollback must report an IOException'
        Assert ($ownershipFailure.Message -match 'Injected rollback removal failure') 'Rollback fixture must reach the failing removal operation'
        $retainedPaths=@($ownershipFailure.Data['OwnedPaths'])
        Assert ($retainedPaths.Count -eq 1 -and $retainedPaths[0] -eq $rollbackPublished) 'Failed rollback must retain the exact published path in exception ownership data'
        Assert (-not (Test-Path -LiteralPath $rollbackSource) -and (Test-Path -LiteralPath $retainedPaths[0])) 'Ownership must describe a file actually moved into output before rollback failed'
        Assert (([IO.File]::ReadAllText($retainedPaths[0])) -ceq 'OWNED PUBLISHED FILE') 'Failed rollback must leave the owned file available for a cleanup retry'
        Assert (([IO.File]::ReadAllText((Join-Path $run.Outside 'sentinel'))) -ceq 'KEEP OUTSIDE') 'Failed publish rollback changed an outside sentinel'
    } finally {
        if ($rollbackModule) { Remove-Module $rollbackModule }
        [void](Remove-VdOwnedFiles -OutputRoot $rollbackOutput -Paths @($rollbackPublished))
    }
    Assert (-not (Test-Path -LiteralPath $rollbackPublished)) 'The real cleanup helper must remove the retained owned file on retry'
    Assert (([IO.File]::ReadAllText((Join-Path $run.Outside 'sentinel'))) -ceq 'KEEP OUTSIDE') 'Cleanup retry changed an outside sentinel'
} finally {
    foreach ($process in $active) {
        try { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() } } catch { }
        $process.Dispose()
    }
    foreach ($link in $links) { try { [IO.Directory]::Delete($link) } catch { } }
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Offline engine review regression tests passed.'
