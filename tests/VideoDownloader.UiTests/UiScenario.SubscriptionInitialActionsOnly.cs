using System.IO;
using System.Windows;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    // The same real scenario runs first as a short CI probe, then again in the
    // complete host. This selector exists only in the test executable.
    internal static void RunSubscriptionInitialActionsOnly()
    {
        static void Stage(string value)
        {
            string? path=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(path))File.WriteAllText(path,value);
        }
        string root=Path.Combine(Path.GetTempPath(),"vd-initial-actions-ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        try
        {
            using var offers=new SubscriptionQueueOfferResponder();
            Stage("focused subscription initial actions: real Add/Check, Cancel/YesAll and native notice");
            TestSubscriptionInitialActions(root);
        }
        finally
        {
            app.Shutdown();
            try{Directory.Delete(root,true);}catch(IOException){}
        }
        // A successful scene must also finish fixture/dispatcher cleanup.
        Stage("PASS");
    }
}
