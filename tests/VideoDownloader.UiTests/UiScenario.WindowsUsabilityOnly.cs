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
        var failures=new List<Exception>();
        void Check(string label,Action action)
        {
            Stage("focused Windows usability: "+label);
            try{action();}
            catch(Exception error){failures.Add(new InvalidOperationException(label,error));}
        }
        try
        {
            using var offers=new SubscriptionQueueOfferResponder();
            Check("tooltip reading time and overdue dispatcher timers",TestToolTipTimers);
            Check("subscription search and compact navigation",()=>TestSubscriptionFilters(root));
            Check("adaptive main window and keyboard navigation",()=>TestPanelLayout(root));
            Check("contrast palette and inherited editor controls",TestWindowContrastTheme);
            Check("component readiness, progress and joined cancellation",()=>TestDependencyControls(root));
            Check("safe record removal and destructive file confirmation",()=>TestQueueRecordRemoval(root));
            Check("invalid input, duplicates and nested admissions",()=>TestInputFeedback(root));
            Check("fragment editing, expert disclosure and error focus",()=>TestMediaSelection(root));
            Check("diagnostics and component replacement exclusion",()=>TestJobDiagnostics(root));
            if(failures.Count>0)throw new AggregateException("Focused Windows usability checks failed.",failures);
        }
        finally
        {
            app.Shutdown();
            try{Directory.Delete(root,true);}catch(IOException){}
        }
        Stage("PASS");
    }
}
