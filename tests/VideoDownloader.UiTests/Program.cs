namespace VideoDownloader.UiTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // WinExe hosts do not reliably forward CLR/dispatcher crash text to
        // the CI console. Keep the original exception in the isolated result
        // file without marking the failure handled or changing its exit code.
        static void RecordFailure(object? error)
        {
            string? result=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(result))
                try{File.WriteAllText(result,"FAIL: "+error);}catch(IOException){}
        }
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>RecordFailure(e.ExceptionObject);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException+=(_,e)=>RecordFailure(e.Exception);
        // Scenarios pump the dispatcher explicitly instead of Application.Run.
        // Inline routed user events must capture the normal WPF context so
        // awaited UI handlers resume on their owning dispatcher as in the app.
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            switch(Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_SCENE")??"All")
            {
                case "All": UiScenario.Run();break;
                case "SubscriptionInitialActions": UiScenario.RunSubscriptionInitialActionsOnly();break;
                case "WindowsUsability": UiScenario.RunWindowsUsabilityOnly();break;
                default: throw new InvalidOperationException("Unknown isolated WPF test scene.");
            }
            return 0;
        }
        catch(Exception error)
        {
            RecordFailure(error);
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
