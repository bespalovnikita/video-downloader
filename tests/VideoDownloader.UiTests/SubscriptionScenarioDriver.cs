using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using VideoDownloader.Native;

namespace VideoDownloader.UiTests;

// All subscription fixture access stays in the test assembly. The driver wraps
// the actual production owner and actual controls; it does not replace its scan,
// progress, queue acceptance or lifecycle handlers with a second implementation.
internal sealed class SubscriptionScenarioDriver(SubscriptionController subject)
{
    private const BindingFlags PrivateInstance=BindingFlags.NonPublic|BindingFlags.Instance;
    private static readonly Type Owner=typeof(SubscriptionController);
    private static readonly FieldInfo WindowField=Field("_window");
    private static readonly FieldInfo StoreField=Field("_store");
    private static readonly PropertyInfo BusyProperty=Owner.GetProperty("Busy",PrivateInstance)
        ?? throw new MissingMemberException(Owner.FullName,"Busy");
    private static readonly FieldInfo CancellationField=Field("_cancellation");
    private static readonly FieldInfo ProgressField=Field("_checkProgress");
    private static readonly MethodInfo UpdateButtons=Owner.GetMethod("UpdateButtons",PrivateInstance)
        ?? throw new MissingMethodException(Owner.FullName,"UpdateButtons");

    internal Window? TestWindow=>(Window?)WindowField.GetValue(subject);
    internal SubscriptionStore TestStore=>(SubscriptionStore)StoreField.GetValue(subject)!;
    internal bool TestIsMutating=>(bool)BusyProperty.GetValue(subject)!;
    internal SubscriptionCheckProgressController TestProgress=>(SubscriptionCheckProgressController)ProgressField.GetValue(subject)!;
    internal DataGrid TestSources=>TestControl<DataGrid>("SubscriptionGrid");
    internal DataGrid TestInbox=>TestControl<DataGrid>("SubscriptionInboxGrid");
    internal T TestControl<T>(string name) where T:class => (T)(TestWindow?.FindName(name)
        ?? throw new InvalidOperationException("Missing subscription fixture control: "+name));

    // These two fixtures isolate cancellation from downloads without opening a
    // public mutation API on the application. The real Cancel button owns the
    // operation and progress transition; the test only supplies its source.
    internal CancellationTokenSource? TestCancellation
    {
        get=>(CancellationTokenSource?)CancellationField.GetValue(subject);
        set=>CancellationField.SetValue(subject,value);
    }
    internal void TestUpdateButtons()
    {
        try { UpdateButtons.Invoke(subject,null); }
        catch(TargetInvocationException error) when(error.InnerException!=null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw; }
    }

    internal void Open()=>subject.Open();
    internal void Refresh()=>subject.Refresh();

    private static FieldInfo Field(string name)=>Owner.GetField(name,PrivateInstance)
        ?? throw new MissingFieldException(Owner.FullName,name);
}
