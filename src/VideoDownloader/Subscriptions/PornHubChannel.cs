using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

/// <summary>Versioned discovery manifest from the packaged yt-dlp collector.</summary>
public static class PornHubChannel
{
    private static readonly Regex Host=new(@"^(?:[a-z0-9-]+\.)?pornhub(?:premium)?\.(?:com|net|org)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex PathPattern=new(@"^/(?<kind>channels|model|pornstar|users)/(?<slug>[A-Za-z0-9_-]+)(?:/videos(?:/upload)?)?/?$",RegexOptions.CultureInvariant);
    public static string? ListingUrl(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var source) || source.Scheme is not ("http" or "https") ||
            source.UserInfo.Length!=0 || !source.IsDefaultPort || !Host.IsMatch(source.Host))return null;
        var match=PathPattern.Match(source.AbsolutePath);
        if(!match.Success)return null;
        string kind=match.Groups["kind"].Value;
        return $"https://{source.Host.ToLowerInvariant()}/{kind}/{match.Groups["slug"].Value}/videos"+
            (kind=="channels"?"":"/upload");
    }
    public static string PluginDirectory=>System.IO.Path.Combine(AppContext.BaseDirectory,"collection-plugins");
    public static void RequireCollector()
    {
        string file=System.IO.Path.Combine(PluginDirectory,"videodownloader","yt_dlp_plugins","extractor","vd_pornhub.py");
        if(!File.Exists(file))throw new FileNotFoundException("Не найден обработчик каналов Pornhub. Распакуйте полный архив приложения.",file);
    }
    public static SubscriptionScan ParseScan(string json,string sourceUrl,int maxItems=10_000,int maxPages=500)
        => ParseManifest(json,sourceUrl,maxItems,maxPages,null);

    public static SubscriptionScan ParseRecentScan(string json,string sourceUrl,int window=20,int maxPages=500)
    {
        if(window is <1 or >20)throw new ArgumentOutOfRangeException(nameof(window));
        return ParseManifest(json,sourceUrl,window,Math.Min(maxPages,window),window);
    }

    private static SubscriptionScan ParseManifest(string json,string sourceUrl,int maxItems,int maxPages,int? recentWindow)
    {
        string expected=ListingUrl(sourceUrl)??throw new InvalidDataException("Некорректный URL канала Pornhub.");
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement;
        try
        {
            var proof=root.GetProperty(recentWindow.HasValue?"vd_ph_recent":"vd_ph_scan");
            if(proof.GetProperty("version").GetInt32()!=1 || proof.GetProperty("complete").GetBoolean()==recentWindow.HasValue ||
                !proof.GetProperty("verified_first_page").GetBoolean() ||
                proof.GetProperty("source_url").GetString()!=sourceUrl || proof.GetProperty("listing_url").GetString()!=expected)
                throw new InvalidDataException("Полнота списка публикаций Pornhub не подтверждена.");
            if(recentWindow.HasValue && (root.TryGetProperty("vd_ph_scan",out _) ||
                proof.GetProperty("scope").GetString()!="recent" || proof.GetProperty("order").GetString()!="most_recent" ||
                proof.GetProperty("window_size").GetInt32()!=recentWindow.Value))
                throw new InvalidDataException("Порядок новых публикаций Pornhub не подтверждён.");
            var entries=root.GetProperty("entries");
            var pages=proof.GetProperty("pages");
            int count=proof.GetProperty("item_count").GetInt32();
            if(entries.ValueKind!=JsonValueKind.Array || pages.ValueKind!=JsonValueKind.Array || count<0 || count>maxItems ||
                count!=entries.GetArrayLength() || pages.GetArrayLength()<1 || pages.GetArrayLength()>maxPages)
                throw new InvalidDataException("Список публикаций Pornhub обрезан или превышает предел.");
            var pageCounts=new int[pages.GetArrayLength()];
            int index=0;
            foreach(var page in pages.EnumerateArray())
            {
                int number=page.GetProperty("number").GetInt32(), size=page.GetProperty("count").GetInt32();
                if(number!=index+1 || size<0 || (size==0 && (count!=0 || pageCounts.Length!=1)) ||
                    (number<pageCounts.Length && !page.GetProperty("has_next").GetBoolean()) ||
                    (!recentWindow.HasValue && number==pageCounts.Length && page.GetProperty("has_next").GetBoolean()))
                    throw new InvalidDataException("Нарушена последовательность страниц Pornhub.");
                pageCounts[index++]=size;
            }
            if(recentWindow.HasValue && count<recentWindow.Value && pages[pages.GetArrayLength()-1].GetProperty("has_next").GetBoolean())
                throw new InvalidDataException("Окно новых публикаций Pornhub обрезано до достижения лимита.");
            string host=Regex.Match(new Uri(sourceUrl).Host,@"pornhub(?:premium)?\.(?:com|net|org)$",RegexOptions.IgnoreCase).Value.ToLowerInvariant();
            var seen=new HashSet<string>(StringComparer.Ordinal);
            var items=new List<DiscoveredMedia>();
            foreach(var entry in entries.EnumerateArray())
            {
                string id=entry.GetProperty("id").GetString()??"";
                string url=entry.GetProperty("url").GetString()??"";
                int number=entry.GetProperty("vd_page").GetInt32();
                if(entry.GetProperty("_type").GetString()!="url" || entry.GetProperty("ie_key").GetString()!="PornHub" ||
                    !Regex.IsMatch(id,@"^[a-z0-9]+$",RegexOptions.CultureInvariant) ||
                    url!=$"https://www.{host}/view_video.php?viewkey={id}" || !seen.Add(url) ||
                    number<1 || number>pageCounts.Length || --pageCounts[number-1]<0)
                    throw new InvalidDataException("В список Pornhub попал повторный или посторонний материал.");
                string title=entry.TryGetProperty("title",out var name)&&name.ValueKind==JsonValueKind.String?name.GetString()??"":"";
                items.Add(new(url,title.Length>0?title:url));
            }
            if(pageCounts.Any(n=>n!=0))throw new InvalidDataException("Количество материалов на страницах Pornhub не совпадает.");
            string caption=root.TryGetProperty("title",out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
            string suggestion = new[] { "channel", "uploader", "title" }
                .Select(name => root.TryGetProperty(name, out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() ?? "" : "")
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
            return new(caption.Length>0?caption:sourceUrl,items,"","",suggestion)
                { Scope=recentWindow.HasValue?SubscriptionScanScope.RecentWindow:SubscriptionScanScope.Full };
        }
        catch(Exception error) when(error is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {throw new InvalidDataException("Некорректный ответ обработчика каналов Pornhub; прежние результаты сохранены.",error);}
    }
}
