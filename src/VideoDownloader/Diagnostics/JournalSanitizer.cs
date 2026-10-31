using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

/// <summary>Removes credentials before a diagnostic enters memory, the UI or a file.</summary>
public static class JournalSanitizer
{
    public const int MaxMessageChars=4096;
    private const int MaxInputChars=16*1024;
    private const string Hidden="[скрыто]";
    private const string Truncated=" …[усечено]";
    // These parameters identify public collection/video positions. Unknown
    // parameters stay private: a site may put a session or signed URL in them.
    private static readonly HashSet<string> PublicQueryKeys=new(StringComparer.OrdinalIgnoreCase)
    {
        "v","list","viewkey","id","video_id","playlist","playlist_id","channel_id",
        "index","page","p","t","start","end"
    };
    private const RegexOptions Options=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking;
    private static readonly Regex Url=new(@"\bhttps?://[^\s<>""']+",Options);
    // Cookie parsers may echo a malformed row as Python repr(), including
    // literal \t escapes and a warning prefix. It must not become a raw log.
    private static readonly Regex CookieRow=new(@"(?:#HttpOnly_)?\.?[^\s""'\\]+(?:\t|\\t)(?:TRUE|FALSE)(?:\t|\\t)[^\r\n]*",Options);
    private static readonly Regex CookieDiagnostic=new(
        @"(?<context>\b(?:cookie\s+file\s+(?:entr(?:y|ies)|line)\b(?:\s+due\s+to\s+invalid\s+length\s+\d+\s*:)?|invalid\s+Netscape\s+format\s+cookies?\s+file\b|(?:(?:failed|unable)\s+to|could\s+not)\s+(?:parse|load|read)\s+cookies?(?:\s+file)?\b))[^\r\n]*",Options);
    private static readonly Regex SecretOption=new(
        "(?<option>--(?:cookies(?:-from-browser)?|username|password|video-password|add-headers?|proxy|client-certificate(?:-key|-password)?|certificate-password)\\b)(?:\\s*=\\s*|\\s+)(?:\"[^\"\\r\\n]*\"?|'[^'\\r\\n]*'?|[^\\s]+)",Options);
    private static readonly Regex Header=new(
        "(?<header>\\b(?:authorization|proxy-authorization|cookie|set-cookie))[\"']?\\s*:\\s*[^\\r\\n]*",Options);
    private static readonly Regex Assignment=new(
        "(?<key>\\b(?:password|passwd|access_token|refresh_token|token|api[_-]?key|authorization|cookie|session(?:id)?|secret))[\"']?\\s*[:=]\\s*(?:\"[^\"\\r\\n]*\"?|'[^'\\r\\n]*'?|[^\\s,;|]+)",Options);
    private static readonly Regex Bearer=new(@"\bBearer\s+[^\s,;'""\r\n]+",Options);

