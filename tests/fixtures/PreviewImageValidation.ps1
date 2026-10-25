# Production uses WPF's real decoder. Linux test hosts cannot load PresentationCore;
# substitute only that private decoder with a real FFmpeg decode, never a signature mock.
function Enable-VdTestPreviewImageValidation {
    param([Parameter(Mandatory=$true)][object]$Module)
    if($IsWindows){return}
    $converter=Get-Command ffmpeg -CommandType Application -ErrorAction Stop | Select-Object -First 1
    & $Module {
        param($converterPath)
        $script:vdTestImageDecoder=$converterPath
        function script:Test-VdPreviewImageBytes {
            param([byte[]]$Bytes,[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None)
            if($Bytes.Length -gt 2000000 -or -not (Test-VdThumbnailBytes $Bytes)){return $false}
            $CancellationToken.ThrowIfCancellationRequested()
            $process=[Diagnostics.Process]::new()
            try {
                $start=[Diagnostics.ProcessStartInfo]::new($script:vdTestImageDecoder)
                $start.UseShellExecute=$false;$start.CreateNoWindow=$true
                $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
                foreach($argument in @('-hide_banner','-v','error','-xerror','-nostdin','-max_alloc','67108864','-max_pixels','16777216',
                    '-protocol_whitelist','pipe','-i','pipe:0','-frames:v','1','-threads','1','-vf','scale=720:720:force_original_aspect_ratio=decrease','-f','null','-')){
                    [void]$start.ArgumentList.Add($argument)
                }
                $process.StartInfo=$start
                if(-not $process.Start()){return $false}
                $stdout=$process.StandardOutput.ReadToEndAsync($CancellationToken)
                $stderr=$process.StandardError.ReadToEndAsync($CancellationToken)
                $process.StandardInput.BaseStream.Write($Bytes,0,$Bytes.Length)
                $process.StandardInput.Close()
                [void]$process.WaitForExitAsync($CancellationToken).GetAwaiter().GetResult()
                $null=$stdout.GetAwaiter().GetResult();$null=$stderr.GetAwaiter().GetResult()
                return $process.ExitCode -eq 0
            } catch [OperationCanceledException] {throw}
              catch {return $false}
            finally {
                try{if(-not $process.HasExited){$process.Kill($true);[void]$process.WaitForExit(2000)}}catch{}
                $process.Dispose()
            }
        }
    } ([string]$converter.Source)
}
