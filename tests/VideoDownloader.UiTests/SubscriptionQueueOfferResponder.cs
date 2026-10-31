using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VideoDownloader.UiTests;

// Real modal-button automation for the isolated WPF host. Existing scenarios
// decline new queue offers unless an individual scenario explicitly answers.
internal sealed class SubscriptionQueueOfferResponder : IDisposable
{
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(20)};
    private readonly HashSet<Window> _seen=new();
    private string _choice="No";
    private Action<Window>? _inspect;
    private Exception? _failure;
    internal static SubscriptionQueueOfferResponder? Current { get; private set; }
    internal int Count { get; private set; }

    internal SubscriptionQueueOfferResponder()
    {
        if(Current!=null)throw new InvalidOperationException("Only one subscription queue-offer responder can own the WPF host.");
        Current=this;_timer.Tick+=Observe;_timer.Start();
    }

    internal IDisposable Respond(string choice,Action<Window>? inspect=null)
    {
        if(choice is not ("Yes" or "No" or "Close"))throw new ArgumentException("Unknown modal response.",nameof(choice));
        var previous=(_choice,_inspect);_choice=choice;_inspect=inspect;
        return new ResponseScope(()=>{(_choice,_inspect)=previous;ThrowIfFailed();});
    }

    private void Observe(object? sender,EventArgs args)
    {
        foreach(var dialog in Application.Current.Windows.Cast<Window>().ToArray())
        {
            if(!dialog.IsVisible || dialog.FindName("SubscriptionQueueOfferSummary") is not TextBlock || !_seen.Add(dialog))continue;
            Count++;
            try
            {
                _inspect?.Invoke(dialog);
                if(!dialog.IsVisible)continue; // A scenario may close the owner.
                if(_choice=="Close")dialog.Close();
                else ((Button)dialog.FindName(_choice=="Yes"?"SubscriptionQueueOfferYesButton":"SubscriptionQueueOfferNoButton")!)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch(Exception error)
            {
                _failure??=error;
                if(dialog.IsVisible)dialog.Close();
            }
        }
    }

    internal void ThrowIfFailed()
    {
        if(_failure!=null)throw new InvalidOperationException("Subscription queue-offer modal automation failed.",_failure);
    }

    public void Dispose()
    {
        _timer.Stop();_timer.Tick-=Observe;
        _seen.Clear();if(ReferenceEquals(Current,this))Current=null;
        ThrowIfFailed();
    }

    private sealed class ResponseScope(Action restore) : IDisposable
    {
        public void Dispose()=>restore();
    }
}
