using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VideoDownloader.Native;

internal sealed record PortableQueueEntry(string Url,string Title="");

// The portable contract deliberately contains no session, ownership or credentials.
internal static class PortableQueueCodec
{
    internal const string Prefix="VDQUEUE1:";
    internal const int MaxItems=100_000;
    internal const int MaxJsonBytes=16*1024*1024;
    internal const int MaxCompressedBytes=16*1024*1024;
    internal const int MaxTextChars=24*1024*1024;
    internal const int ClipboardTextLimit=262_144;
    private const string Format="video-downloader.queue";
    private static readonly UTF8Encoding Utf8=new(false,true);
    private static readonly uint[] CrcTable=CreateCrcTable();

    internal static bool IsPortable(string? text)
    {
        if(text==null)return false;
        var value=text.AsSpan().TrimStart();
        if(value.StartsWith("\uFEFF",StringComparison.Ordinal))value=value[1..].TrimStart();
        return value.StartsWith("VDQUEUE",StringComparison.Ordinal);
    }

    internal static string Encode(IEnumerable<PortableQueueEntry> entries)
    {
        var items=Validate(entries);
        using var json=new BoundedMemoryStream(MaxJsonBytes);
        using(var writer=new Utf8JsonWriter(json,new JsonWriterOptions{Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}))
        {
            writer.WriteStartObject();writer.WriteString("format",Format);writer.WriteNumber("version",1);
            writer.WriteStartArray("items");
            foreach(var item in items)
            {
                writer.WriteStartObject();writer.WriteString("url",item.Url);writer.WriteString("title",item.Title);writer.WriteEndObject();
            }
            writer.WriteEndArray();writer.WriteEndObject();writer.Flush();
        }
        json.Position=0;
        using var compressed=new BoundedMemoryStream(MaxCompressedBytes);
        using(var gzip=new GZipStream(compressed,CompressionLevel.Optimal,true))json.CopyTo(gzip);
        return Prefix+Convert.ToBase64String(compressed.ToArray());
    }

