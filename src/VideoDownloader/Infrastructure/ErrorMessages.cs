using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace VideoDownloader.Native;

// Presentation only. Never feed these strings back into retry decisions, the
// checkpoint, the subscription store or the thumbnail-cache compatibility keys.
internal static class ErrorMessages
{
    internal static bool IsRussian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
    internal static string Text(string russian,string english) => IsRussian ? russian : english;

    private const string Unknown = "Не удалось выполнить операцию. Исходная диагностика доступна в журнале или подробностях ошибки.";
    private static readonly Regex Url = Pattern(@"https?://[^\s<>]+" );
    private static readonly Regex QuotedPath = Pattern("['\"](?:[A-Za-z]:[\\\\/]|\\\\\\\\)[^'\"\\r\\n]*['\"]");
    private static readonly Regex WindowsPath = Pattern(@"(?:\b[A-Za-z]:[\\/]|\\\\)[^\s""'<>|]*");
    private static readonly Regex RussianStart = Pattern(@"^(?:(?:ERROR|WARNING):\s*)?(?:(?:yt-dlp|FFmpeg|ffprobe)\s+)?[А-Яа-яЁё]");
    private static readonly Regex Context = Pattern(@"^(?<prefix>[А-Яа-яЁё][\s\S]*?[:.!?]\s+)(?<detail>[A-Za-z][\s\S]*)$");
    private static readonly Regex HttpCode = Pattern(@"\b(?:HTTP(?:/\d(?:\.\d)?\s+|\s+(?:Error\s+)?)|(?:response\s+)?status\s+code(?:\s+does\s+not\s+indicate\s+success)?\s*[:=]?\s*)(?<code>[45]\d\d)\b");
    private static readonly Regex HistoryWarning = Pattern(@"^WARNING:\s*Could not append successful URL to history:\s*(?<detail>[\s\S]*)$");
    private static readonly Regex RetryWarning = Pattern(@"^Transient error on \S+, retry (?<attempt>\d+)/(?<total>\d+) in (?<delay>\d+)s:\s*(?<detail>[\s\S]*)$");
    private static readonly Regex ThumbnailWarning = Pattern(@"^(?:Preview thumbnail unavailable|Thumbnail (?:request failed|unavailable|decode failed)):\s*(?<detail>[\s\S]*)$");
    private static readonly Regex PreviewFailure = Pattern(@"^Preview unavailable for \S+:\s+(?<detail>[\s\S]*)$");
    private static readonly Regex CacheWarning = Pattern(@"^Preview cache write failed:\s*(?<detail>[\s\S]*)$");
    private static readonly Regex FormatFallback = Pattern(@"^Format unavailable at (?<quality>\S+), trying lower quality\.$");
    private static readonly Regex PreviewTimeout = Pattern(@"^Preview timed out for \S+\s*$");
    private static readonly Regex ThumbnailTimeout = Pattern(@"^Thumbnail timed out for \S+\s*$");
    private static readonly string[] DiagnosticLogPrefixes =
    [
        "ERROR:", "ERROR ", "WARNING:", "WARNING ", "Failed:",
        "Preview unavailable for ", "Preview timed out for ", "Preview cache write failed:", "Preview thumbnail unavailable:",
        "Thumbnail request failed:", "Thumbnail unavailable:", "Thumbnail decode failed:", "Thumbnail timed out for ",
        "Removal failed for ", "Cannot ", "Dependency operation failed:", "Shutdown cleanup failed:", "Transient error on ", "Format unavailable at "
    ];

