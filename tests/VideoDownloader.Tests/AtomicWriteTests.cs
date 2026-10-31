using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class AtomicWriteTests
{
    private const string OldJson="{\"version\":1,\"title\":\"Прежний снимок\",\"urls\":[\"https://atomic.invalid/old\"]}";
    private const string NewJson="{\"version\":1,\"title\":\"Новый снимок 🎬\",\"urls\":[\"https://atomic.invalid/old\",\"https://atomic.invalid/new\"]}";

    internal static Task RunAsync()
    {
        if(!OperatingSystem.IsWindows())return Task.CompletedTask;
        string root=Path.Combine(Path.GetTempPath(),"vd-atomic-write-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TemporaryReader(Path.Combine(root,"temporary.json"));
            PersistentReader(Path.Combine(root,"persistent.json"));
            ReadOnlyTarget(Path.Combine(root,"readonly.json"));
            Console.WriteLine("PASS: atomic checkpoint replacement recovers after a temporary Windows reader and preserves locked/read-only targets on bounded failure");
        }
        finally { Directory.Delete(root,true); }
        return Task.CompletedTask;
    }

    private static void TemporaryReader(string path)
    {
        AppPersistence.AtomicWrite(path,OldJson);
        var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var start=new ManualResetEventSlim();
        using var releaserReady=new ManualResetEventSlim();
        Exception? releaseError=null;
        // A dedicated OS thread releases the Windows handle even while the
        // caller is inside a synchronous replacement/retry. A captured WPF
        // context or a busy thread pool must not delay the release indefinitely.
        var releaser=new Thread(()=>
        {
            try
            {
                releaserReady.Set();
                if(!start.Wait(TimeSpan.FromSeconds(2)))throw new TimeoutException("The temporary-reader test did not start its writer.");
                Thread.Sleep(80);
                held.Dispose();
            }
            catch(Exception error) { releaseError=error; }
        }){IsBackground=true};
        releaser.Start();
        try
        {
            Require(releaserReady.Wait(TimeSpan.FromSeconds(2)),
                "The dedicated handle releaser must be ready before the writer starts");
            start.Set();
            AppPersistence.AtomicWrite(path,NewJson);
        }
        finally
        {
            start.Set();
            bool joined=releaser.Join(TimeSpan.FromSeconds(2));
            held.Dispose();
            Require(joined,"The dedicated temporary-reader releaser must terminate promptly");
        }
        if(releaseError!=null)throw new InvalidOperationException("The temporary-reader releaser failed.",releaseError);
        Require(File.ReadAllText(path,Encoding.UTF8)==NewJson,
            "A temporary reader denying Delete sharing must not permanently prevent the next complete checkpoint");
        using var json=JsonDocument.Parse(File.ReadAllText(path,Encoding.UTF8));
        Require(json.RootElement.GetProperty("urls").GetArrayLength()==2,
            "A recovered replacement must publish the complete new JSON document");
        RequireNoTemporaryFiles(path);
    }

    private static void PersistentReader(string path)
    {
        AppPersistence.AtomicWrite(path,OldJson);
        byte[] previous=File.ReadAllBytes(path);
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var elapsed=Stopwatch.StartNew();
            Exception error=ReplacementFailure(()=>AppPersistence.AtomicWrite(path,NewJson));
            elapsed.Stop();
            RequireWindowsReplacementError(error);
            Require(elapsed.Elapsed<TimeSpan.FromSeconds(2),
                "A persistent reader must cause a bounded failure rather than an indefinitely blocked checkpoint write");
            Require(File.ReadAllBytes(path).SequenceEqual(previous),
                "A failed replacement must leave the previous checkpoint byte-for-byte intact");
            RequireNoTemporaryFiles(path);
        }
        AppPersistence.AtomicWrite(path,NewJson);
        Require(File.ReadAllText(path,Encoding.UTF8)==NewJson,
            "The persistence helper must remain usable after a previously blocked reader releases its handle");
        RequireNoTemporaryFiles(path);
    }

    private static void ReadOnlyTarget(string path)
    {
        AppPersistence.AtomicWrite(path,OldJson);
        byte[] previous=File.ReadAllBytes(path);
        FileAttributes attributes=File.GetAttributes(path);
        File.SetAttributes(path,attributes|FileAttributes.ReadOnly);
        try
        {
            var elapsed=Stopwatch.StartNew();
            Exception error=ReplacementFailure(()=>AppPersistence.AtomicWrite(path,NewJson));
            elapsed.Stop();
            RequireWindowsReplacementError(error);
            Require(elapsed.Elapsed<TimeSpan.FromSeconds(2),
                "A permanently read-only destination must fail within the bounded retry period");
            Require((File.GetAttributes(path)&FileAttributes.ReadOnly)!=0 &&
                    File.ReadAllBytes(path).SequenceEqual(previous),
                "Replacing a read-only checkpoint must neither clear its protection nor change its previous bytes");
            RequireNoTemporaryFiles(path);
        }
        finally { File.SetAttributes(path,attributes); }
    }

    private static Exception ReplacementFailure(Action action)
    {
        try { action(); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { return error; }
        throw new InvalidOperationException("Atomic-write Windows test failed: an exclusively protected target was unexpectedly replaced.");
    }

    private static void RequireWindowsReplacementError(Exception error)=>
        Require((error.HResult&0xffff) is 5 or 32 or 33,
            "The original Windows access/sharing/lock failure and its HRESULT must remain available");

    private static void RequireNoTemporaryFiles(string path)=>
        Require(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+".*.tmp").Any(),
            "Each completed or failed atomic replacement must clean its own temporary file");

    private static void Require(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("Atomic-write Windows test failed: "+message);
    }
}
