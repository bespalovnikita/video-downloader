using System.Text.Json;
using VideoDownloader.Native;

internal static class TitleDisplaySettingsTests
{
    internal static void Run()
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Title display settings: "+message);
        }
        Require(new DownloadOptions().ShowVideoTitles,"New profiles should show readable titles.");
        var legacy=JsonSerializer.Deserialize<DownloadOptions>("{\"Quality\":\"720\"}",AppPersistence.Json)!;
        Require(!legacy.ShowVideoTitles && legacy.Quality=="720","Missing old setting must retain URL display.");
        foreach(bool enabled in new[]{false,true})
        {
            var selected=new DownloadOptions{ShowVideoTitles=enabled};
            var restored=JsonSerializer.Deserialize<DownloadOptions>(JsonSerializer.Serialize(selected,AppPersistence.Json),AppPersistence.Json)!;
            Require(restored.ShowVideoTitles==enabled && restored.Snapshot().ShowVideoTitles==enabled,
                "An explicit user choice must survive saving, restoring and snapshotting.");
            var oldCasing=JsonSerializer.Deserialize<DownloadOptions>("{\"showVideoTitles\":"+(enabled?"true":"false")+"}",AppPersistence.Json)!;
            Require(oldCasing.ShowVideoTitles==enabled,"Legacy case-insensitive settings must retain an explicit choice.");
        }
        var fresh=JsonSerializer.Deserialize<DownloadOptions>(JsonSerializer.Serialize(new DownloadOptions(),AppPersistence.Json),AppPersistence.Json)!;
        Require(fresh.ShowVideoTitles,"A newly saved default must not be treated as an old missing setting.");
        Console.WriteLine("PASS: new title defaults and compatible saved display settings");
    }
}
