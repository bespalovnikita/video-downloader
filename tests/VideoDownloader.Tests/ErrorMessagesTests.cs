using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Xml;
using VideoDownloader.Native;

internal static class ErrorMessagesTests
{
    internal static void Run()
    {
        var previousUi=CultureInfo.CurrentUICulture;
        var previousFormat=CultureInfo.CurrentCulture;
        try
        {
            const string denied="ERROR: [youtube] fixture: HTTP Error 403: Forbidden";
            foreach(string language in new[]{"ru","ru-RU","ru-KZ"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("en-US");
                Require(ErrorMessages.IsRussian && ErrorMessages.Text("Ошибка","Error")=="Ошибка",
                    "All Russian UI locales must choose Russian independently of numeric formatting: "+language);
                string translated=ErrorMessages.ForDisplay(denied);
                Require(translated.Contains("доступ",StringComparison.OrdinalIgnoreCase) && translated.Contains("403") && !translated.Contains("Forbidden"),
                    "The actual yt-dlp access diagnostic must have a Russian explanation: "+language);
                Require(CultureInfo.CurrentCulture.Name=="en-US" && CultureInfo.CurrentUICulture.Name==language,
                    "Formatting a message must not change thread cultures");
            }

            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ru-RU");
            Require(!ErrorMessages.IsRussian && ErrorMessages.Text("Ошибка","Error")=="Error" && ErrorMessages.ForDisplay(denied)==denied,
                "Russian regional number settings must not override the English UI language");
            var original=new IOException("  custom extractor output\nkeep exact whitespace  ");
            Require(ErrorMessages.ForException(original)==original.Message && ErrorMessages.ForDisplay(original.Message)==original.Message,
                "Other UI languages must retain the exact original diagnostic");
            Require(ErrorMessages.ForLog(denied)==denied,"Other UI languages must keep original log entries exactly");
            Require(ErrorMessages.ForDisplay(null)=="" && ErrorMessages.ForDisplay("")=="",
                "No diagnostic must remain empty");

            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
            var cases=new (string Diagnostic,string Expected)[]
            {
                ("HTTP Error 400: Bad Request","400"),
                ("HTTP/1.1 401 Unauthorized","авторизац"),
                ("Response status code does not indicate success: 403 (Forbidden).","403"),
                ("HTTP Error 404: Not Found","не найден"),
                ("HTTP Error 410: Gone","410"),
                ("HTTP Error 429: Too Many Requests","частоту"),
                ("HTTP Error 451: Unavailable For Legal Reasons","юридическ"),
                ("HTTP Error 503: Service Unavailable","сервера"),
                ("ERROR: Unsupported URL: https://example.invalid/clip","не поддерживается"),
                ("ERROR: This video is DRM protected", "DRM"),
                ("ERROR: This video is DRM-protected", "DRM"),
                ("ERROR: This video is not available in your country", "регион"),
                ("ERROR: Sign in to confirm you’re not a bot", "проверку пользователя"),
                ("ERROR: Sign in to confirm your age", "аккаунт"),
                ("ERROR: Private video", "закрытый"),
                ("ERROR: This video is age-restricted", "возраст"),
                ("ERROR: This video has been removed", "удалено"),
                ("ERROR: Requested format is not available", "формат"),
                ("ERROR: Video unavailable", "недоступно"),
                ("ERROR: [SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed", "защищённое"),
                ("ERROR: The read operation timed out", "ожидания"),
                ("ERROR: Unable to download webpage: Name or service not known", "адрес сайта"),
                ("ERROR: Connection reset by peer", "подключиться"),
                ("No space left on device", "места"),
                ("Access to the path is denied.", "доступа"),
                (@"Access to the path 'D:\DNS\Timeout\video.mp4' is denied.", "доступа"),
                ("The process cannot access the file because it is being used by another process", "занят"),
                ("Could not find file 'D:/video/missing.mp4'", "Не найден"),
                ("Filename template must be relative and may not contain '..', drive letters or rooted paths.", "Шаблон"),
                ("yt-dlp finished without reporting a downloaded media file.", "не отмечена успешной"),
                ("yt-dlp did not produce the requested container: MP4", "контейнер"),
                ("Refusing to overwrite an existing file: D:/video.mp4", "существующие файлы сохранены"),
                ("Refusing deletion outside download folder: D:/other.mp4", "заблокирована"),
                ("This download has no ownership manifest. Remove its file manually; the application cannot safely delete it.", "удаление заблокировано"),
                ("Staging contains a symlink directory: D:/video", "перенаправления"),
                ("Damaged download history at line 9. No changes made.", "исходный файл сохранён"),
                ("Invalid session checkpoint format.", "Исходный файл сохранён"),
                ("Preview thumbnail unavailable: no JPEG/PNG thumbnail was provided by yt-dlp", "обложку"),
                ("WebP/AVIF thumbnail needs FFmpeg; text metadata is available.", "нужен FFmpeg"),
                ("FFmpeg could not convert the WebP thumbnail; text metadata is available.", "преобразовать"),
                ("Thumbnail exceeds 2MB", "ограничение"),
                ("Thumbnail response is not a supported JPEG, PNG, GIF, BMP or TIFF image.", "повреждённое"),
                ("No ffmpeg or ffprobe binary in package.", "В скачанном архиве"),
                ("ffmpeg.exe not found", "FFmpeg"),
                ("Cannot start yt-dlp", "запустить"),
                ("yt-dlp exit code 1", "yt-dlp завершился"),
                ("HTTP Error 503: Service Unavailable | ERROR: HTTP Error 403: Forbidden | connection timed out", "403")
            };
            foreach(var (diagnostic,expected) in cases)
            {
                string result=ErrorMessages.ForDisplay(diagnostic);
                Require(result.Contains(expected,StringComparison.OrdinalIgnoreCase) && result!=diagnostic,
                    "Russian error explanation should identify the cause: "+diagnostic+" / "+result);
            }

            const string unknown="ERROR: extractor returned an undocumented result";
            string fallback=ErrorMessages.ForDisplay(unknown);
            Require(fallback.Contains("Исходная диагностика",StringComparison.Ordinal) && !fallback.Contains("extractor"),
                "Unrecognized text needs an honest Russian fallback, not a fabricated specific cause");
            string idOnly=ErrorMessages.ForDisplay("ERROR: https://example.invalid/video/403?note=timeout&reason=private-video unrecognized extractor response");
            Require(idOnly==fallback,"URL IDs and query text are data, not HTTP/content/network diagnostics");
            Require(ErrorMessages.ForDisplay("ERROR: [youtube] video503: HTTP Error 403: Forbidden")
                    .Contains("403",StringComparison.Ordinal),
                "A numeric video identifier must not replace the actual HTTP status");
            const string russian="Файл подписок повреждён или имеет неизвестный формат. Данные не изменены.";
            Require(ErrorMessages.ForDisplay(russian)==russian,"Useful existing Russian errors must be preserved");
            const string russianWithTool="Не удалось обработать файл. FFmpeg недоступен для этой операции.";
            Require(ErrorMessages.ForDisplay(russianWithTool)==russianWithTool,
                "A Latin tool name in Russian prose must not turn that prose into an unknown English cause");
            string scan=ErrorMessages.ForDisplay("Полный обход yt-dlp не завершён: ERROR: HTTP Error 403: Forbidden");
            Require(scan.StartsWith("Полный обход yt-dlp не завершён: ",StringComparison.Ordinal) && scan.Contains("403") && !scan.Contains("Forbidden"),
                "Scan failure must retain the full-enumeration context while translating the cause");
            string feed=ErrorMessages.ForDisplay("Лента недоступна; использован полный обход yt-dlp. Response status code does not indicate success: 404 (Not Found).");
            Require(feed.StartsWith("Лента недоступна; использован полный обход yt-dlp. ",StringComparison.Ordinal) && feed.Contains("404") && !feed.Contains("Not Found"),
                "RSS warnings must retain successful full-scan fallback semantics");
            string mixedUnknown=ErrorMessages.ForDisplay("Полный обход yt-dlp не завершён: "+unknown);
            Require(mixedUnknown.StartsWith("Полный обход",StringComparison.Ordinal) && mixedUnknown.EndsWith(fallback,StringComparison.Ordinal),
                "Unknown child diagnostics must not discard the Russian operation context");
            string historyWarning=ErrorMessages.ForDisplay("WARNING: Could not append successful URL to history: Access is denied.");
            Require(historyWarning.StartsWith("Файл скачан, но ",StringComparison.Ordinal) && historyWarning.Contains("историю успехов") && historyWarning.Contains("доступа"),
                "A history-write warning must preserve the actual completed download, not imply a failed download");
            string retryWarning=ErrorMessages.ForDisplay("Transient error on https://example.invalid/video, retry 2/3 in 10s: connection timed out");
            Require(retryWarning.StartsWith("Временная ошибка. Повторная попытка 2/3 через 10 с.",StringComparison.Ordinal) && retryWarning.Contains("ожидания"),
                "A retry warning must preserve the actual scheduled attempt and delay");
            string cacheWarning=ErrorMessages.ForDisplay("Preview cache write failed: Access is denied.");
            Require(cacheWarning.StartsWith("Метаданные получены, но ",StringComparison.Ordinal),
                "A cache-write warning must not claim metadata was lost");
            string translatedLog=ErrorMessages.ForLog(denied);
            Require(translatedLog.StartsWith(ErrorMessages.ForDisplay(denied)+Environment.NewLine,StringComparison.Ordinal) &&
                translatedLog.EndsWith("Исходная диагностика: "+denied,StringComparison.Ordinal),
                "Error log entries must show a Russian explanation and preserve the full original diagnostic");
            foreach(string success in new[]{"Session complete: 1 OK, 0 failed.","Downloaded: a video titled failed", "Started 2 URL(s), parallel=4, quality=Best."})
                Require(ErrorMessages.ForLog(success)==success,"Successful events must not become errors based on incidental words: "+success);
            string quality=ErrorMessages.ForLog("Format unavailable at 1080, trying lower quality.");
            Require(quality.StartsWith("Формат качества 1080 недоступен. Пробуем более низкое качество.",StringComparison.Ordinal),
                "A quality fallback is an ongoing operation, not a terminal failed download");

            var typed=new (Exception Error,string Expected)[]
            {
                (new HttpRequestException("Undocumented response",null,HttpStatusCode.Forbidden),"403"),
                (new HttpRequestException("Undocumented network failure"),"сетевой"),
                (new TimeoutException("Undocumented deadline failure"),"ожидания"),
                (new OperationCanceledException("Undocumented cancellation"),"отменена"),
                (new FileNotFoundException("Undocumented missing resource"),"Не найден"),
                (new FileNotFoundException(@"Could not find file 'D:\DNS\video.mp4'."),"Не найден"),
                (new UnauthorizedAccessException(@"Access to the path 'D:\Timeout\video.mp4' is denied."),"доступа"),
                (new DirectoryNotFoundException("Undocumented missing resource"),"Не найден"),
                (new UnauthorizedAccessException("Undocumented access failure"),"доступа"),
                (new PathTooLongException("Undocumented path failure"),"слишком длинный"),
                (new JsonException("Undocumented parse failure"),"Данные повреждены"),
                (new JsonException("DNS is an invalid start of a value"),"Данные повреждены"),
                (new XmlException("Undocumented parse failure"),"Данные повреждены"),
                (new InvalidDataException("Undocumented data failure"),"Данные повреждены"),
                (new IOException("Undocumented storage failure",unchecked((int)0x80070070)),"места"),
                (new IOException(@"D:\DNS\Timeout.mp4",unchecked((int)0x80070070)),"места"),
                (new IOException("Undocumented file failure",unchecked((int)0x80070020)),"занят"),
                (new IOException("Undocumented yt-dlp failure"),"Исходная диагностика"),
                (new Win32Exception(5,"Undocumented OS failure"),"Нет прав"),
                (new ArgumentException("Undocumented option failure"),"параметры"),
                (new Exception("Undocumented failure"),"Исходная диагностика")
            };
            foreach(var (error,expected) in typed)
                Require(ErrorMessages.ForException(error).Contains(expected,StringComparison.OrdinalIgnoreCase),
                    "Exception types must provide useful Russian fallback: "+error.GetType().Name);
            Require(ErrorMessages.ForException(new IOException(russian))==russian,
                "A generic exception type must not overwrite a precise Russian message");
            Require(ErrorMessages.ForDisplay(" \r\n ")=="","Empty diagnostics must not invent an error");
            Console.WriteLine($"Russian error messages: PASS ({cases.Length} diagnostics, {typed.Length} exception cases, UI-culture/fallback/context regressions).");
        }
        finally
        {
            CultureInfo.CurrentUICulture=previousUi;
            CultureInfo.CurrentCulture=previousFormat;
        }
    }

    private static void Require(bool condition,string message)
    {
        if(!condition) throw new InvalidOperationException(message);
    }
}
