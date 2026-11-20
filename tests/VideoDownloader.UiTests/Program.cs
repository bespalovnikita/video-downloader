namespace VideoDownloader.UiTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // A failed native Dispatcher wake-up must expose its originating call
        // instead of silently leaving every timer/queued operation asleep.
        System.Windows.BaseCompatibilityPreferences.HandleDispatcherRequestProcessingFailure=
            System.Windows.BaseCompatibilityPreferences.HandleDispatcherRequestProcessingFailureOptions.Throw;
        // WinExe hosts do not reliably forward CLR/dispatcher crash text to
        // the CI console. Keep the original exception in the isolated result
        // file without marking the failure handled or changing its exit code.
        static void RecordFailure(object? error)
        {
            int nativeError=System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            string? result=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(result))
                try{File.WriteAllText(result,"FAIL: "+error+Environment.NewLine+"Cached P/Invoke error (context only): "+nativeError);}catch(IOException){}
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
            string scene=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_SCENE")??"All";
            using var diagnostics=new UiRunDiagnostics(scene);
            switch(scene)
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
