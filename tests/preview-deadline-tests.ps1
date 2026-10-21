#requires -Version 7.4
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'lib/VideoDownloader.PreviewCache.psm1') -Force
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('vd-preview-deadlines-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$previous=$env:VD_PREVIEW_FIXTURE
$oldPwsh=$env:VD_PREVIEW_PWSH
$server=$null
try {
    $env:VD_PREVIEW_FIXTURE=$temp
    $exe=Join-Path $temp 'yt-dlp.exe'
    if($IsWindows){
        $compiler=@('Framework64','Framework') | ForEach-Object {Join-Path $env:WINDIR "Microsoft.NET/$_/v4.0.30319/csc.exe"} | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
        Assert ([bool]$compiler) 'Windows fixture compiler is unavailable'
        $source=Join-Path $temp 'Fixture.cs'
        @'
using System;
using System.IO;
using System.Linq;
using System.Threading;
class Fixture {
 static int Main(string[] args) {
  if(!args.Contains("--ignore-config")) return 2;
  if(args.Last().EndsWith("hang")) {
   File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("VD_PREVIEW_FIXTURE"),"pid"),System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
   Thread.Sleep(Timeout.Infinite);
  }
  int index=Array.IndexOf(args,"--cookies");
  string image=index>=0 ? File.ReadAllText(args[index+1]) : "";
  Console.WriteLine("{\"title\":\"Offline preview\",\"thumbnail\":\""+image+"\"}");
  return 0;
 }
}
'@ | Set-Content -LiteralPath $source
        $result=& $compiler /nologo "/out:$exe" $source 2>&1
        Assert ($LASTEXITCODE -eq 0) "Fixture compilation failed: $result"
    } else {
        $env:VD_PREVIEW_PWSH=(Get-Process -Id $PID).Path
        @'
$ErrorActionPreference='Stop'
if($args -notcontains '--ignore-config'){exit 2}
if($args[-1].EndsWith('hang')){
 [IO.File]::WriteAllText((Join-Path $env:VD_PREVIEW_FIXTURE 'pid'),[string]$PID)
 Start-Sleep -Seconds 60
}
$index=[Array]::IndexOf($args,'--cookies')
$image=if($index -ge 0){[IO.File]::ReadAllText($args[$index+1])}else{''}
@{title='Offline preview';thumbnail=$image}|ConvertTo-Json -Compress
'@ | Set-Content -LiteralPath (Join-Path $temp 'fixture.ps1')
        [IO.File]::WriteAllText($exe,"#!/bin/sh`nexec `"`$VD_PREVIEW_PWSH`" -NoProfile -File `"`$VD_PREVIEW_FIXTURE/fixture.ps1`" `"`$@`"`n")
        & chmod +x $exe
    }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $hung=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/hang' -TimeoutSeconds 2
    Assert (-not $hung.Success -and $watch.Elapsed.TotalSeconds -lt 8) 'A hung metadata child must time out'
    $childPid=[int][IO.File]::ReadAllText((Join-Path $temp 'pid'))
    Assert (-not (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) 'Timeout must terminate its metadata process'
    $next=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/next' -TimeoutSeconds 5
    Assert ($next.Success -and $next.Title -eq 'Offline preview') 'The worker must fetch another URL after a timed-out process'

    Add-Type @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public sealed class VdPreviewDeadlineServer : IDisposable {
 readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);
 readonly CancellationTokenSource stop=new CancellationTokenSource();
 readonly Task worker;
 public string BaseUrl {get;private set;}
 public VdPreviewDeadlineServer(){
  listener.Start();BaseUrl="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port;
  worker=Task.Run(async()=>{while(!stop.IsCancellationRequested){
   using(var client=await listener.AcceptTcpClientAsync(stop.Token)){
    var stream=client.GetStream();var buffer=new byte[4096];
    int count=await stream.ReadAsync(buffer,stop.Token);
    string request=Encoding.ASCII.GetString(buffer,0,count);
    bool stall=request.Contains("/stall");bool large=request.Contains("/large");
    string header="HTTP/1.1 200 OK\r\nContent-Length: "+(large?2000001:stall?100:3)+"\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(header),stop.Token);
    if(stall){
     // Wait until the timed-out client closes, without blocking the next request.
     await stream.ReadAsync(buffer,stop.Token);
    } else if(!large) await stream.WriteAsync(new byte[]{1,2,3},stop.Token);
   }
  }});
 }
 public void Dispose(){stop.Cancel();listener.Stop();try{worker.GetAwaiter().GetResult();}catch(OperationCanceledException){}stop.Dispose();}
}
'@
    $server=[VdPreviewDeadlineServer]::new()
    $cookies=Join-Path $temp 'cookies with spaces.txt'
    [IO.File]::WriteAllText($cookies,$server.BaseUrl+'/stall.png')
    $watch.Restart()
    $stalled=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/stall' -CookiePath $cookies -TimeoutSeconds 5 -ThumbnailTimeoutSeconds 1
    Assert ($stalled.Success -and -not $stalled.ThumbnailBase64 -and $stalled.ThumbnailError -and $watch.Elapsed.TotalSeconds -lt 7) 'Image body must have a deadline while retaining metadata'
    [IO.File]::WriteAllText($cookies,$server.BaseUrl+'/large.png')
    $large=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/large' -CookiePath $cookies -TimeoutSeconds 5
    Assert ($large.Success -and $large.ThumbnailError -like '*2MB*' -and -not $large.ThumbnailBase64) 'Oversized thumbnail must not be buffered'
    [IO.File]::WriteAllText($cookies,$server.BaseUrl+'/normal.png')
    $normal=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/normal' -CookiePath $cookies -TimeoutSeconds 5
    Assert ($normal.Success -and $normal.ThumbnailBase64 -eq 'AQID') 'A later preview must still read a complete thumbnail'
    Write-Host 'PowerShell preview child/body deadlines and bounded thumbnails: PASS'
} finally {
    if($server){$server.Dispose()}
    $env:VD_PREVIEW_FIXTURE=$previous;$env:VD_PREVIEW_PWSH=$oldPwsh
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
