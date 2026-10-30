using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private const string FilenameTemplateHint="Шаблон имени файла yt-dlp: %(title)s — название, %(id)s — ID, %(ext)s — расширение. " +
        "Например: %(title)s [%(id)s].%(ext)s. Полный список примеров и правила — по кнопке «Справка…».";

    private const string FilenameTemplateHelp="""
        Как работает Filename template

        Обычный текст остаётся в имени, а выражения %(поле)s заменяются метаданными yt-dlp. Для чисел можно указать формат: %(playlist_index)03d → 001, 002, 003. Чтобы получить знак процента, напиши %%.

        Частые поля
        %(title)s — название видео
        %(id)s — идентификатор видео; помогает различать одинаковые названия
        %(ext)s — расширение; оставляй этот плейсхолдер, чтобы yt-dlp учитывал выбранный формат
        %(uploader)s — имя загрузившего материал автора
        %(channel)s — название канала
        %(upload_date)s — дата публикации в формате ГГГГММДД
        %(duration)s — длительность в секундах
        %(playlist_title)s — название плейлиста
        %(playlist_index)03d — номер видео в плейлисте с ведущими нулями
        %(autonumber)03d — порядковый номер внутри одного запуска yt-dlp

        Примеры
        %(title)s [%(id)s].%(ext)s
        → Название ролика [abc123].mp4

        %(upload_date)s - %(title)s [%(id)s].%(ext)s
        → 20261030 - Название ролика [abc123].mp4

        %(playlist_index)03d - %(title)s [%(id)s].%(ext)s
        → 001 - Название ролика [abc123].mp4

        %(channel)s\%(title)s [%(id)s].%(ext)s
        → папка канала внутри папки загрузок, затем файл видео

        Что учитывать
        • Набор метаданных зависит от сайта. Отсутствующее поле обычно заменяется на NA. Поля плейлиста есть только при наличии контекста плейлиста: отдельные URL из подписки могут его не иметь.
        • У каждого URL очереди свой запуск yt-dlp. autonumber не задаёт сквозную нумерацию всей очереди или подписки.
        • Используй относительный шаблон. Абсолютные пути, буква диска, двоеточие и последовательность .. запрещены; папки внутри каталога загрузок допустимы.
        • Папка подписки добавляется приложением отдельно. Название подписки не является полем шаблона yt-dlp.
        • yt-dlp очищает недопустимые для Windows символы. Конечное расширение может измениться при конвертации или remux; примеры предполагают MP4.
        • Уже существующие файлы не перезаписываются: при подтверждённом повторе приложение подбирает свободное имя.

        Расширенный синтаксис, запасные поля и полный перечень:
        https://github.com/yt-dlp/yt-dlp#output-template
        """;

    private void BindFilenameTemplateHelp()
    {
        B("FilenameTemplateBox").ToolTip=FilenameTemplateHint;
        T("FilenameTemplateLabel").ToolTip=FilenameTemplateHint;
        F<Button>("FilenameTemplateHelpButton").Click+=(_,_)=>ShowFilenameTemplateHelp();
    }

    private void ShowFilenameTemplateHelp()
    {
        var dialog=new Window
        {
            Title="Шаблон имени файла — справка",Owner=_window,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Width=760,Height=660,MinWidth=500,MinHeight=380,
            Background=_window.Background,Foreground=_window.Foreground,
            FontFamily=_window.FontFamily,Resources=_window.Resources
        };
        var content=new DockPanel{Margin=new Thickness(16)};
        var close=new Button{Content="Закрыть",IsCancel=true,IsDefault=true,
            HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        close.Click+=(_,_)=>dialog.Close();
        DockPanel.SetDock(close,Dock.Bottom);
        content.Children.Add(close);
        content.Children.Add(new TextBox
        {
            Text=FilenameTemplateHelp,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            Background=Brushes.Transparent,BorderThickness=new Thickness(0),
            FontSize=14,Padding=new Thickness(6)
        });
        dialog.Content=content;
        dialog.ShowDialog();
    }
}
