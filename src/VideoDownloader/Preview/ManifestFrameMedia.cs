using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VideoDownloader.Native;

internal sealed record PreparedManifestMedia(byte[]? Bytes,string Demuxer,string ContentType,double StartSeconds,
    long DownloadedBytes,Uri? DirectAddress=null,double? EndSeconds=null);

/// <summary>
/// Resolves manifests inside the application and materializes only the small
/// binary window needed by a preview. FFmpeg never receives a remote manifest.
/// </summary>
internal static partial class ManifestFrameMedia
{
    internal const int MaxManifestBytes=1024*1024;
    internal const int MaxSegments=10000;
    private sealed record Resource(Uri Address,long? First=null,long? Last=null);
    private sealed record Encryption(Resource Key,byte[]? Iv,long Sequence);
    private sealed record Segment(Resource Resource,double Start,double Duration,Resource? Init=null,
        Encryption? Encryption=null,int Group=0);
    private sealed record Plan(List<Segment> Segments,string? Container=null,Uri? Direct=null,double DirectStart=0);

    internal static async Task<PreparedManifestMedia> PrepareAsync(MediaFrameSource source,double seconds,
        CookieContainer cookies,CancellationToken token,long maxBytes=MediaFrameService.MaxMediaBytes,double? endSeconds=null)
    {
        using var fetch=new Fetcher(source,cookies,token,maxBytes);
        Plan plan;
        if(source.IsHls)plan=await HlsAsync(fetch,source.ManifestAddress??source.Address,seconds,0,new HashSet<string>(StringComparer.Ordinal)).ConfigureAwait(false);
        else if(source.Fragments is {Count:>0} fragments)
        {
            var segments=new List<Segment>();Resource? init=null;double at=0;
            foreach(var fragment in fragments)
            {
                var resource=new Resource(fragment.Address,fragment.RangeStart,fragment.RangeEnd);
                if(fragment.Duration is not {} duration){init=resource;continue;}
                segments.Add(new(resource,at,duration,init));at=CheckedTime(at+duration);
            }
            plan=new(segments,SourceContainer(source));
        }
        else
        {
            var manifest=await fetch.GetAsync(new(source.ManifestAddress??source.Address),MaxManifestBytes).ConfigureAwait(false);
            plan=Dash(manifest.Bytes,manifest.Address,source,seconds,endSeconds);
        }
        if(plan.Direct is {} direct)
            return new(null,"mov","video/mp4",plan.DirectStart,fetch.ReceivedBytes,direct);
        if(plan.Segments.Count==0)throw Unsupported("В манифесте нет видеосегментов для кадра.");
        int target=plan.Segments.FindIndex(segment=>seconds>=segment.Start && seconds<segment.Start+segment.Duration);
        if(target<0 && seconds>=plan.Segments[^1].Start && seconds<=plan.Segments[^1].Start+plan.Segments[^1].Duration+.001)
            target=plan.Segments.Count-1;
        if(target<0)throw Unsupported("Выбранное время не попало в доступные сегменты видео.");
        var selected=plan.Segments[target];
        int first=target,last=Math.Min(plan.Segments.Count-1,target+1);
        if(endSeconds is {} end)
        {
            int final=plan.Segments.FindIndex(segment=>end-.0001>=segment.Start && end-.0001<segment.Start+segment.Duration);
            if(final<target)throw Unsupported("Фрагмент выходит за доступные сегменты этого периода.");
            last=final;
            for(int index=target;index<=last;index++)
                if(plan.Segments[index].Group!=selected.Group || plan.Segments[index].Init!=selected.Init ||
                    index>target && Math.Abs(plan.Segments[index].Start-plan.Segments[index-1].Start-plan.Segments[index-1].Duration)>.01)
                    throw Unsupported("Предпросмотр не может пересекать разрыв временной шкалы.");
        }
        while(first>0 && target-first<2 && plan.Segments[first-1].Group==selected.Group && plan.Segments[first-1].Init==selected.Init)first--;
        if(plan.Segments[last].Group!=selected.Group || plan.Segments[last].Init!=selected.Init)last=target;
        using var output=new MemoryStream();
        var keys=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        string? container=plan.Container;
        try
        {
        if(selected.Init is {} initialization)
        {
            var initialized=await fetch.GetAsync(initialization,(int)Math.Min(maxBytes,int.MaxValue)).ConfigureAwait(false);
            container??=DetectContainer(initialized.Bytes);
            EnsureBinary(initialized.Bytes,container,initialization:true);
            output.Write(initialized.Bytes);
        }
        for(int index=first;index<=last;index++)
        {
            token.ThrowIfCancellationRequested();
            var segment=plan.Segments[index];
            var fetched=await fetch.GetAsync(segment.Resource,(int)Math.Min(maxBytes,int.MaxValue)).ConfigureAwait(false);
            byte[] bytes=fetched.Bytes;
            if(segment.Encryption is {} encryption)
            {
                if(!keys.TryGetValue(encryption.Key.Address.AbsoluteUri,out var key))
                {
                    var fetchedKey=await fetch.GetAsync(encryption.Key,16).ConfigureAwait(false);
                    if(fetchedKey.Bytes.Length!=16)throw Unsupported("Некорректный ключ HLS AES-128.");
                    keys[encryption.Key.Address.AbsoluteUri]=key=fetchedKey.Bytes;
                }
                byte[] iv=encryption.Iv??SequenceIv(encryption.Sequence);
                try
                {
                    using var aes=Aes.Create();aes.Key=key;aes.IV=iv;aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;
                    bytes=aes.DecryptCbc(bytes,iv,PaddingMode.PKCS7);
                }
                catch(CryptographicException){throw Unsupported("Не удалось расшифровать сегмент HLS AES-128.");}
            }
            container??=DetectContainer(bytes);
            EnsureBinary(bytes,container,initialization:false);
            if(output.Length+bytes.Length>maxBytes)throw Unsupported("Сегменты кадра превышают ограничение объёма.");
            output.Write(bytes);
        }
        return new(output.ToArray(),container!,container=="mpegts"?"video/mp2t":container=="matroska"?"video/webm":container=="aac"?"audio/aac":"video/mp4",
            plan.Segments[first].Start,fetch.ReceivedBytes,EndSeconds:plan.Segments[last].Start+plan.Segments[last].Duration);
        }
        finally{foreach(var key in keys.Values)CryptographicOperations.ZeroMemory(key);}
    }

