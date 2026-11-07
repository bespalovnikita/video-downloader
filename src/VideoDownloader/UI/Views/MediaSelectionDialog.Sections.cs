using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ListBox=System.Windows.Controls.ListBox;
using Orientation=System.Windows.Controls.Orientation;

namespace VideoDownloader.Native;

internal sealed partial class MediaSelectionDialog
{
    private sealed class SectionDraft : INotifyPropertyChanged
    {
        internal string Start="",End="";
        internal int Number;
        public string Label=>$"{Number}. "+Boundary(Start)+" — "+Boundary(End);
        private static string Boundary(string text)=>DownloadSection.TryParseTimestamp(text,out double seconds)?
            DownloadSection.FormatTimestamp(seconds):string.IsNullOrWhiteSpace(text)?"не задано":"неверное время";
        internal void Notify()=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Label)));
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly ObservableCollection<SectionDraft> _sectionDrafts=new();
    private CheckBox? _multipleSections;
    private ListBox? _sectionList;
    private FrameworkElement? _sectionListPanel;
    private Button? _addSection,_removeSection,_sectionUp,_sectionDown;
    private bool _switchingSection;

    private void InitializeSectionList(StackPanel body,FrameworkElement range,DownloadSelection? selection)
    {
        var panel=new StackPanel();
        _multipleSections=Named("MultipleMediaSectionsCheck",new CheckBox{Content="Несколько фрагментов",Margin=new Thickness(0,6,0,6)});
        panel.Children.Add(_multipleSections);
        var details=new StackPanel();_sectionListPanel=details;
        _sectionList=Named("MediaSectionsList",new ListBox{ItemsSource=_sectionDrafts,DisplayMemberPath=nameof(SectionDraft.Label),
            MinHeight=48,MaxHeight=140,Margin=new Thickness(0,2,0,4),Background=Background,Foreground=Foreground,
            HorizontalContentAlignment=System.Windows.HorizontalAlignment.Stretch});
        details.Children.Add(_sectionList);
        var actions=new WrapPanel{Orientation=Orientation.Horizontal};
        Button Action(string name,string label)
        {
            var button=Named(name,new Button{Content=label,Margin=new Thickness(0,2,6,2)});
            actions.Children.Add(button);return button;
        }
        _addSection=Action("AddMediaSectionButton","Добавить фрагмент");
        _removeSection=Action("RemoveMediaSectionButton","Удалить фрагмент");
        _sectionUp=Action("MoveMediaSectionUpButton","Выше");
        _sectionDown=Action("MoveMediaSectionDownButton","Ниже");
        details.Children.Add(actions);
        details.Children.Add(Note($"До {DownloadSectionSet.MaximumCount} непересекающихся фрагментов. Выбери строку и меняй её начало/конец ниже. Порядок списка задаёт порядок файлов или отрезков в одном ролике. Выключение списка оставляет текущий диапазон."));
        panel.Children.Add(details);
        body.Children.Insert(body.Children.IndexOf(range),panel);

        foreach(var section in selection?.EffectiveSections??Array.Empty<DownloadSection>())
            _sectionDrafts.Add(new SectionDraft{Start=DownloadSection.FormatTimestamp(section.StartSeconds),End=DownloadSection.FormatTimestamp(section.EndSeconds)});
        RenumberSections();
        _multipleSections.IsChecked=_sectionDrafts.Count>1;
        _sectionList.SelectedIndex=_sectionDrafts.Count>0?0:-1;
        _sectionListPanel.Visibility=_multipleSections.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
        InitializeFragmentOutput(details,selection);
        _multipleSections.Checked+=SectionModeChanged;_multipleSections.Unchecked+=SectionModeChanged;
        _sectionList.SelectionChanged+=SectionChanged;
        _start.TextChanged+=SectionTextChanged;_end.TextChanged+=SectionTextChanged;
        _addSection.Click+=(_,_)=>AddSection();
        _removeSection.Click+=(_,_)=>RemoveSection();
        _sectionUp.Click+=(_,_)=>MoveSection(-1);_sectionDown.Click+=(_,_)=>MoveSection(1);
        Closed+=(_,_)=>
        {
            _start.TextChanged-=SectionTextChanged;_end.TextChanged-=SectionTextChanged;
            _sectionList.SelectionChanged-=SectionChanged;
            _multipleSections.Checked-=SectionModeChanged;_multipleSections.Unchecked-=SectionModeChanged;
        };
        RefreshSectionControls();
    }
    private void SectionModeChanged(object sender,RoutedEventArgs e)
    {
        if(_multipleSections is null || _sectionList is null || _sectionListPanel is null)return;
        if(!_editable){_multipleSections.IsChecked=_sectionListPanel.Visibility==Visibility.Visible;return;}
        bool multiple=_multipleSections.IsChecked==true;
        if(!multiple || _sectionDrafts.Count<=1)
        {
            _sectionDrafts.Clear();
            _sectionDrafts.Add(new SectionDraft{Start=_start.Text,End=_end.Text});
            RenumberSections();_sectionList.SelectedIndex=0;
        }
        _sectionListPanel.Visibility=multiple?Visibility.Visible:Visibility.Collapsed;
        if(multiple)DisplaySelectedSection();
        RefreshSectionControls();
    }
    private void SectionTextChanged(object sender,TextChangedEventArgs e)
    {
        if(_switchingSection || _multipleSections?.IsChecked!=true || _sectionList?.SelectedItem is not SectionDraft current)return;
        current.Start=_start.Text;current.End=_end.Text;current.Notify();
    }
    private void SectionChanged(object sender,SelectionChangedEventArgs e)
    {DisplaySelectedSection();RefreshSectionControls();}
    private void DisplaySelectedSection()
    {
        if(_multipleSections?.IsChecked!=true || _sectionList?.SelectedItem is not SectionDraft current)return;
        _switchingSection=true;
        try{_start.Text=current.Start;_end.Text=current.End;}
        finally{_switchingSection=false;}
    }
    private void AddSection()
    {
        if(!_editable || _multipleSections?.IsChecked!=true || _sectionDrafts.Count>=DownloadSectionSet.MaximumCount || _sectionList is null)return;
        var added=new SectionDraft();_sectionDrafts.Add(added);RenumberSections();_sectionList.SelectedItem=added;RefreshSectionControls();
    }
    private void RemoveSection()
    {
        if(!_editable || _multipleSections?.IsChecked!=true || _sectionList is null || _sectionList.SelectedIndex<0)return;
        int index=_sectionList.SelectedIndex;_sectionDrafts.RemoveAt(index);RenumberSections();
        _sectionList.SelectedIndex=Math.Min(index,_sectionDrafts.Count-1);
        if(_sectionDrafts.Count==0){_start.Text="";_end.Text="";}
        RefreshSectionControls();
    }
    private void MoveSection(int direction)
    {
        if(!_editable || _multipleSections?.IsChecked!=true || _sectionList is null)return;
        int index=_sectionList.SelectedIndex,target=index+direction;
        if(index<0 || target<0 || target>=_sectionDrafts.Count)return;
        _sectionDrafts.Move(index,target);RenumberSections();_sectionList.SelectedIndex=target;RefreshSectionControls();
    }
    private void RenumberSections()
    {for(int index=0;index<_sectionDrafts.Count;index++){_sectionDrafts[index].Number=index+1;_sectionDrafts[index].Notify();}}
    private void RefreshSectionControls()
    {
        if(_multipleSections is null || _sectionList is null || _addSection is null || _removeSection is null || _sectionUp is null || _sectionDown is null)return;
        bool enabled=_editable && _multipleSections.IsChecked==true;
        _multipleSections.IsEnabled=_editable;_sectionList.IsEnabled=enabled;
        _addSection.IsEnabled=enabled && _sectionDrafts.Count<DownloadSectionSet.MaximumCount;
        int selected=_sectionList.SelectedIndex;
        _removeSection.IsEnabled=enabled && selected>=0;
        _sectionUp.IsEnabled=enabled && selected>0;
        _sectionDown.IsEnabled=enabled && selected>=0 && selected<_sectionDrafts.Count-1;
        RefreshFragmentOutput();
    }
    private bool TryReadSections(double? duration,out DownloadSection[] sections,out string error)
    {
        sections=[];error="";
        if(_multipleSections?.IsChecked!=true)
        {
            if(!DownloadSection.TryParse(_start.Text,_end.Text,duration,out var section,out error))return false;
            sections=section is null?[]:new[]{section};return true;
        }
        if(_sectionDrafts.Count==0){error="Добавьте хотя бы один фрагмент или выключите список.";return false;}
        var parsed=new List<DownloadSection>();
        for(int index=0;index<_sectionDrafts.Count;index++)
        {
            var draft=_sectionDrafts[index];
            if(!DownloadSection.TryParse(draft.Start,draft.End,duration,out var section,out error) || section is null)
            {error=$"Фрагмент {index+1}: "+(error.Length>0?error:"укажите начало и конец.");return false;}
            parsed.Add(section);
        }
        try{var set=new DownloadSectionSet(parsed);set.Validate(duration);sections=set.ToArray();return true;}
        catch(ArgumentException issue){error=issue.Message;return false;}
    }
}