    internal static IReadOnlyList<PortableQueueEntry> Decode(string text)
    {
        try
        {
            if(text.Length>MaxTextChars)throw Invalid();
            string value=text.Trim();
            if(value.StartsWith('\uFEFF'))value=value[1..].TrimStart();
            if(!value.StartsWith(Prefix,StringComparison.Ordinal))throw Invalid();
            string body=value[Prefix.Length..];
            if(body.Length==0)throw Invalid();
            // ASCII line wrapping is safe for copying the text through a chat.
            if(body.Any(character=>!IsBase64(character) && character is not (' ' or '\t' or '\r' or '\n')))throw Invalid();
            int symbols=body.Count(character=>character is not (' ' or '\t' or '\r' or '\n'));
            if(symbols==0 || symbols%4!=0 || symbols>((MaxCompressedBytes+2L)/3L)*4L)throw Invalid();
            byte[] bytes=Convert.FromBase64String(body);
            if(bytes.Length<18 || bytes.Length>MaxCompressedBytes)throw Invalid();
            using var source=new MemoryStream(bytes,false);
            using var gzip=new GZipStream(source,CompressionMode.Decompress);
            using var json=new BoundedMemoryStream(MaxJsonBytes);
            gzip.CopyTo(json);
            // .NET may accept EOF before a GZIP trailer. Require the same
            // complete member that Java's GZIPInputStream requires.
            var trailer=bytes.AsSpan(bytes.Length-8);
            if(BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..])!=(uint)json.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(trailer)!=Crc32(json.GetBuffer().AsSpan(0,(int)json.Length)))throw Invalid();
            string document=Utf8.GetString(json.GetBuffer(),0,checked((int)json.Length));
            using var parsed=JsonDocument.Parse(document,new JsonDocumentOptions{MaxDepth=32});
            var root=parsed.RootElement;
            if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("format",out var format) ||
                format.ValueKind!=JsonValueKind.String || format.GetString()!=Format ||
                !root.TryGetProperty("version",out var version) || version.ValueKind!=JsonValueKind.Number || version.GetRawText()!="1" ||
                !root.TryGetProperty("items",out var items) || items.ValueKind!=JsonValueKind.Array || items.GetArrayLength()>MaxItems)throw Invalid();
            var entries=new List<PortableQueueEntry>(items.GetArrayLength());
            foreach(var item in items.EnumerateArray())
            {
                if(item.ValueKind!=JsonValueKind.Object || !item.TryGetProperty("url",out var url) || url.ValueKind!=JsonValueKind.String)throw Invalid();
                string title="";
                if(item.TryGetProperty("title",out var name))
                {
                    if(name.ValueKind!=JsonValueKind.String)throw Invalid();
                    title=name.GetString()!;
                }
                entries.Add(new PortableQueueEntry(url.GetString()!,title));
            }
            return Validate(entries);
        }
        catch(Exception error) when(error is FormatException or JsonException or DecoderFallbackException or ArgumentException or IOException or OverflowException or InvalidOperationException)
        {
            throw Invalid();
        }
    }

    private static bool IsBase64(char value)=>value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=';
    private static List<PortableQueueEntry> Validate(IEnumerable<PortableQueueEntry> entries)
    {
        var result=new List<PortableQueueEntry>();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        int count=0;
        foreach(var entry in entries)
        {
            if(++count>MaxItems || entry==null || entry.Title==null || !ValidUrl(entry.Url))throw Invalid();
            try { if(Utf8.GetByteCount(entry.Title)>=MaxJsonBytes)throw Invalid(); }
            catch(EncoderFallbackException) { throw Invalid(); }
            if(seen.Add(entry.Url))result.Add(entry);
        }
        return result;
    }

    private static bool ValidUrl(string? value)
    {
        try { if(value!=null)Utf8.GetByteCount(value); }
        catch(EncoderFallbackException) { return false; }
        if(string.IsNullOrEmpty(value) || value.Any(character=>char.IsWhiteSpace(character) || char.IsControl(character)) ||
            !Uri.TryCreate(value,UriKind.Absolute,out var uri) || !uri.IsWellFormedOriginalString() ||
            (uri.Scheme!=Uri.UriSchemeHttp && uri.Scheme!=Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length!=0)return false;
        int start=value.IndexOf("://",StringComparison.Ordinal);
        if(start<0)return false;
        string authority=value[(start+3)..].Split(['/','?','#'],2)[0];
        if(authority.Contains('@'))return false;
        if(uri.HostNameType==UriHostNameType.IPv6)return true;
        try
        {
            string host=uri.IdnHost;
            if(host.EndsWith('.'))host=host[..^1];
            return host.Length is >0 and <=253 && host.Split('.').All(label=>label.Length is >0 and <=63 &&
                label.All(character=>character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'));
        }
        catch(UriFormatException) { return false; }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint value=uint.MaxValue;
        foreach(byte item in bytes)value=CrcTable[(value^item)&255]^(value>>8);
        return ~value;
    }
    private static uint[] CreateCrcTable()
    {
        var table=new uint[256];
        for(uint index=0;index<table.Length;index++)
        {
            uint value=index;
            for(int bit=0;bit<8;bit++)value=(value&1)!=0 ? 0xedb88320U^(value>>1) : value>>1;
            table[index]=value;
        }
        return table;
    }

    private static InvalidDataException Invalid()=>new("Не удалось прочитать переносимую очередь. Нужен неповреждённый экспорт VDQUEUE1 версии 1, до 100 000 ссылок и 16 МиБ данных.");

    private sealed class BoundedMemoryStream(int limit):MemoryStream
    {
        private void Check(int count)
        {
            if(count<0 || Length+count>limit)throw Invalid();
        }
        public override void Write(byte[] buffer,int offset,int count){Check(count);base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){Check(buffer.Length);base.Write(buffer);}
        public override void WriteByte(byte value){Check(1);base.WriteByte(value);}
    }
}