    private static Regex Pattern(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private sealed record Rule(Regex Pattern,string Message);
    private static Rule RuleFor(string pattern,string message) => new(Pattern(pattern),message);

    // Specific permanent/content errors precede transient network hints. URLs
    // are excluded from matching so their IDs and query parameters are not
    // mistaken for HTTP codes or an extractor's diagnostic text.
    private static readonly Rule[] BeforeHttp =
    [
        RuleFor(@"\bunsupported URL\b|\bno suitable extractor\b", "Этот адрес не поддерживается yt-dlp. Проверьте ссылку и обновите yt-dlp в разделе Dependencies."),
        RuleFor(@"\bDRM[- ]protected\b|\bprotected (?:with|by) DRM\b|\bDRM protection\b", "Материал защищён DRM. Приложение не может скачать его обычным способом."),
        RuleFor(@"\bnot (?:available|accessible) in your (?:country|region)\b|\bgeo[- ]restrict|\bgeographic restriction", "Материал недоступен в вашем регионе. Проверьте доступность видео в браузере."),
        RuleFor(@"\bconfirm you(?:'|’)re not a bot\b|\bnot a bot\b|\bcaptcha\b", "Сайт требует проверку пользователя. Откройте видео в браузере и при необходимости укажите актуальный файл cookies."),
        RuleFor(@"\bsign in\b|\blog[ -]?in required\b|\bauthentication required\b|\baccount is required\b|\bmembers[- ]only\b", "Для доступа нужен вход в аккаунт. Проверьте видео в браузере и укажите актуальный файл cookies."),
        RuleFor(@"\bprivate video\b|\bprivate (?:playlist|collection)\b", "Это закрытый материал. Убедитесь, что вашему аккаунту доступно видео, и укажите актуальный файл cookies."),
        RuleFor(@"\bage[- ]restrict|\bconfirm your age\b", "Материал имеет возрастное ограничение. Проверьте доступ в браузере и укажите cookies аккаунта с подтверждённым возрастом."),
        RuleFor(@"\bvideo has been removed\b|\bvideo (?:has been )?deleted\b|\bremoved by (?:the )?uploader\b", "Видео удалено с сайта. Проверьте исходную ссылку."),
        RuleFor(@"\brequested format is not available\b|\bno suitable quality available\b", "Выбранный формат или качество недоступны. Выберите другое качество, кодек или профиль и повторите загрузку."),
        RuleFor(@"\bvideo unavailable\b|\bthis video is unavailable\b", "Видео недоступно. Проверьте исходную ссылку и доступ к ней в браузере."),
        RuleFor(@"\bfilename template must be relative\b", "Шаблон имени должен задавать относительный путь без «..», буквы диска и начального разделителя папок."),
        RuleFor(@"\bno ownership manifest\b", "Нет списка файлов, принадлежащих этой загрузке. Автоматическое удаление заблокировано; удалите нужные файлы вручную."),
        RuleFor(@"\brefusing deletion outside download folder\b|\b(?:stage|destination) (?:path )?escaped its folder\b|\bunknown staging directory\b", "Операция с файлами за пределами известной папки загрузки заблокирована. Проверьте папку назначения и исходную диагностику."),
        RuleFor(@"\b(?:reparse point|symlink|junction)\b", "Операция с файлами заблокирована из-за ссылки или перенаправления папки. Выберите обычную папку назначения."),
        RuleFor(@"\brefusing to overwrite\b|\bcannot find a free output name\b", "Не удалось выбрать свободное имя файла. Проверьте содержимое папки назначения; существующие файлы сохранены."),
        RuleFor(@"\bfinished without reporting a downloaded media file\b|\bexpected downloaded media path not found\b", "yt-dlp не предоставил готовый медиафайл. Загрузка не отмечена успешной; обновите yt-dlp и проверьте исходную диагностику."),
        RuleFor(@"\bdid not produce the requested container\b", "Не удалось получить выбранный контейнер. Проверьте FFmpeg или выберите другой контейнер и повторите загрузку."),
        RuleFor(@"\bdamaged download history\b", "История скачиваний повреждена. Операция заблокирована для защиты записей; исходный файл сохранён. Проверьте диагностику и резервную копию."),
        RuleFor(@"\b(?:invalid|empty) session checkpoint\b|\binvalid or duplicate checkpoint\b|\bsession checkpoint exceeds\b|\binvalid subscription folder in session checkpoint\b", "Сохранённая сессия повреждена или имеет неподдерживаемый формат. Исходный файл сохранён; проверьте диагностику и резервную копию."),
        RuleFor(@"\bcannot acquire download-history lock\b", "Файл истории занят другой операцией. Дождитесь её завершения и повторите действие."),
        RuleFor(@"\bno JPEG/PNG thumbnail was provided\b|\bno (?:usable )?thumbnail\b", "yt-dlp не предоставил подходящую обложку. Текстовые метаданные могут быть доступны без изображения."),
        RuleFor(@"\b(?:WebP|AVIF)[\s\S]*thumbnail (?:needs|requires) FFmpeg\b", "Для этой обложки нужен FFmpeg. Установите его в разделе Dependencies; текстовые метаданные доступны без изображения."),
        RuleFor(@"\bFFmpeg could not (?:convert|start for thumbnail conversion)\b", "Не удалось преобразовать обложку с помощью FFmpeg. Проверьте FFmpeg; текстовые метаданные доступны без изображения."),
        RuleFor(@"\b(?:thumbnail|image)[\s\S]*(?:exceeds|dimensions limit)|\binvalid thumbnail size\b", "Размер обложки превышает ограничение предпросмотра. Текстовые метаданные доступны без изображения."),
        RuleFor(@"\bthumbnail (?:response is not|image is damaged|has no image frame)\b", "Сайт вернул неподдерживаемое или повреждённое изображение обложки. Текстовые метаданные доступны без изображения."),
        RuleFor(@"\bno ffmpeg or ffprobe binary in package\b", "В скачанном архиве не найдены FFmpeg и ffprobe. Повторите установку или установите их вручную."),
        RuleFor(@"\b(?:ffmpeg|ffprobe)(?:\.exe)?[\s\S]*(?:not found|missing|not installed)\b", "Не найдены FFmpeg или ffprobe. Установите FFmpeg в разделе Dependencies и повторите действие."),
        RuleFor(@"\b(?:yt-dlp|ffmpeg|ffprobe)(?:\.exe)?[\s\S]*(?:cannot start|could not start)\b|\bcannot start yt-dlp\b", "Не удалось запустить инструмент обработки медиа. Проверьте наличие yt-dlp, FFmpeg и ffprobe и права на запуск."),
        RuleFor(@"\bno space left on device\b|\bnot enough space on (?:the )?disk\b|\bdisk (?:is )?full\b", "На диске недостаточно свободного места. Освободите место или выберите другую папку назначения."),
        RuleFor(@"\bpermission denied\b|\baccess (?:is )?denied\b|\baccess to (?:the )?path[^\r\n]*? is denied\b", "Нет доступа к файлу или папке. Проверьте права доступа и выберите доступную папку назначения."),
        RuleFor(@"\bbeing used by another process\b|\bsharing violation\b", "Файл занят другим приложением. Закройте его и повторите действие."),
        RuleFor(@"\b(?:original (?:output folder|staging root) is unavailable|stage absent|staging path is not a directory|not a folder)\b", "Рабочая папка загрузки недоступна. Проверьте наличие и доступность исходной папки перед повтором операции.")
    ];

    private static readonly Rule[] AfterHttp =
    [
        RuleFor(@"\bcertificate verify failed\b|\bcertificate (?:is )?(?:invalid|expired)\b|\bSSL\b|\bTLS\b", "Не удалось установить защищённое соединение. Проверьте дату и время Windows, сертификаты и настройки сети."),
        RuleFor(@"\b(?:timed? out|timeout)\b", "Превышено время ожидания ответа. Проверьте сеть и доступность сайта, затем повторите действие."),
        RuleFor(@"\b(?:name or service not known|name resolution|no such host is known|nodename nor servname|DNS)\b", "Не удалось определить адрес сайта. Проверьте подключение к интернету и настройки DNS."),
        RuleFor(@"\bconnection (?:reset|aborted|refused|closed)|\bnetwork is unreachable\b|\bremote host\b|\btemporary failure\b|\btemporarily unavailable\b", "Не удалось подключиться к сайту. Проверьте сеть и доступность сайта, затем повторите действие."),
        RuleFor(@"\b(?:could not|cannot) find (?:file|a part of the path)\b|\b(?:file|directory) not found\b|\bno such file or directory\b", "Не найден нужный файл или папка. Проверьте указанный путь и наличие необходимых файлов."),
        RuleFor(@"\b(?:filename|directory name|volume label) syntax is incorrect\b|\bpath is too long\b|\billegal characters in path\b", "Недопустимое имя файла или путь. Сократите путь и проверьте шаблон имени и папку назначения."),
        RuleFor(@"\byt-dlp metadata request failed\b|\byt-dlp exit code\s+\d+\b", "yt-dlp завершился с ошибкой. Обновите yt-dlp, проверьте ссылку в браузере и исходную диагностику.")
    ];

    internal static string ForDisplay(string? diagnostic)
    {
        if (!IsRussian) return diagnostic ?? "";
        if (string.IsNullOrWhiteSpace(diagnostic)) return "";
        return ForRussian(diagnostic.Trim(),0);
    }

    private static string ForRussian(string raw,int depth)
    {
        if (depth>4) return Unknown;
        string Detail(Match match) => ForRussian(match.Groups["detail"].Value.Trim(),depth+1);
        var history=HistoryWarning.Match(raw);
        if(history.Success) return "Файл скачан, но не удалось записать URL в историю успехов. "+Detail(history);
        var retry=RetryWarning.Match(raw);
        if(retry.Success) return $"Временная ошибка. Повторная попытка {retry.Groups["attempt"].Value}/{retry.Groups["total"].Value} через {retry.Groups["delay"].Value} с. "+Detail(retry);
        var thumbnail=ThumbnailWarning.Match(raw);
        if(thumbnail.Success) return "Обложка недоступна. "+Detail(thumbnail);
        var preview=PreviewFailure.Match(raw);
        if(preview.Success) return "Не удалось получить метаданные. "+Detail(preview);
        var cache=CacheWarning.Match(raw);
        if(cache.Success) return "Метаданные получены, но не удалось сохранить кеш превью. "+Detail(cache);
        var format=FormatFallback.Match(raw);
        if(format.Success) return $"Формат качества {format.Groups["quality"].Value} недоступен. Пробуем более низкое качество.";
        if(PreviewTimeout.IsMatch(raw)) return "Истекло время получения метаданных. Проверьте доступность сайта и повторите действие.";
        if(ThumbnailTimeout.IsMatch(raw)) return "Истекло время получения обложки. Текстовые метаданные могут быть доступны без изображения.";
        if (RussianStart.IsMatch(raw))
        {
            // A scanner/RSS message can carry an English child/HTTP diagnostic.
            // Keep the truthful context (e.g. full scan fallback), not just cause.
            var context=Context.Match(raw);
            if (context.Success)
                return context.Groups["prefix"].Value + ForRussian(context.Groups["detail"].Value,depth+1);
            return raw;
        }
        return Translate(raw);
    }

    internal static string ForLog(string message)
    {
        // Match events emitted by this application, not arbitrary occurrences
        // of words such as "failed" in a successful session summary or title.
        if(!IsRussian || !DiagnosticLogPrefixes.Any(prefix=>message.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))) return message;
        string summary=ForDisplay(message);
        return summary==message ? message : summary+Environment.NewLine+"Исходная диагностика: "+message;
    }

