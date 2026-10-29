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
    private const RegexOptions Options=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking;
    private static readonly Regex Url=new(@"\bhttps?://[^\s<>""']+",Options);
    private static readonly Regex CookieRow=new(@"^(?:#HttpOnly_)?\.?[^\s]+\t(?:TRUE|FALSE)\t[^\r\n]*",Options|RegexOptions.Multiline);
    private static readonly Regex SecretOption=new(
        "(?<option>--(?:cookies(?:-from-browser)?|username|password|video-password|add-headers?|proxy|client-certificate(?:-key|-password)?|certificate-password)\\b)(?:\\s*=\\s*|\\s+)(?:\"[^\"\\r\\n]*\"?|'[^'\\r\\n]*'?|[^\\s]+)",Options);
    private static readonly Regex Header=new(
        "(?<header>\\b(?:authorization|proxy-authorization|cookie|set-cookie))[\"']?\\s*:\\s*[^\\r\\n]*",Options);
    private static readonly Regex Assignment=new(
        "(?<key>\\b(?:password|passwd|access_token|refresh_token|token|api[_-]?key|authorization|cookie|session(?:id)?|secret))[\"']?\\s*[:=]\\s*(?:\"[^\"\\r\\n]*\"?|'[^'\\r\\n]*'?|[^\\s,;|]+)",Options);
    private static readonly Regex Bearer=new(@"\bBearer\s+[^\s,;'""\r\n]+",Options);

    public static string Redact(string? text)
    {
        if(string.IsNullOrEmpty(text))return "";
        bool truncated=text.Length>MaxInputChars;
        if(truncated)text=text[..SafeLength(text,MaxInputChars)];
        text=DecodeEngineOutput(text);
        // Keep real line boundaries until headers and Netscape cookie rows have
        // been removed. Flattening first could merge a secret into a later line.
        text=CookieRow.Replace(text,"[строка cookies скрыта]");
        text=SecretOption.Replace(text,match=>match.Groups["option"].Value+" "+Hidden);
        text=Url.Replace(text,match=>RedactUrl(match.Value));
        text=Header.Replace(text,match=>match.Groups["header"].Value+": "+Hidden);
        text=Assignment.Replace(text,match=>match.Groups["key"].Value+"="+Hidden);
        text=Bearer.Replace(text,"Bearer "+Hidden);
        var output=new StringBuilder(Math.Min(text.Length,MaxMessageChars));
        for(int index=0;index<text.Length;index++)
        {
            char value=text[index];
            switch(value)
            {
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
        int query=value.IndexOf('?'),fragment=value.IndexOf('#');
        int suffix=query<0?fragment:fragment<0?query:Math.Min(query,fragment);
        if(suffix>=0)
        {
            string markers=(query>=0 && (fragment<0 || query<fragment)?"?"+Hidden:"")+(fragment>=0?"#"+Hidden:"");
            value=value[..suffix]+markers;
        }
        return value;
    }

    private static int SafeLength(string text,int length)=>length>0 && char.IsHighSurrogate(text[length-1])?length-1:length;
}
