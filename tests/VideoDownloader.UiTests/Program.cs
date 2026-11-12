namespace VideoDownloader.UiTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length==0)UiScenario.Run();
            else if(args.SequenceEqual(new[]{"--scenario","subscription-requeue"}))UiScenario.RunSubscriptionRequeueOnly();
            else throw new ArgumentException("Unknown native UI test scenario.");
            return 0;
        }
        catch(Exception error)
        {
            string? result=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(result))File.WriteAllText(result,"FAIL: "+error);
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}

// Temporary focused host entry for the isolated debug branch. The normal
// invocation still runs the complete scenario, and the product EXE is unchanged.
internal sealed partial class UiScenario
{
    internal static void RunSubscriptionRequeueOnly()
    {
        static void Stage(string status)
        {
            string? result=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(result))File.WriteAllText(result,status);
        }
        string root=Path.Combine(Path.GetTempPath(),"vd-native-ui-requeue-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Stage("creating focused WPF application");
        var app=new System.Windows.Application {ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
        using var queueOffers=new SubscriptionQueueOfferResponder();
        try
        {
            Stage("testing focused subscription requeue");
            TestSubscriptionRequeue(root);
        }
        finally
        {
            Stage("shutdown focused dispatcher");
            app.Shutdown();
            Stage("delete focused isolated test profile");
            try {Directory.Delete(root,true);}catch(IOException){}
        }
        Stage("PASS");
    }
}
