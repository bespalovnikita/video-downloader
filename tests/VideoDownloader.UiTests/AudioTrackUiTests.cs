using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestAudioTrackSettings(string testRoot)
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Audio-track UI: "+message);
        }
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(testRoot,"audio-tracks"));
        using var subject=new NativeWindow(window,storage,"must-not-launch");
        try
        {
            window.Show();
            ((Button)window.FindName("OpenQueueOptionsButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
            var mode=(ComboBox)window.FindName("AudioTrackCombo")!;
            var languages=(TextBox)window.FindName("AudioLanguagesBox")!;
            var languagePanel=(StackPanel)window.FindName("AudioLanguagesPanel")!;
            var hint=(TextBlock)window.FindName("AudioTrackHint")!;
            mode.BringIntoView();window.UpdateLayout();
            Require(mode.IsVisible && !languages.IsVisible && languagePanel.Visibility==Visibility.Collapsed && hint.Visibility==Visibility.Collapsed,
                "Default audio must show the mode selector without inactive language fields or unrelated explanations");
            Require(subject.ReadOptions().AudioTrack=="Default" && !languages.IsEnabled,"Old/default settings use automatic audio without a language filter");
            mode.SelectedItem="По языкам";
            languages.BringIntoView();window.UpdateLayout();
            Require(languages.IsEnabled && languages.IsVisible && hint.IsVisible && hint.Text.Contains("ru,en"),
                "Language selection must reveal the actual language field and its relevant guidance");
            languages.Text="ru,en";
            var selected=subject.ReadOptions();
            Require(selected.AudioTrack=="Languages" && selected.AudioLanguages=="ru,en","UI saves stable mode IDs and selected languages");
            storage.Save([],selected);
            mode.SelectedItem="Оригинальная";window.UpdateLayout();
            Require(!languages.IsVisible && !languages.IsEnabled && languages.Text=="ru,en" && hint.IsVisible && !hint.Text.Contains("ru,en"),
                "Original audio must hide the unrelated language input, preserve its draft and show only its own guidance");
            mode.SelectedItem="По умолчанию";window.UpdateLayout();
            Require(languagePanel.Visibility==Visibility.Collapsed && hint.Visibility==Visibility.Collapsed && languages.Text=="ru,en",
                "Returning to default audio must hide optional guidance without clearing the language draft");
            mode.SelectedItem="По языкам";window.UpdateLayout();
            Require(languages.IsVisible && languages.Text=="ru,en","Returning to language selection must restore the existing draft");
            subject.ApplyOptions(new DownloadOptions{AudioTrack="Original"});
            Require(Equals(mode.SelectedItem,"Оригинальная") && !languages.IsEnabled,"Original audio does not enable unrelated language input");
            subject.ApplyOptions(storage.Restore()!.Settings);
            window.UpdateLayout();
            Require(languages.IsEnabled && languages.IsVisible && languages.Text=="ru,en" && subject.ReadOptions().AudioTrack=="Languages",
                "Checkpoint restore must reveal the saved language mode and restore its field value");
            Require(subject.SessionState.Queue.Count==0 && subject.SessionState.Coordinator==null,"Audio settings must not start downloads");
            Console.WriteLine("PASS: audio-track controls, language selection and saved settings without downloads");
        }
        finally { subject.Dispose();if(window.IsVisible)window.Close(); }
    }
}
