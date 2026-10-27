using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VideoDownloader.UiTests;

// The host composes the real application controller. All assertions and test
// fixture state live in this assembly; the product is referenced, not rebuilt
// from linked source or extended by a test-only NativeWindow partial class.
internal sealed partial class UiScenario : IDisposable
{
    private readonly Window _window;
    private readonly AppPersistence _storage;
    internal NativeWindow Subject { get; }
    private PreviewController _preview=>Subject.Preview;
    private WindowLifecycleController _lifecycle=>Subject.Lifecycle;
    private InputIngressController _ingress=>Subject.Input;
    internal PreviewController Preview=>Subject.Preview;
    internal QueueSessionController SessionState=>Subject.SessionState;
    private SubscriptionController Subscriptions=>Subject.Subscriptions;
    internal Dispatcher Dispatcher=>_window.Dispatcher;

    internal UiScenario(Window window,AppPersistence storage,string? executable=null)
    {
        _window=window;_storage=storage;
        Subject=executable==null?new NativeWindow(window,storage):new NativeWindow(window,storage,executable);
    }
    private T F<T>(string name) where T:class=>(T)(_window.FindName(name)
        ??throw new InvalidOperationException("Missing WPF scenario control: "+name));
    private TextBox B(string name)=>F<TextBox>(name);
    private ComboBox C(string name)=>F<ComboBox>(name);
    private CheckBox K(string name)=>F<CheckBox>(name);
    private TextBlock T(string name)=>F<TextBlock>(name);
    private DataGrid Q=>F<DataGrid>("QueueGrid");
    private DataGrid D=>F<DataGrid>("DownloadGrid");
    private static bool Check(CheckBox control)=>control.IsChecked==true;
    private static string Pick(ComboBox control)=>control.SelectedItem?.ToString()??control.Text;

    internal void ReceiveArguments(IEnumerable<string> arguments)=>Subject.ReceiveArguments(arguments);
    private void Reindex()=>Subject.Reindex();
    private void SaveCheckpoint()=>Subject.SaveCheckpoint();
    private void RequestRowPreview(DownloadItem row,bool priority=false)=>_preview.RequestRow(row,priority);
    private void ShowPreview(string url)=>_preview.Show(url);
    private void PollClipboard()=>_ingress.PollClipboard();
    private void AddUrls(IEnumerable<string> urls,bool clipboard=false,bool selectNew=false)=>Subject.AddUrls(urls,clipboard,selectNew);
    private void ForgetRow(DownloadItem row)=>SessionState.Forget(row);
    private DownloadOptions ReadOptions()=>Subject.ReadOptions();
    private void ApplyOptions(DownloadOptions options)=>Subject.ApplyOptions(options);
    private string ConfiguredOutputDirectory()=>Subject.ConfiguredOutputDirectory();
    private List<DownloadItem>? ConfirmDuplicates(List<DownloadItem> candidates,bool removeRejected)=>
        Subject.ConfirmDuplicates(candidates,removeRejected);
    private IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls)=>Subject.QueueSubscriptionUrls(urls);
    private IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls,string id,string name)=>Subject.QueueSubscriptionUrls(urls,id,name);
    private IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls,string id,string name,IReadOnlyDictionary<string,string>? titles)=>
        Subject.QueueSubscriptionUrls(urls,id,name,titles);

    public void Dispose()=>Subject.Dispose();
}
