using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class PortableQueueTests
{
    internal static void Run()
    {
        var items=new[]{new PortableQueueEntry("https://example.org/watch?v=AbC&Token=%2f", "Канал — 東京 🎬"),
            new PortableQueueEntry("https://example.org/watch?v=abc&Token=%2F", "вторая\nстрока")};
        string encoded=PortableQueueCodec.Encode(items);
        Require(encoded.StartsWith(PortableQueueCodec.Prefix,StringComparison.Ordinal) && encoded.All(character=>character<128),"Transfer is a portable ASCII envelope");
        Require(PortableQueueCodec.Decode(encoded).SequenceEqual(items),"Unicode titles, exact case and escaping survive compression round trip");
        Require(PortableQueueCodec.Decode(" \r\n\uFEFF\t"+encoded+"\n").SequenceEqual(items),"One outer BOM and whitespace may wrap a transfer");
        string body=encoded[PortableQueueCodec.Prefix.Length..];
        string wrapped=PortableQueueCodec.Prefix+string.Join(" \t\r\n",Enumerable.Range(0,(body.Length+19)/20).Select(index=>body.Substring(index*20,Math.Min(20,body.Length-index*20))));
        Require(PortableQueueCodec.Decode(wrapped).SequenceEqual(items),"ASCII wrapping introduced by chat is allowed inside base64");
        Require(PortableQueueCodec.IsPortable("\uFEFF "+encoded) && !PortableQueueCodec.IsPortable("https://example.org"),"Ingress identifies only the queue marker");
        Require(PortableQueueCodec.IsPortable("VDQUEUE2:broken https://example.org/must-not-be-partially-added"),"Future transfer versions must reach strict decoding instead of ordinary URL extraction");
        Require(PortableQueueCodec.Decode(PortableQueueCodec.Encode([])).Count==0,"An empty queue has a valid envelope");

        var duplicate=Raw("""{"format":"video-downloader.queue","version":1,"items":[{"url":"https://example.org/Video","title":"первое"},{"url":"https://example.org/Video","title":"замена"},{"url":"https://example.org/video"}],"history":["not imported"],"cookies":"not imported"}""");
        var deduplicated=PortableQueueCodec.Decode(duplicate);
        Require(deduplicated.Count==2 && deduplicated[0].Title=="первое" && deduplicated[1].Title=="", "First exact URL wins and unknown state cannot enter the transfer contract");
        Require(PortableQueueCodec.Encode([items[0],items[0],items[1]])==encoded,"Export coalesces duplicates without reordering unique URLs");
        Refuses(()=>PortableQueueCodec.Decode(Raw("""{"format":"video-downloader.queue","version":1,"items":[{"url":"https://example.org/a"},{"url":"https://example.org/a","title":null}]}""")),"A malformed duplicate cannot hide behind a valid first record");

        foreach(string document in new[]{
            "{}", "[]", "null", "{", """{"format":"other","version":1,"items":[]}""",
            """{"format":"video-downloader.queue","version":2,"items":[]}""",
            """{"format":"video-downloader.queue","version":1.0,"items":[]}""",
            """{"format":"video-downloader.queue","version":1e0,"items":[]}""",
            """{"format":"video-downloader.queue","version":"1","items":[]}""",
            """{"format":"video-downloader.queue","version":1,"items":{}}""",
            """{"format":"video-downloader.queue","version":1,"items":[null]}""",
            """{"format":"video-downloader.queue","version":1,"items":[{"url":123}]}""",
            """{"format":"video-downloader.queue","version":1,"items":[{"url":"https://example.org","title":false}]}"""})
            Refuses(()=>PortableQueueCodec.Decode(Raw(document)),"Invalid document shape/version must be rejected atomically");
        Refuses(()=>PortableQueueCodec.Decode(Raw("""{"format":"video-downloader.queue","version":1,"items":[{"url":"https://example.org","title":"\ud800"}]}""")),"Unpaired escaped Unicode cannot become an uncaught parser exception");
        Refuses(()=>PortableQueueCodec.Encode([new PortableQueueEntry("https://example.org",new string('\ud800',1))]),"Export cannot silently replace an unpaired Unicode surrogate");
        Refuses(()=>PortableQueueCodec.Encode([new PortableQueueEntry("https://example.org/"+new string('\ud800',1))]),"An unpaired surrogate cannot be replaced inside an exact URL");
        foreach(string url in new[]{"https://пример.рф/video","https://some_host.example/a","https://127.1/a","https://-bad.example/a","https://[::1]:65535/a","https://example.org:0/a"})
            Require(PortableQueueCodec.Decode(PortableQueueCodec.Encode([new PortableQueueEntry(url)])).Single().Url==url,"Portable hosts and valid ports share the Android contract without URL normalization");
        foreach(string url in new[]{"file:///tmp/video.mp4","ftp://example.org/video","https://user:pass@example.org/video","https://@example.org/video","https://example.org/a b","https://example.org/%ZZ","https://example.org/\nvideo"})
        {
            string json=JsonSerializer.Serialize(new {format="video-downloader.queue",version=1,items=new[]{new{url,title=""}}});
            Refuses(()=>PortableQueueCodec.Decode(Raw(json)),"Only exact valid HTTP(S) addresses are portable");
            Refuses(()=>PortableQueueCodec.Encode([new PortableQueueEntry(url)]),"Export validation equals import validation");
        }
        foreach(string invalid in new[]{"VDQUEUE2:AAAA","VDQUEUE1:","VDQUEUE1:%%%","VDQUEUE1:AAAA_===","VDQUEUE1:AAA", "VDQUEUE1:A===", "VDQUEUE1:AAAA\u00A0AAAA", "\uFEFF\uFEFF"+encoded})
            Refuses(()=>PortableQueueCodec.Decode(invalid),"Invalid prefix/base64/padding must not become partial imports");
        Refuses(()=>PortableQueueCodec.Decode(PortableQueueCodec.Prefix+Convert.ToBase64String(new byte[]{1,2,3,4})),"Non-gzip bytes are rejected");
        Refuses(()=>PortableQueueCodec.Encode([new PortableQueueEntry("https://example.org:65536/a")]),"Ports outside 0..65535 are rejected by both platforms");
        byte[] gzipBytes=Convert.FromBase64String(body);
        foreach(int removed in new[]{1,4,8,10})
            Refuses(()=>PortableQueueCodec.Decode(PortableQueueCodec.Prefix+Convert.ToBase64String(gzipBytes[..^removed])),"Truncated GZIP trailers cannot import a partial packet");
        Refuses(()=>PortableQueueCodec.Decode(RawBytes(new byte[]{0xC0,0xAF})),"Malformed UTF-8 is rejected rather than replaced");
        Refuses(()=>PortableQueueCodec.Decode(Raw(new string('[',40)+new string(']',40))),"Excessive JSON nesting is rejected");

        using(var json=new MemoryStream())
        {
            json.Write(Encoding.UTF8.GetBytes("{\"format\":\"video-downloader.queue\",\"version\":1,\"items\":["));
            byte[] record=Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/duplicate\"}");
            for(int index=0;index<=PortableQueueCodec.MaxItems;index++) { if(index>0)json.WriteByte((byte)',');json.Write(record); }
            json.Write(Encoding.UTF8.GetBytes("]}"));
            Refuses(()=>PortableQueueCodec.Decode(RawBytes(json.ToArray())),"The record count applies before de-duplication");
        }
        Refuses(()=>PortableQueueCodec.Encode(Enumerable.Repeat(new PortableQueueEntry("https://example.org/repeat"),PortableQueueCodec.MaxItems+1)),"Export bounds the source record count before de-duplication");
        Refuses(()=>PortableQueueCodec.Decode(new string(' ',PortableQueueCodec.MaxTextChars+1)),"Raw text length is checked before trimming or decoding");
        Refuses(()=>PortableQueueCodec.Decode(Raw(new string(' ',PortableQueueCodec.MaxJsonBytes+1))),"A tiny gzip bomb cannot inflate beyond the JSON byte cap");
        Refuses(()=>PortableQueueCodec.Encode([new PortableQueueEntry("https://example.org/huge",new string('x',PortableQueueCodec.MaxJsonBytes))]),"Encoding is bounded while writing UTF-8 JSON");

        string fixture=Path.Combine(AppContext.BaseDirectory,"fixtures","portable-queue","queue-v1.txt");
        string expectedFile=Path.Combine(AppContext.BaseDirectory,"fixtures","portable-queue","queue-v1.expected.json");
        Require(File.Exists(fixture) && File.Exists(expectedFile),"The shared cross-platform fixture must be packaged with tests");
        var shared=PortableQueueCodec.Decode(File.ReadAllText(fixture));
        using var expected=JsonDocument.Parse(File.ReadAllText(expectedFile));
        var expectedRows=expected.RootElement.GetProperty("items").EnumerateArray().Select(item=>new PortableQueueEntry(item.GetProperty("url").GetString()!,item.GetProperty("title").GetString()!)).ToArray();
        Require(shared.SequenceEqual(expectedRows),"Windows decodes the same language-neutral fixture as Android");
        Require(PortableQueueCodec.Decode(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture)!,"queue-v1-wrapped.txt"))).SequenceEqual(expectedRows),"Windows and Android accept the same BOM and line-wrapped fixture");
        Require(PortableQueueCodec.Decode(PortableQueueCodec.Encode(shared)).SequenceEqual(expectedRows),"Re-export of a foreign queue preserves logical data");
        using var urlCases=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture)!,"url-cases.json")));
        foreach(var item in urlCases.RootElement.EnumerateArray())
        {
            string url=item.GetProperty("url").GetString()!;
            var entry=new PortableQueueEntry(url,"точный URL");
            string raw=Raw(JsonSerializer.Serialize(new{format="video-downloader.queue",version=1,items=new[]{new{url,title=entry.Title}}}));
            if(item.GetProperty("valid").GetBoolean())
            {
                Require(PortableQueueCodec.Decode(PortableQueueCodec.Encode([entry])).Single()==entry,"Shared valid URL survives encode/decode: "+url);
                Require(PortableQueueCodec.Decode(raw).Single()==entry,"Shared valid URL is accepted on import: "+url);
            }
            else
            {
                Refuses(()=>PortableQueueCodec.Encode([entry]),"Shared invalid URL cannot be exported: "+url);
                Refuses(()=>PortableQueueCodec.Decode(raw),"Shared invalid URL cannot be imported: "+url);
            }
        }
    }

    private static string Raw(string json)=>RawBytes(Encoding.UTF8.GetBytes(json));
    private static string RawBytes(byte[] json)
    {
        using var output=new MemoryStream();
        using(var gzip=new GZipStream(output,CompressionLevel.Optimal,true))gzip.Write(json);
        return PortableQueueCodec.Prefix+Convert.ToBase64String(output.ToArray());
    }
    private static void Refuses(Action action,string message)
    {
        try { action(); }
        catch(InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
}