    private static string Translate(string raw)
    {
        string searchable=WindowsPath.Replace(QuotedPath.Replace(Url.Replace(raw,""),""),"");
        foreach(var rule in BeforeHttp)
            if (rule.Pattern.IsMatch(searchable)) return rule.Message;
        var codes=HttpCode.Matches(searchable).Select(match=>int.Parse(match.Groups["code"].Value,CultureInfo.InvariantCulture)).ToArray();
        int code=codes.FirstOrDefault(code=>code is 400 or 401 or 403 or 404 or 405 or 410 or 451);
        if(code==0) code=codes.FirstOrDefault();
        if(code!=0) return HttpMessage(code);
        foreach(var rule in AfterHttp)
            if(rule.Pattern.IsMatch(searchable)) return rule.Message;
        return Unknown;
    }

    private static string HttpMessage(int code) => code switch
    {
        400 => "Сайт отклонил запрос (HTTP 400). Проверьте ссылку и обновите yt-dlp.",
        401 => "Сайт требует авторизацию (HTTP 401). Проверьте доступ в браузере и укажите актуальный файл cookies.",
        403 => "Сайт запретил доступ (HTTP 403). Проверьте доступность видео в браузере, cookies и актуальность yt-dlp.",
        404 or 410 => $"Материал не найден или удалён (HTTP {code}). Проверьте исходную ссылку.",
        405 => "Сайт отклонил способ запроса (HTTP 405). Обновите yt-dlp и проверьте ссылку в браузере.",
        429 => "Сайт ограничил частоту запросов (HTTP 429). Подождите и уменьшите число одновременных загрузок.",
        451 => "Доступ к материалу ограничен по юридическим причинам (HTTP 451). Проверьте доступность ссылки в браузере.",
        >=500 and <=599 => $"Ошибка сервера сайта (HTTP {code}). Повторите действие позже.",
        _ => $"Сайт вернул ошибку HTTP {code}. Проверьте ссылку в браузере и исходную диагностику."
    };

