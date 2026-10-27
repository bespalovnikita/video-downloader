using VideoDownloader.Native;

internal static class NativeIpcFixture
{
    internal static bool TryRunFixture(string[] args)
    {
        if(args.FirstOrDefault()!="--native-ipc-forward")return false;
        if(args.Length<2)throw new ArgumentException("An isolated pipe name is required.");
        VideoDownloader.Native.Program.ForwardArguments(args.Skip(2).ToArray(),args[1]);
        Console.WriteLine("FORWARDED:"+Environment.ProcessId);
        return true;
    }
}