    private static string SourceContainer(MediaFrameSource source)=>source.Extension=="webm" || source.Address.AbsolutePath.EndsWith(".webm",StringComparison.OrdinalIgnoreCase)?"matroska":"mov";
    private static string DetectContainer(byte[] bytes)
    {
        if(IsTransportStream(bytes))return "mpegts";
        if(IsAac(bytes))return "aac";
        if(bytes.Length>=4 && bytes.AsSpan(0,4).SequenceEqual(new byte[]{0x1a,0x45,0xdf,0xa3}))return "matroska";
        if(bytes.Length>=8 && new[]{"ftyp","styp","moof","sidx","emsg"}.Any(atom=>bytes.AsSpan(4,4).SequenceEqual(Encoding.ASCII.GetBytes(atom))))return "mov";
        throw Unsupported("Сегмент не содержит поддерживаемое двоичное видео.");
    }
    private static void EnsureBinary(byte[] bytes,string container,bool initialization)
    {
        if(bytes.Length<4)throw Unsupported("Пустой или повреждённый видеосегмент.");
        bool valid=container switch {
            "mpegts"=>IsTransportStream(bytes),
            "aac"=>IsAac(bytes),
            "mov"=>bytes.Length>=8 && new[]{"ftyp","styp","moof","moov","sidx","emsg","free"}.Any(atom=>bytes.AsSpan(4,4).SequenceEqual(Encoding.ASCII.GetBytes(atom))),
            "matroska"=>bytes.AsSpan(0,4).SequenceEqual(initialization?new byte[]{0x1a,0x45,0xdf,0xa3}:new byte[]{0x1f,0x43,0xb6,0x75}) ||
                bytes.AsSpan(0,4).SequenceEqual(new byte[]{0x1a,0x45,0xdf,0xa3}),
            _=>false};
        if(!valid)throw Unsupported("Вместо видеосегмента источник вернул неподдерживаемые данные.");
    }
    private static bool IsAac(byte[] bytes)=>bytes.Length>=3 &&
        (bytes[0]==0xff && (bytes[1]&0xf6)==0xf0 || bytes.AsSpan(0,3).SequenceEqual("ID3"u8));
    private static bool IsTransportStream(byte[] bytes)
    {
        for(int offset=0;offset<=Math.Min(8192,bytes.Length-188);offset++)
            if(bytes[offset]==0x47 && (bytes.Length-offset<376 || bytes[offset+188]==0x47))return true;
        return false;
    }
    private static MediaFrameUnavailableException Unsupported(string message)=>new(message);
    private static double CheckedTime(double value)
    {if(!double.IsFinite(value) || value<0 || value>=TimeSpan.MaxValue.TotalSeconds)throw Unsupported("Некорректная временная шкала манифеста.");return value;}
    private static byte[] SequenceIv(long sequence)
    {var bytes=new byte[16];System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(8),sequence);return bytes;}

    private static async Task<Plan> HlsAsync(Fetcher fetch,Uri address,double seconds,int depth,HashSet<string> visited)
    {
        if(depth>3 || !visited.Add(address.AbsoluteUri))throw Unsupported("HLS содержит слишком много вложений или повтор манифеста.");
        var file=await fetch.GetAsync(new(address),MaxManifestBytes).ConfigureAwait(false);
        string text;
        try{text=new UTF8Encoding(false,true).GetString(file.Bytes).TrimStart('\uFEFF');}
        catch(DecoderFallbackException){throw Unsupported("Некорректная кодировка HLS.");}
        string[] lines=text.Replace("\r","").Split('\n').Select(line=>line.Trim()).ToArray();
        if(lines.Length>30000 || lines.Any(line=>line.Length>16384) || lines.FirstOrDefault(line=>line.Length>0)!="#EXTM3U")throw Unsupported("Некорректный HLS-манифест.");
        var variants=new List<(Uri Address,long Rank)>();
        for(int index=0;index<lines.Length;index++)
            if(lines[index].StartsWith("#EXT-X-STREAM-INF:",StringComparison.Ordinal))
            {
                var attributes=Attributes(lines[index][18..]);
                string next=lines.Skip(index+1).FirstOrDefault(line=>line.Length>0 && !line.StartsWith('#'))??"";
                if(next.Length==0)throw Unsupported("В HLS отсутствует адрес варианта видео.");
                if(attributes.GetValueOrDefault("CODECS","").Split(',').All(codec=>codec.Trim().StartsWith("mp4a",StringComparison.Ordinal)) && !attributes.ContainsKey("RESOLUTION"))continue;
                int height=0;
                var resolution=attributes.GetValueOrDefault("RESOLUTION","").Split('x');
                if(resolution.Length==2)int.TryParse(resolution[1],NumberStyles.None,CultureInfo.InvariantCulture,out height);
                variants.Add((Resolve(file.Address,next),(height is >=240 and <=720?0L:1000000)+Math.Abs((height>0?height:10000)-360)*1000L));
            }
        if(variants.Count>0)return await HlsAsync(fetch,variants.MinBy(variant=>variant.Rank).Address,seconds,depth+1,visited).ConfigureAwait(false);
        if(!lines.Contains("#EXT-X-ENDLIST",StringComparer.Ordinal))throw Unsupported("Предпросмотр кадров доступен для завершённого HLS, а не live-потока.");
        var result=new List<Segment>();double at=0;double? duration=null;Resource? init=null;
        long sequence=0;string? keyAddress=null;byte[]? iv=null;int group=0;
        (long Length,long? Offset)? pendingRange=null;
        long? previousEnd=null;Uri? previousAddress=null;
        foreach(string line in lines)
        {
            if(line.Length==0)continue;
            if(line.StartsWith("#EXTINF:",StringComparison.Ordinal))
            {
                string number=line[8..].Split(',')[0];
                if(!double.TryParse(number,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var parsed) || !double.IsFinite(parsed) || parsed<=0)throw Unsupported("Некорректная длительность HLS-сегмента.");
                duration=parsed;
            }
            else if(line.StartsWith("#EXT-X-MEDIA-SEQUENCE:",StringComparison.Ordinal))
            {if(!long.TryParse(line[22..],NumberStyles.None,CultureInfo.InvariantCulture,out sequence) || sequence<0)throw Unsupported("Некорректный номер HLS-сегмента.");}
            else if(line=="#EXT-X-DISCONTINUITY")group++;
            else if(line.StartsWith("#EXT-X-MAP:",StringComparison.Ordinal))
            {
                var map=Attributes(line[11..]);
                if(!map.TryGetValue("URI",out string? value))throw Unsupported("HLS init-сегмент не содержит адрес.");
                var resource=new Resource(Resolve(file.Address,value));
                if(map.TryGetValue("BYTERANGE",out var range))
                {
                    var parsed=ByteRange(range);if(parsed.Offset is null)throw Unsupported("HLS init-сегмент требует явного смещения.");
                    resource=resource with{First=parsed.Offset,Last=checked(parsed.Offset.Value+parsed.Length-1)};
                }
                if(init!=resource)group++;
                init=resource;
            }
            else if(line.StartsWith("#EXT-X-BYTERANGE:",StringComparison.Ordinal))pendingRange=ByteRange(line[17..]);
            else if(line.StartsWith("#EXT-X-KEY:",StringComparison.Ordinal))
            {
                var key=Attributes(line[11..]);
                string method=key.GetValueOrDefault("METHOD","");
                if(method=="NONE"){keyAddress=null;iv=null;continue;}
                if(method!="AES-128" || key.GetValueOrDefault("KEYFORMAT","identity")!="identity" || !key.TryGetValue("URI",out keyAddress))throw Unsupported("Защищённый HLS не поддерживается для кадров.");
                iv=null;
                if(key.TryGetValue("IV",out var hex))
                {
                    if(!Regex.IsMatch(hex,@"\A0[xX][0-9a-fA-F]{1,32}\z",RegexOptions.CultureInvariant))throw Unsupported("Некорректный IV HLS.");
                    iv=Convert.FromHexString(hex[2..].PadLeft(32,'0'));
                }
            }
            else if(!line.StartsWith('#'))
            {
                if(duration is null)continue;
                var uri=Resolve(file.Address,line);Resource resource=new(uri);
                if(pendingRange is {} range)
                {
                    long start=range.Offset??(previousAddress==uri && previousEnd is {} end?checked(end+1):throw Unsupported("Неизвестное смещение HLS byte range."));
                    resource=resource with{First=start,Last=checked(start+range.Length-1)};
                    previousEnd=resource.Last;previousAddress=uri;pendingRange=null;
                }
                else{previousEnd=null;previousAddress=null;}
                Encryption? encryption=keyAddress is null?null:new(new(Resolve(file.Address,keyAddress)),iv,sequence);
                if(init is not null && encryption is not null)throw Unsupported("Зашифрованный HLS fMP4 пока не поддерживается для кадров.");
                result.Add(new(resource,at,duration.Value,init,encryption,group));
                if(result.Count>MaxSegments)throw Unsupported("HLS содержит слишком много сегментов.");
                at=CheckedTime(at+duration.Value);duration=null;sequence=checked(sequence+1);
            }
        }
        return new(result);
    }

    private static Dictionary<string,string> Attributes(string text)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        int at=0;
        while(at<text.Length)
        {
            while(at<text.Length && (text[at]==',' || char.IsWhiteSpace(text[at])))at++;
            int equal=text.IndexOf('=',at);if(equal<0)throw Unsupported("Некорректные атрибуты HLS.");
            string key=text[at..equal].Trim();at=equal+1;string value;
            if(at<text.Length && text[at]=='"')
            {int end=text.IndexOf('"',++at);if(end<0)throw Unsupported("Незавершённый атрибут HLS.");value=text[at..end];at=end+1;if(at<text.Length && text[at]!=',')throw Unsupported("Некорректные атрибуты HLS.");}
            else{int end=text.IndexOf(',',at);if(end<0)end=text.Length;value=text[at..end].Trim();at=end;}
            if(key.Length==0 || !result.TryAdd(key,value))throw Unsupported("Повтор атрибута HLS.");
        }
        return result;
    }
    private static (long Length,long? Offset) ByteRange(string value)
    {
        var parts=value.Split('@');
        if(parts.Length>2 || !long.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out long length) || length<=0 || length>MediaFrameService.MaxMediaBytes)throw Unsupported("Некорректный диапазон сегмента.");
        long? offset=null;
        if(parts.Length==2){if(!long.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out long parsed) || parsed<0 || parsed>long.MaxValue-length)throw Unsupported("Некорректное смещение сегмента.");offset=parsed;}
        return(length,offset);
    }
    private static Uri Resolve(Uri basis,string value)
    {
        if(value.Length==0 || value.Length>32768 || !Uri.TryCreate(basis,value,out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length>0 || string.IsNullOrWhiteSpace(uri.Host) ||
            basis.Scheme=="https" && uri.Scheme!="https")throw Unsupported("Манифест содержит неподдерживаемый адрес ресурса.");
        return uri;
    }

    // DASH parsing and the transport live below; neither exposes a remote URI
    // to the decoder or writes a manifest/credential to persistent storage.
    private static Plan Dash(byte[] bytes,Uri address,MediaFrameSource source,double seconds,double? endSeconds)=>DashPlan.Parse(bytes,address,source,seconds,endSeconds);
    private sealed class Fetcher:IDisposable
    {
        private readonly MediaFrameSource _source;
        private readonly CookieContainer _cookies;
        private readonly CancellationToken _token;
        private readonly long _limit;
        private readonly HttpClient _http;
        private int _requests;
        internal long ReceivedBytes {get;private set;}
        internal Fetcher(MediaFrameSource source,CookieContainer cookies,CancellationToken token,long limit)
        {
            _source=source;_cookies=cookies;_token=token;_limit=limit;
            _http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,
                AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TimeSpan.FromSeconds(10),MaxResponseHeadersLength=32,
                MaxResponseDrainSize=0,ResponseDrainTimeout=TimeSpan.Zero,UseProxy=!source.Address.IsLoopback}){Timeout=Timeout.InfiniteTimeSpan};
        }
        internal sealed record Fetched(Uri Address,byte[] Bytes,string Type);
        internal async Task<Fetched> GetAsync(Resource resource,int maximum)
        {
            _token.ThrowIfCancellationRequested();
            Uri origin=_source.ManifestAddress??_source.Address;
            Uri target=Resolve(origin,resource.Address.AbsoluteUri);
            bool publicOnly=!SameOrigin(origin,target);
            for(int redirect=0;redirect<=3;redirect++)
            {
                if(++_requests>64)throw Unsupported("Предпросмотр требует слишком много запросов сегментов.");
                using var request=new HttpRequestMessage(HttpMethod.Get,target);
                if(resource.First is {} first)request.Headers.Range=new RangeHeaderValue(first,resource.Last);
                request.Headers.TryAddWithoutValidation("Accept-Encoding","identity");
                foreach(var pair in _source.Headers)
                    if(!BlockedHeader(pair.Key) && (!publicOnly || PublicHeader(pair.Key)))request.Headers.TryAddWithoutValidation(pair.Key,pair.Value);
                string cookie=_cookies.GetCookieHeader(target);
                if(cookie.Length>32*1024)throw Unsupported("Заголовок cookies превышает ограничение предпросмотра.");
                if(cookie.Length>0)request.Headers.TryAddWithoutValidation("Cookie",cookie);
                using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,_token).ConfigureAwait(false);
                if(response.Headers.TryGetValues("Set-Cookie",out var receivedCookies))
                    foreach(string received in receivedCookies)try{_cookies.SetCookies(target,received);}catch(CookieException){ }
                if(response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if(redirect==3 || response.Headers.Location is not {} location)throw Unsupported("Слишком много перенаправлений сегмента.");
                    target=Resolve(target,location.OriginalString);publicOnly|=!SameOrigin(origin,target);continue;
                }
                if(!response.IsSuccessStatusCode)throw Unsupported("Сегмент видео недоступен: HTTP "+(int)response.StatusCode+".");
                if(response.Content.Headers.ContentEncoding.Any(value=>!value.Equals("identity",StringComparison.OrdinalIgnoreCase)))throw Unsupported("Сжатый HTTP-ответ сегмента не поддерживается.");
                if(resource.First is {} expected)
                {
                    var range=response.Content.Headers.ContentRange;
                    if(response.StatusCode!=HttpStatusCode.PartialContent || range is null || !range.HasRange ||
                        !range.Unit.Equals("bytes",StringComparison.OrdinalIgnoreCase) || range.From!=expected ||
                        resource.Last is {} last && range.To!=last)throw Unsupported("Источник не выполнил byte range сегмента.");
                }
                long room=Math.Min(maximum,_limit-ReceivedBytes);
                if(room<0 || response.Content.Headers.ContentLength is {} length && length>room)throw Unsupported("Данные кадра превышают ограничение объёма.");
                using var input=await response.Content.ReadAsStreamAsync(_token).ConfigureAwait(false);
                using var output=new MemoryStream();byte[] buffer=new byte[8192];
                while(true)
                {
                    int count=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,room-output.Length+1)),_token).ConfigureAwait(false);
                    ReceivedBytes+=count;
                    if(output.Length+count>room)throw Unsupported("Данные кадра превышают ограничение объёма.");
                    if(count==0)break;
                    output.Write(buffer,0,count);
                }
                if(resource.First is {} start && resource.Last is {} end && output.Length!=end-start+1)throw Unsupported("Сегмент передан не полностью.");
                return new(target,output.ToArray(),response.Content.Headers.ContentType?.MediaType??"");
            }
            throw Unsupported("Сегмент видео недоступен.");
        }
        private static bool SameOrigin(Uri left,Uri right)=>left.Scheme==right.Scheme && left.Port==right.Port && left.IdnHost.Equals(right.IdnHost,StringComparison.OrdinalIgnoreCase);
        private static bool PublicHeader(string name)=>name.Equals("User-Agent",StringComparison.OrdinalIgnoreCase) || name.Equals("Accept",StringComparison.OrdinalIgnoreCase) || name.Equals("Accept-Language",StringComparison.OrdinalIgnoreCase);
        private static bool BlockedHeader(string name)=>name.Equals("Cookie",StringComparison.OrdinalIgnoreCase) || name.Equals("Host",StringComparison.OrdinalIgnoreCase) || name.Equals("Range",StringComparison.OrdinalIgnoreCase) || name.Equals("Connection",StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length",StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization",StringComparison.OrdinalIgnoreCase) || name.Equals("Accept-Encoding",StringComparison.OrdinalIgnoreCase);
        public void Dispose()=>_http.Dispose();
    }
}