    internal static string ForException(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if(!IsRussian) return error.Message;
        // Structured OS/HTTP information is stronger evidence than words that
        // can occur in a user-controlled filename, e.g. D:\DNS\Timeout.mp4.
        if(RussianStart.IsMatch(error.Message.Trim())) return ForDisplay(error.Message);
        string? precise=error switch
        {
            HttpRequestException { StatusCode: { } code } => HttpMessage((int)code),
            UnauthorizedAccessException => "Нет доступа к файлу или папке. Проверьте права доступа и выберите доступную папку назначения.",
            FileNotFoundException or DirectoryNotFoundException => "Не найден нужный файл или папка. Проверьте указанный путь и наличие необходимых файлов.",
            PathTooLongException => "Путь к файлу слишком длинный. Сократите имя или выберите другую папку назначения.",
            JsonException or XmlException => "Данные повреждены или имеют неподдерживаемый формат. Проверьте исходную диагностику и резервную копию.",
            IOException io when (io.HResult & 0xffff) is 0x27 or 0x70 => "На диске недостаточно свободного места. Освободите место или выберите другую папку назначения.",
            IOException io when (io.HResult & 0xffff) is 0x20 or 0x21 => "Файл занят другим приложением. Закройте его и повторите действие.",
            Win32Exception { NativeErrorCode: 2 or 3 } => "Не найден нужный файл или папка. Проверьте указанный путь и наличие необходимых файлов.",
            Win32Exception { NativeErrorCode: 5 } => "Нет прав на выполнение операции. Проверьте доступ к файлу или папке.",
            Win32Exception { NativeErrorCode: 112 } => "На диске недостаточно свободного места. Освободите место или выберите другую папку назначения.",
            _ => null
        };
        if(precise!=null) return precise;
        string display=ForDisplay(error.Message);
        if(display.Length>0 && display!=Unknown) return display;
        return error switch
        {
            TimeoutException => "Превышено время ожидания операции. Проверьте доступность сайта или файла и повторите действие.",
            OperationCanceledException => "Операция отменена.",
            InvalidDataException => "Данные повреждены или имеют неподдерживаемый формат. Проверьте исходную диагностику и резервную копию.",
            // IOException also wraps arbitrary yt-dlp stderr. Its type alone
            // does not establish a filesystem failure.
            IOException => Unknown,
            HttpRequestException => "Не удалось выполнить сетевой запрос. Проверьте подключение к интернету и доступность сайта.",
            ArgumentException => "Некорректные параметры операции. Проверьте ссылку, пути и выбранные настройки.",
            _ => Unknown
        };
    }
}
