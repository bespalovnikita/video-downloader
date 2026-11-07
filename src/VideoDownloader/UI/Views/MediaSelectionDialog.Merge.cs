using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

internal sealed partial class MediaSelectionDialog
{
    private CheckBox? _mergeSections,_mergeReencode;
    private TextBlock? _mergeDescription;
    private bool _mergeDraft,_mergeReencodeDraft,_syncingMerge;

    private void InitializeFragmentOutput(StackPanel parent,DownloadSelection? selection)
    {
        _mergeDraft=selection?.IsMerged==true;
        _mergeReencodeDraft=_mergeDraft && selection?.AllowMergeReencode==true;
        _mergeSections=Named("MergeMediaSectionsCheck",new CheckBox{
            Content="Объединить выбранные фрагменты в один файл",Margin=new Thickness(0,8,0,4)});
        _mergeReencode=Named("AllowMergeReencodeCheck",new CheckBox{
            Content="Разрешить дополнительное перекодирование при несовместимости потоков",Margin=new Thickness(18,2,0,4)});
        _mergeDescription=Named("MediaMergeCompatibilityText",Note(""));
        parent.Children.Add(_mergeSections);parent.Children.Add(_mergeReencode);parent.Children.Add(_mergeDescription);
        _mergeSections.Checked+=MergeOutputChanged;_mergeSections.Unchecked+=MergeOutputChanged;
        _mergeReencode.Checked+=MergeReencodeChanged;_mergeReencode.Unchecked+=MergeReencodeChanged;
        Closed+=(_,_)=>
        {
            _mergeSections.Checked-=MergeOutputChanged;_mergeSections.Unchecked-=MergeOutputChanged;
            _mergeReencode.Checked-=MergeReencodeChanged;_mergeReencode.Unchecked-=MergeReencodeChanged;
        };
        RefreshFragmentOutput();
    }

    private void MergeOutputChanged(object sender,RoutedEventArgs e)
    {
        if(_syncingMerge)return;
        if(_editable && _multipleSections?.IsChecked==true)
        {
            _mergeDraft=_mergeSections?.IsChecked==true;
            if(!_mergeDraft)_mergeReencodeDraft=false;
        }
        RefreshFragmentOutput();
    }

    private void MergeReencodeChanged(object sender,RoutedEventArgs e)
    {
        if(_syncingMerge)return;
        if(_editable && _multipleSections?.IsChecked==true && _mergeDraft)
            _mergeReencodeDraft=_mergeReencode?.IsChecked==true;
        RefreshFragmentOutput();
    }

    private void RefreshFragmentOutput()
    {
        if(_mergeSections is null || _mergeReencode is null || _mergeDescription is null)return;
        bool multiple=_multipleSections?.IsChecked==true;
        if(!multiple && _editable){_mergeDraft=false;_mergeReencodeDraft=false;}
        _syncingMerge=true;
        try
        {
            _mergeSections.IsChecked=_mergeDraft;_mergeReencode.IsChecked=_mergeReencodeDraft;
            _mergeSections.IsEnabled=_editable && multiple;
            _mergeReencode.IsEnabled=_editable && multiple && _mergeDraft;
        }
        finally{_syncingMerge=false;}
        if(!_mergeDraft)
        {
            _mergeDescription.Text="Каждый фрагмент будет сохранён отдельным файлом.";
            return;
        }
        string count=_sectionDrafts.Count<2?"Для одного ролика добавь не менее двух фрагментов. ":"";
        _mergeDescription.Text=count+"Отрезки будут соединены в порядке списка. Фактическая совместимость потоков проверяется после получения всех фрагментов. "+
            (_mergeReencodeDraft?"При необходимости разрешено дополнительное перекодирование: кодеки и качество могут измениться, размер кадра и частота кадров приводятся к первому фрагменту с полями при необходимости. ":
                "Дополнительное перекодирование запрещено: при несовместимости программа объяснит причину. ")+
            "Для склейки отключи обычные и автоматические субтитры и их встраивание. "+
            "Предпросмотр выше по-прежнему относится к выбранному отрезку. Склейка не отмечает полное видео скачанным.";
    }
}
