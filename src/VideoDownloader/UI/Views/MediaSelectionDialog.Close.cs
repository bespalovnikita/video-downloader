using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;

namespace VideoDownloader.Native;

internal sealed partial class MediaSelectionDialog
{
    private sealed record EditorDraft(int Mode,string Primary,string Audio,string Start,string End,
        bool Multiple,string Sections,bool Merge,bool Reencode);
    private EditorDraft? _initialDraft;
    private StackPanel? _discardPrompt;
    private Button? _continueEditing;
    private IInputElement? _discardReturnFocus;
    private HwndSource? _closeSource;
    private bool _userCloseRequested,_discardAccepted;

    private void InitializeDiscardPrompt(StackPanel footer)
    {
        _discardPrompt=Named("MediaDiscardDraftPanel",new StackPanel{Visibility=Visibility.Collapsed,
            Margin=new Thickness(0,8,0,0)});
        var explanation=Note("Изменения ещё не применены. Продолжить редактирование или закрыть без применения?");
        AutomationProperties.SetLiveSetting(explanation,AutomationLiveSetting.Assertive);
        _discardPrompt.Children.Add(explanation);
        var actions=new WrapPanel();
        _continueEditing=Named("ContinueMediaEditingButton",new Button{Content="Продолжить редактирование"});
        var discard=Named("DiscardMediaDraftButton",new Button{Content="Закрыть без применения"});
        actions.Children.Add(_continueEditing);actions.Children.Add(discard);_discardPrompt.Children.Add(actions);
        _continueEditing.Click+=(_,_)=>ContinueEditorDraft();
        discard.Click+=(_,_)=>{_discardAccepted=true;Close();};
        footer.Children.Add(_discardPrompt);
    }

    private EditorDraft CaptureEditorDraft()
    {
        bool multiple=_multipleSections?.IsChecked==true;
        bool manual=_mode.SelectedIndex==1;
        // Inactive extra ranges are still valuable unapplied work. A plain
        // single-range on/off round trip does not invent a dirty draft.
        bool retainSections=multiple || _sectionDrafts.Count>1;
        return new(_mode.SelectedIndex,manual?_wantedPrimary:"",manual?_wantedAudio:"",multiple?"":_start.Text,multiple?"":_end.Text,
            multiple,retainSections?JsonSerializer.Serialize(_sectionDrafts.Select(draft=>new[]{draft.Start,draft.End})):"",
            multiple && _mergeDraft,multiple && _mergeReencodeDraft);
    }

    private void InitializeEditorCloseGuard()
    {
        _initialDraft=CaptureEditorDraft();
        // Let focused controls handle Escape first (in particular an open
        // ComboBox). A tunnelling Window.PreviewKeyDown closes too early.
        KeyDown+=EditorKeyDown;
        Closing+=EditorClosing;
        SourceInitialized+=(_,_)=>
        {
            _closeSource=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _closeSource?.AddHook(EditorWindowMessage);
        };
        Closed+=(_,_)=>
        {
            if(_closeSource is {IsDisposed:false} source)source.RemoveHook(EditorWindowMessage);
            _closeSource=null;_discardReturnFocus=null;
        };
    }

    private nint EditorWindowMessage(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        // Chrome Close and Alt+F4 enter through SC_CLOSE. Programmatic owner
        // shutdown/reset still closes immediately and joins preview owners;
        // it must never wait for a user-facing draft confirmation.
        if(message==0x0112 && (wParam.ToInt64()&0xfff0)==0xf060)_userCloseRequested=true;
        return 0;
    }

    private void EditorKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Handled || e.Key!=Key.Escape)return;
        e.Handled=true;
        var open=new[]{_mode,_primary,_audio}.FirstOrDefault(combo=>combo.IsDropDownOpen);
        if(open is not null){open.IsDropDownOpen=false;return;}
        if(_discardPrompt?.Visibility==Visibility.Visible){ContinueEditorDraft();return;}
        RequestEditorClose();
    }

    private void RequestEditorClose()
    {_userCloseRequested=true;Close();}

    private void EditorClosing(object? sender,CancelEventArgs e)
    {
        bool userClose=_userCloseRequested;_userCloseRequested=false;
        if(e.Cancel || !userClose || _discardAccepted || _initialDraft==CaptureEditorDraft())return;
        e.Cancel=true;
        if(_discardPrompt is null || _continueEditing is null)return;
        if(_discardPrompt.Visibility!=Visibility.Visible)_discardReturnFocus=Keyboard.FocusedElement;
        _applyButton.IsDefault=false;_continueEditing.IsDefault=true;
        _discardPrompt.Visibility=Visibility.Visible;UpdateLayout();_continueEditing.Focus();
    }

    private void ContinueEditorDraft()
    {
        if(_discardPrompt is null)return;
        if(_continueEditing is not null)_continueEditing.IsDefault=false;
        _applyButton.IsDefault=true;
        _discardPrompt.Visibility=Visibility.Collapsed;
        bool restored=_discardReturnFocus is UIElement {IsVisible:true,IsEnabled:true} target && target.Focus();
        if(!restored && _start.IsVisible && _start.IsEnabled)
        {_start.BringIntoView();restored=_start.Focus();}
        if(!restored && FindName("CloseMediaSelectionButton") is Button {IsVisible:true,IsEnabled:true} close)close.Focus();
        _discardReturnFocus=null;
    }
}
