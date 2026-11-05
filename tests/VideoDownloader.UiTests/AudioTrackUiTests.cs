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
            var mode=(ComboBox)window.FindName("AudioTrackCombo")!;
            var languages=(TextBox)window.FindName("AudioLanguagesBox")!;
            Require(subject.ReadOptions().AudioTrack=="Default" && !languages.IsEnabled,"Old/default settings use automatic audio without a language filter");
            mode.SelectedItem="По языкам";
            Require(languages.IsEnabled,"Language selection enables the language field");
            languages.Text="ru,en";
            var selected=subject.ReadOptions();
            Require(selected.AudioTrack=="Languages" && selected.AudioLanguages=="ru,en","UI saves stable mode IDs and selected languages");
            storage.Save([],selected);
            subject.ApplyOptions(new DownloadOptions{AudioTrack="Original"});
            Require(Equals(mode.SelectedItem,"Оригинальная") && !languages.IsEnabled,"Original audio does not enable unrelated language input");
            subject.ApplyOptions(storage.Restore()!.Settings);
            Require(languages.IsEnabled && languages.Text=="ru,en" && subject.ReadOptions().AudioTrack=="Languages","Checkpoint restore restores both mode and language field");
            Require(subject.SessionState.Queue.Count==0 && subject.SessionState.Coordinator==null,"Audio settings must not start downloads");
            Console.WriteLine("PASS: audio-track controls, language selection and saved settings without downloads");
        }
        finally { subject.Dispose();if(window.IsVisible)window.Close(); }
    }
}
