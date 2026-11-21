using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private static readonly DownloadOptions AdvancedDefaults=new();

    private void InitializeAdvancedOptions()
    {
        F<Expander>("QueueAdvancedOptionsExpander").Collapsed+=(_,_)=>RefreshAdvancedOptionsSummary();
        B("ThreadsBox").TextChanged+=(_,_)=>RefreshAdvancedOptionsSummary();
        B("FilenameTemplateBox").TextChanged+=(_,_)=>RefreshAdvancedOptionsSummary();
        C("FragmentsCombo").SelectionChanged+=(_,_)=>RefreshAdvancedOptionsSummary();
        foreach(string name in new[]{"HttpChunksCheck","PreferAria2cCheck"})
        {
            K(name).Checked+=(_,_)=>RefreshAdvancedOptionsSummary();
            K(name).Unchecked+=(_,_)=>RefreshAdvancedOptionsSummary();
        }
        C("RateLimitCombo").AddHandler(TextBox.TextChangedEvent,new TextChangedEventHandler((_,_)=>RefreshAdvancedOptionsSummary()));
        F<Button>("ResetQueueAdvancedOptionsButton").Click+=(_,_)=>ResetAdvancedOptions();
        RefreshAdvancedOptionsSummary();
    }

    private void RefreshAdvancedOptionsSummary()
    {
        if(_disposed || _closing)return;
        var defaults=AdvancedDefaults;
        var parts=new List<string>();
        string rate=DisplayValueConverter.OptionId(C("RateLimitCombo").Text);
        if(!string.Equals(rate,defaults.RateLimit,StringComparison.Ordinal))
            parts.Add(ErrorMessages.Text("Скорость: ","Rate: ")+DisplayValueConverter.Display(rate));
        if(B("ThreadsBox").Text.Trim()!=defaults.Threads)
            parts.Add(ErrorMessages.Text("Параллельно: ","Parallel: ")+B("ThreadsBox").Text.Trim());
        if(Pick(C("FragmentsCombo"))!=defaults.Fragments)
            parts.Add(ErrorMessages.Text("Фрагменты: ","Fragments: ")+DisplayValueConverter.Display(Pick(C("FragmentsCombo"))));
        if(Check(K("HttpChunksCheck")))
            parts.Add(ErrorMessages.Text("Блоки HTTP по 4 МиБ","4 MiB HTTP chunks"));
        if(Check(K("PreferAria2cCheck")))
            parts.Add(ErrorMessages.Text("aria2c для прямого HTTP(S)","aria2c for direct HTTP(S)"));
        if(B("FilenameTemplateBox").Text!=defaults.FilenameTemplate)
            parts.Add(ErrorMessages.Text("Свой шаблон имени","Custom filename template"));
        string text=string.Join(" · ",parts);
        var summary=T("QueueAdvancedOptionsSummary");
        if(summary.Text!=text)summary.Text=text;
        summary.Visibility=parts.Count==0?Visibility.Collapsed:Visibility.Visible;
        F<Button>("ResetQueueAdvancedOptionsButton").Visibility=summary.Visibility;
        string title=ErrorMessages.Text("Дополнительные параметры","Advanced options");
        var group=F<Expander>("QueueAdvancedOptionsExpander");
        string accessibleName=parts.Count==0?title:title+": "+text;
        if(System.Windows.Automation.AutomationProperties.GetName(group)!=accessibleName)
            System.Windows.Automation.AutomationProperties.SetName(group,accessibleName);
        group.ToolTip=ErrorMessages.Text("Параметры для следующего запуска. Сворачивание сохраняет их значения.",
            "Options for the next start. Collapsing the group keeps their values.");
    }

    private void ResetAdvancedOptions()
    {
        if(_disposed || _closing || _appUpdateHandoff || RejectProfileBackupMutation())return;
        var defaults=AdvancedDefaults;
        C("RateLimitCombo").Text=DisplayValueConverter.Display(defaults.RateLimit);
        B("ThreadsBox").Text=defaults.Threads;
        C("FragmentsCombo").SelectedItem=defaults.Fragments;
        K("HttpChunksCheck").IsChecked=defaults.UseHttpChunks;
        K("PreferAria2cCheck").IsChecked=defaults.PreferAria2c;
        B("FilenameTemplateBox").Text=defaults.FilenameTemplate;
        RefreshAdvancedOptionsSummary();SaveCheckpoint();
    }
}
