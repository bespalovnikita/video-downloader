namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private SubscriptionController? _subscriptions;
    internal SubscriptionController Subscriptions=>_subscriptions ??= new SubscriptionController(
        _window,new SubscriptionStore(Path.GetDirectoryName(_storage.SessionPath)!),_ytDlp,
        Path.Combine(AppContext.BaseDirectory,"ui","SubscriptionsWindow.xaml"),
        _storage.ReadHistory,()=>B("CookiesBox").Text.Trim(),QueueSubscriptionUrls,OpenUrl,Log);

    private void OpenSubscriptions()
    {
        if(!_disposed)Subscriptions.Open();
    }
    private void DisposeSubscriptions()=>_subscriptions?.Dispose();
}
