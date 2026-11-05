using System.Runtime.ExceptionServices;

namespace VideoDownloader.Native;

internal sealed class ProcessOutputLimitException(string stream,int limit)
    : IOException($"Preview metadata {stream} exceeds {limit} bytes.")
{
    internal string StreamName { get; }=stream;
    internal int ByteLimit { get; }=limit;
}

internal sealed record CapturedProcessOutput(byte[] StandardOutput,byte[] StandardError);

/// <summary>Limits raw pipe bytes before accumulating or decoding child output.</summary>
internal static class BoundedProcessOutput
{
    internal static async Task<CapturedProcessOutput> ReadAsync(ManagedChildProcess child,
        int stdoutLimit,int stderrLimit,CancellationToken cancellationToken)
    {
        if(stdoutLimit<=0)throw new ArgumentOutOfRangeException(nameof(stdoutLimit));
        if(stderrLimit<=0)throw new ArgumentOutOfRangeException(nameof(stderrLimit));
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var kill=stop.Token.Register(child.Terminate);
        Exception? readError=null;

        async Task<byte[]> ReadPipe(Stream stream,int limit,string name)
        {
            using var retained=new MemoryStream();
            byte[] chunk=new byte[8192];
            try
            {
                while(true)
                {
                    // The extra byte detects overflow without allocating or
                    // decoding an unbounded line, including output with no LF.
                    int wanted=(int)Math.Min(chunk.Length,limit-retained.Length+1);
                    int count=await stream.ReadAsync(chunk.AsMemory(0,wanted),stop.Token).ConfigureAwait(false);
                    if(count==0)return retained.ToArray();
                    if(retained.Length+count>limit)throw new ProcessOutputLimitException(name,limit);
                    retained.Write(chunk,0,count);
                }
            }
            catch(Exception error)
            {
                Interlocked.CompareExchange(ref readError,error,null);
                stop.Cancel();
                throw;
            }
        }

        var stdout=ReadPipe(child.StandardOutput.BaseStream,stdoutLimit,"stdout");
        var stderr=ReadPipe(child.StandardError.BaseStream,stderrLimit,"stderr");
        var exited=child.Process.WaitForExitAsync(stop.Token);
        try
        {
            await Task.WhenAll(stdout,stderr,exited).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(stdout.Result,stderr.Result);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The sibling reader observes cancellation after a limit failure.
            // Retain the original bounded-reader diagnostic instead of turning
            // an oversized response into a misleading request timeout.
            if(Volatile.Read(ref readError) is {} error)ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
        finally
        {
            stop.Cancel();
            child.Terminate();
            await child.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(stdout,stderr).ConfigureAwait(false); } catch { }
        }
    }
}
