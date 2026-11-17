using System.IO;
using System.Windows;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    // Run the changed interactions before the long actual-media integration
    // suite. The full host still executes these same scenarios independently.
    internal static void RunWindowsUsabilityOnly()
    {
        static void Stage(string value)
        {
            string? path=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(path))File.WriteAllText(path,value);
        }
        string root=Path.Combine(Path.GetTempPath(),"vd-usability-ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        try
        {
            using var offers=new SubscriptionQueueOfferResponder();
            Stage("focused Windows usability: subscription search and compact navigation");
            TestSubscriptionFilters(root);
            Stage("focused Windows usability: adaptive main window and keyboard navigation");
            TestPanelLayout(root);
            Stage("focused Windows usability: contrast palette and inherited editor controls");
            TestWindowContrastTheme();
            Stage("focused Windows usability: component readiness, progress and joined cancellation");
            TestDependencyControls(root);
            Stage("focused Windows usability: safe record removal and destructive file confirmation");
            TestQueueRecordRemoval(root);
            Stage("focused Windows usability: invalid input, duplicates and nested admissions");
            TestInputFeedback(root);
            Stage("focused Windows usability: fragment editing, expert disclosure and error focus");
            TestMediaSelection(root);
            Stage("focused Windows usability: diagnostics and component replacement exclusion");
            TestJobDiagnostics(root);
        }
        finally
        {
            app.Shutdown();
            try{Directory.Delete(root,true);}catch(IOException){}
        }
        Stage("PASS");
    }
}
