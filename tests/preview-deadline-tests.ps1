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
  if(image.StartsWith("{")){Console.WriteLine(image);return 0;}
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
if($image.StartsWith('{')){Write-Output $image;exit 0}
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
    Assert (@($next).Count -eq 1) 'Metadata fetch must return exactly one preview record without leaking the child wait result'
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
 public const string Png="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII=";
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
    bool stall=request.Contains("/stall");bool large=request.Contains("/large");bool bad=request.Contains("/bad");bool unsupported=request.Contains("/unsupported");bool webp=request.Contains("/real-webp");
    byte[] image=webp?Convert.FromBase64String("UklGRiQAAABXRUJQVlA4IBgAAAAwAQCdASoCAAIAAgA0JaQAA3AA/vuUAAA="):unsupported?Encoding.ASCII.GetBytes("unsupported image"):Convert.FromBase64String(Png);
    string header="HTTP/1.1 "+(bad?"404 Not Found":"200 OK")+"\r\nContent-Type: image/png\r\nContent-Length: "+(bad?0:large?2000001:stall?100:image.Length)+"\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(header),stop.Token);
    if(stall){
     // Wait until the timed-out client closes, without blocking the next request.
     await stream.ReadAsync(buffer,stop.Token);
    } else if(!large && !bad) await stream.WriteAsync(image,stop.Token);
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
    Assert ($normal.Success -and $normal.ThumbnailBase64 -eq [VdPreviewDeadlineServer]::Png) 'A later preview must still read a complete thumbnail'
    foreach($imagePath in @('/image?id=123&format=jpeg','/normal.webp?token=123','/normal.png#signed')) {
        [IO.File]::WriteAllText($cookies,$server.BaseUrl+$imagePath)
        $image=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/suffix-independent' -CookiePath $cookies -TimeoutSeconds 5
        Assert ($image.Success -and $image.Title -eq 'Offline preview' -and $image.ThumbnailBase64 -eq [VdPreviewDeadlineServer]::Png -and -not $image.ThumbnailError) 'Thumbnail URL suffix/query must not decide whether valid image bytes can be used'
    }
    $fallbackMetadata=@{title='Fallback title';uploader='Author';duration=42;thumbnail=$server.BaseUrl+'/image?second=1';thumbnails=@(
        @{url=$server.BaseUrl+'/bad.png';width=1920;height=1080},@{url=$server.BaseUrl+'/unsupported';width=100;height=100})}
    [IO.File]::WriteAllText($cookies,($fallbackMetadata|ConvertTo-Json -Depth 5 -Compress))
    $fallback=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/fallback' -CookiePath $cookies -TimeoutSeconds 5
    Assert ($fallback.Success -and $fallback.Title -eq 'Fallback title' -and $fallback.Uploader -eq 'Author' -and $fallback.Duration -eq 42 -and $fallback.ThumbnailBase64 -eq [VdPreviewDeadlineServer]::Png -and $fallback.ThumbnailUrl -ceq $fallbackMetadata.thumbnail) 'A bad or unsupported first image must allow a later valid thumbnail without losing metadata'
    $fallbackMetadata.thumbnail=$server.BaseUrl+'/bad'
    $fallbackMetadata.thumbnails=@(@{url=$server.BaseUrl+'/unsupported'})
    [IO.File]::WriteAllText($cookies,($fallbackMetadata|ConvertTo-Json -Depth 5 -Compress))
    $noImage=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/no-image' -CookiePath $cookies -TimeoutSeconds 5
    Assert ($noImage.Success -and $noImage.Title -eq 'Fallback title' -and $noImage.Uploader -eq 'Author' -and $noImage.Duration -eq 42 -and -not $noImage.ThumbnailBase64 -and $noImage.ThumbnailError) 'Missing thumbnails must preserve successful text metadata'
    [IO.File]::WriteAllText($cookies,$server.BaseUrl+'/real-webp?token=123')
    $realConverter=Get-Command ffmpeg.exe,ffmpeg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if($realConverter) {
        $converted=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/webp' -CookiePath $cookies -TimeoutSeconds 10
        Assert ($converted.Success -and $converted.ThumbnailBase64 -and -not $converted.ThumbnailError -and $converted.ThumbnailBase64.StartsWith('iVBOR')) 'A WebP thumbnail must convert to a WPF-compatible PNG through the existing FFmpeg'
    }
    $module=Get-Module VideoDownloader.PreviewCache
    try {
        & $module {function script:Get-Command {param($Name,$CommandType,$ErrorAction)}}
        $missingConverter=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/webp-no-converter' -CookiePath $cookies -TimeoutSeconds 5
        Assert ($missingConverter.Success -and $missingConverter.Title -eq 'Offline preview' -and -not $missingConverter.ThumbnailBase64 -and $missingConverter.ThumbnailError -like '*requires FFmpeg*') 'Missing WebP conversion support must retain useful text metadata'
    } finally {& $module {Remove-Item function:script:Get-Command}}
    $converterExe=Join-Path $temp 'ffmpeg.exe'
    if($IsWindows) {
        $converterSource=Join-Path $temp 'SlowConverter.cs'
        @'
using System;
using System.IO;
using System.Threading;
class SlowConverter {
 static void Main(){
  File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("VD_PREVIEW_FIXTURE"),"converter.pid"),System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
  Thread.Sleep(Timeout.Infinite);
 }
}
'@ | Set-Content -LiteralPath $converterSource
        $result=& $compiler /nologo "/out:$converterExe" $converterSource 2>&1
        Assert ($LASTEXITCODE -eq 0) "Converter fixture compilation failed: $result"
    } else {
        @'
[IO.File]::WriteAllText((Join-Path $env:VD_PREVIEW_FIXTURE 'converter.pid'),[string]$PID)
Start-Sleep -Seconds 60
'@ | Set-Content -LiteralPath (Join-Path $temp 'converter.ps1')
        [IO.File]::WriteAllText($converterExe,"#!/bin/sh`nexec `"`$VD_PREVIEW_PWSH`" -NoProfile -File `"`$VD_PREVIEW_FIXTURE/converter.ps1`" `"`$@`"`n")
        & chmod +x $converterExe
    }
    $watch.Restart()
    $converterTimeout=Get-VdPreviewData -YtDlpPath $exe -Url 'https://example.com/webp-slow-converter' -CookiePath $cookies -TimeoutSeconds 6 -ThumbnailTimeoutSeconds 2
    Assert ($converterTimeout.Success -and $converterTimeout.Title -eq 'Offline preview' -and -not $converterTimeout.ThumbnailBase64 -and $converterTimeout.ThumbnailError -and $watch.Elapsed.TotalSeconds -lt 7) 'WebP conversion must share the bounded image deadline while preserving text metadata'
    $converterPid=[int][IO.File]::ReadAllText((Join-Path $temp 'converter.pid'))
    Assert (-not (Get-Process -Id $converterPid -ErrorAction SilentlyContinue)) 'A timed-out thumbnail converter must terminate its own process'
    $cacheDir=Join-Path $temp 'cache'
    $oldFailure=[pscustomobject]@{Title='Old title';ThumbnailBase64='';ThumbnailError='no JPEG/PNG thumbnail was provided by yt-dlp'}
    $null=Write-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/old' -Preview $oldFailure
    Assert ($null -eq (Read-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/old')) 'An old extension-filter rejection must be retried after upgrade instead of staying cached for 72 hours'
    $freshFailure=[pscustomobject]@{Title='New title';PreviewFetchVersion=2;ThumbnailBase64='';ThumbnailError='no JPEG/PNG thumbnail was provided by yt-dlp'}
    $null=Write-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/fresh' -Preview $freshFailure
    Assert ((Read-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/fresh').Title -eq 'New title') 'A fresh text-only preview must remain cacheable without a retry loop'
    $installation=Join-Path $temp 'later-installation';[void][IO.Directory]::CreateDirectory($installation)
    $futureYtDlp=Join-Path $installation 'yt-dlp.exe'
    $missingState=$missingConverter
    try {
        & $module {function script:Get-Command {param($Name,$CommandType,$ErrorAction)}}
        $null=Write-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/install-later' -Preview $missingState
        Assert (-not (Test-VdPreviewCacheRefreshNeeded $missingState -YtDlpPath $futureYtDlp) -and
            (Read-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/install-later' -YtDlpPath $futureYtDlp).Title -eq $missingState.Title) 'Missing FFmpeg must keep text metadata cached while conversion remains unavailable'
        [IO.File]::WriteAllText((Join-Path $installation 'ffmpeg.exe'),'available converter fixture')
        Assert (Test-VdPreviewCacheRefreshNeeded $missingState -YtDlpPath $futureYtDlp) 'An in-memory text-only WebP record must allow retry after FFmpeg installation'
        Assert ($null -eq (Read-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/install-later' -YtDlpPath $futureYtDlp)) 'An installed FFmpeg must invalidate the corresponding disk-cache failure before the 72-hour TTL'
        $nativeMissing=[pscustomobject]@{Title='Native title';PreviewFetchVersion=2;ThumbnailBase64='';ThumbnailError='WebP/AVIF thumbnail needs FFmpeg; text metadata is available.'}
        $null=Write-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/native-install-later' -Preview $nativeMissing
        Assert ((Test-VdPreviewCacheRefreshNeeded $nativeMissing -YtDlpPath $futureYtDlp) -and
            $null -eq (Read-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/native-install-later' -YtDlpPath $futureYtDlp)) 'Native-created text-only cache must refresh in PowerShell after installing FFmpeg'
        $recovered=[pscustomobject]@{Title='Recovered';PreviewFetchVersion=2;ThumbnailBase64=[VdPreviewDeadlineServer]::Png;ThumbnailError=''}
        $null=Write-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/install-later' -Preview $recovered
        Assert (-not (Test-VdPreviewCacheRefreshNeeded $recovered -YtDlpPath $futureYtDlp) -and
            (Read-VdPreviewCache -CacheDir $cacheDir -Url 'https://example.com/install-later' -YtDlpPath $futureYtDlp).ThumbnailBase64) 'A completed thumbnail must remain cached after the targeted retry'
    } finally {& $module {Remove-Item function:script:Get-Command}}
    Write-Host 'PowerShell preview child/body deadlines and bounded thumbnails: PASS'
} finally {
    if($server){$server.Dispose()}
    $env:VD_PREVIEW_FIXTURE=$previous;$env:VD_PREVIEW_PWSH=$oldPwsh
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