    public static string Redact(string? text,bool multiline=false)
    {
        if(string.IsNullOrEmpty(text))return "";
        bool truncated=text.Length>MaxInputChars;
        if(truncated)
        {
            text=text[..SafeLength(text,MaxInputChars)];
            // A cut inside URL userinfo could remove its trailing '@host' and
            // disguise a credential as a public authority. Hide the incomplete
            // final URL before processing the already bounded input.
            var tail=Url.Matches(text).Cast<Match>().LastOrDefault();
            if(tail!=null && tail.Index+tail.Length==text.Length)
                text=text[..tail.Index]+"[слишком длинный URL скрыт]";
        }
        text=DecodeEngineOutput(text);
        // Keep real line boundaries until headers and Netscape cookie rows have
        // been removed. Flattening first could merge a secret into a later line.
        text=CookieDiagnostic.Replace(text,match=>match.Groups["context"].Value+" [содержимое cookies скрыто]");
        text=CookieRow.Replace(text,"[строка cookies скрыта]");
        text=SecretOption.Replace(text,match=>match.Groups["option"].Value+" "+Hidden);
        text=Url.Replace(text,match=>RedactUrl(match.Value));
        text=Header.Replace(text,match=>match.Groups["header"].Value+": "+Hidden);
        // The URL parser already handled its parameters. A generic assignment
        // matcher would otherwise swallow '&v=...' after a hidden token value.
        // Outside URLs, still hide the complete opaque value (including '&').
        var sanitizedUrls=Url.Matches(text);
        text=Assignment.Replace(text,match=>sanitizedUrls.Any(url=>match.Index>=url.Index && match.Index+match.Length<=url.Index+url.Length)
            ? match.Value : match.Groups["key"].Value+"="+Hidden);
        text=Bearer.Replace(text,"Bearer "+Hidden);
        var output=new StringBuilder(Math.Min(text.Length,MaxMessageChars));
        for(int index=0;index<text.Length;index++)
        {
            char value=text[index];
            switch(value)
            {
                // Continuations stay readable without impersonating the next
                // timestamped journal record. Never unescape literal backslashes.
                case '\r' when multiline:
                    if(index+1<text.Length && text[index+1]=='\n')index++;
                    output.Append(Environment.NewLine).Append("    ");break;
                case '\n' when multiline:output.Append(Environment.NewLine).Append("    ");break;
                case '\r':output.Append("\\r");break;
                case '\n':output.Append("\\n");break;
                case '\t':output.Append("\\t");break;
                default:
                    if(char.IsControl(value))output.Append("\\u"+((int)value).ToString("x4"));
                    else if(char.IsHighSurrogate(value))
                    {
                        if(index+1<text.Length && char.IsLowSurrogate(text[index+1]))output.Append(value).Append(text[++index]);
                        else output.Append('\uFFFD');
                    }
                    else output.Append(char.IsLowSurrogate(value)?'\uFFFD':value);
                    break;
            }
            if(output.Length>MaxMessageChars) {truncated=true;break;}
        }
        string result=output.ToString();
        if(truncated || result.Length>MaxMessageChars)
            result=result[..SafeLength(result,Math.Min(result.Length,MaxMessageChars-Truncated.Length))]+Truncated;
        return result;
    }

    private static string DecodeEngineOutput(string text)
    {
        // Engine output uses a final text=<JSON string> field. Decode that field
        // before matching headers/options: e.g. escaped quote characters around
        // a password must not make the remaining words look like public text.
        // Do not unescape arbitrary backslashes in ordinary Windows paths.
        int marker=text.IndexOf(" text=\"",StringComparison.Ordinal);
        int start=marker<0?(text.StartsWith("text=\"",StringComparison.Ordinal)?5:-1):marker+6;
        if(start<0)return text;
        try
        {
            string? value=JsonSerializer.Deserialize<string>(text[start..]);
            return text[..start]+(value??"");
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or ArgumentException)
        {
            // The raw field may have hit the pre-processing limit. Keep useful
            // session/component fields, but never expose half-escaped secrets.
            return text[..start]+"[исходный вывод скрыт: неполная JSON-строка]";
        }
    }

    private static string RedactUrl(string value)
    {
        int authority=value.IndexOf("://",StringComparison.Ordinal)+3;
        int authorityEnd=value.IndexOfAny(['/','?','#'],authority);
        if(authorityEnd<0)authorityEnd=value.Length;
        int userInfo=value.LastIndexOf('@',authorityEnd-1,authorityEnd-authority);
        if(userInfo>=authority)value=value[..authority]+Hidden+"@"+value[(userInfo+1)..];
        int fragment=value.IndexOf('#');
        bool hasFragment=fragment>=0;
        if(hasFragment)value=value[..fragment];
        int query=value.IndexOf('?');
        if(query>=0)
        {
            var result=new StringBuilder(value[..(query+1)]);
            int start=query+1;
            for(int index=start;index<=value.Length;index++)
            {
                if(index<value.Length && value[index] is not ('&' or ';'))continue;
                string parameter=value[start..index];
                int equals=parameter.IndexOf('=');
                string key=equals<0?parameter:parameter[..equals];
                string decoded;
                try{decoded=Uri.UnescapeDataString(key.Replace('+',' '));}
                catch(UriFormatException){decoded="";}
                if(PublicQueryKeys.Contains(decoded))result.Append(parameter);
                else if(parameter.Length>0)result.Append(equals<0?Hidden:key+"="+Hidden);
                if(index<value.Length)result.Append(value[index]);
                start=index+1;
            }
            value=result.ToString();
        }
        return value+(hasFragment?"#"+Hidden:"");
    }

    private static int SafeLength(string text,int length)=>length>0 && char.IsHighSurrogate(text[length-1])?length-1:length;
}
