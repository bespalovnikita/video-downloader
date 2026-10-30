using VideoDownloader.Native;
using System.Text.Json;

internal static class JournalSanitizerTests
{
    internal static void Run()
    {
        string url=JournalSanitizer.Redact("extract https://user:pass@example.org/Видео?id=public-id&token=secret-token#secret-fragment; next");
        Require(url.Contains("https://[скрыто]@example.org/Видео") && !url.Contains("user:") && !url.Contains("pass") &&
            url.Contains("id=public-id") && !url.Contains("secret-token") && !url.Contains("secret-fragment"),
            "URL credentials and secret query/fragment values must be removed while retaining public video IDs and Unicode paths");
        Require(JournalSanitizer.Redact("HTTPS://EXAMPLE.org/watch?Token=AbC").Contains("HTTPS://EXAMPLE.org/watch?Token=[скрыто]"),
            "Masking must cover uppercase schemes and preserve host/path spelling");
        string publicUrl="https://www.youtube.com/watch?v=AbC_de-12&list=PL123&index=3&t=25";
        Require(JournalSanitizer.Redact("Thumbnail timed out for "+publicUrl).Contains(publicUrl),
            "A failed public YouTube video remains identifiable and copyable, including collection position");
        string mixed=JournalSanitizer.Redact("https://example.org/watch?token=secret-token&v=PublicVideo&unknown=private-unknown&viewkey=PublicKey&%61ccess_token=secret-access&Signature=secret-signed&%76=SecondId#private-fragment");
        Require(mixed.Contains("v=PublicVideo") && mixed.Contains("viewkey=PublicKey") && mixed.Contains("%76=SecondId") &&
            !mixed.Contains("secret-token") && !mixed.Contains("private-unknown") && !mixed.Contains("secret-access") &&
            !mixed.Contains("secret-signed") && !mixed.Contains("private-fragment"),
            "Public keys survive alongside secret/unknown/signed parameters without decoding or normalizing the original address");
        string headers=JournalSanitizer.Redact("HTTP 403\nAuthorization: Bearer PRIVATE-AUTH\r\nCookie: SID=PRIVATE-COOKIE\nSet-Cookie: token=PRIVATE-SET\nProxy-Authorization: Basic PRIVATE-PROXY\nnext useful diagnostic");
        Require(!headers.Contains("PRIVATE-") && headers.Contains("HTTP 403") && headers.Contains("next useful diagnostic") &&
            !headers.Contains('\n') && !headers.Contains('\r'),"All authentication headers must be redacted before physical lines are escaped");
        string options=JournalSanitizer.Redact("--cookies \"C:\\Users\\Private Name\\cookies.txt\" --password=my-password --username login-name --video-password 'video password' --add-header 'Cookie: SID=very-secret' --proxy http://proxy-user:proxy-password@proxy.invalid:8080 --quality 1080");
        foreach(string secret in new[]{"Private Name","my-password","login-name","video password","very-secret","proxy-user","proxy-password"})
            Require(!options.Contains(secret),"CLI secret values must be removed: "+secret);
        Require(options.Contains("--quality 1080"),"Ordinary diagnostic arguments must remain useful");
        string assignments=JournalSanitizer.Redact("password=not-for-logs api_key: abc-123 access_token=xyz sessionid=hidden-id secret='two words' Bearer opaque-bearer progress=12.5 component=video:137");
        foreach(string secret in new[]{"not-for-logs","abc-123","xyz","hidden-id","two words","opaque-bearer"})
            Require(!assignments.Contains(secret),"Secret assignments must be removed: "+secret);
        Require(assignments.Contains("progress=12.5") && assignments.Contains("component=video:137"),
            "Progress and component fields are not secrets and must survive redaction");
        Require(!JournalSanitizer.Redact("password=opaque&password-tail").Contains("password-tail"),
            "Opaque assignments outside URLs still hide complete secret values containing an ampersand");
        string cookie=JournalSanitizer.Redact(".example.org\tTRUE\t/\tTRUE\t1790000000\tSID\tNetscape-cookie-secret\n#HttpOnly_example.org\tFALSE\t/\tTRUE\t1790000000\tSID\thttp-only-secret");
        Require(!cookie.Contains("Netscape-cookie-secret") && !cookie.Contains("http-only-secret"),"Accidentally supplied Netscape rows must not enter a journal");
        Require(JournalSanitizer.Redact("one\n[ERROR] injected\r\ntab\t\u0001")=="one\\n[ERROR] injected\\r\\ntab\\t\\u0001",
            "Untrusted child text cannot inject extra physical journal records");
        Require(JournalSanitizer.Redact("one\r\n[ERROR] injected\ntab\t\u0001",multiline:true)==
            "one"+Environment.NewLine+"    [ERROR] injected"+Environment.NewLine+"    tab\\t\\u0001",
            "Readable multiline messages indent every continuation so child output cannot forge another journal record");
        Require(JournalSanitizer.Redact(@"C:\new\root\notes.txt; literal \r\n",multiline:true)==@"C:\new\root\notes.txt; literal \r\n",
            "Multiline rendering must not unescape ordinary Windows paths or literal backslashes");
        Require(JournalSanitizer.Redact(new string('x',100_000)).Length<=JournalSanitizer.MaxMessageChars,
            "Raw child output is bounded before regex processing and after formatting");
        Require(JournalSanitizer.Redact(new string('\t',10_000)).Length<=JournalSanitizer.MaxMessageChars,
            "Escaping cannot expand past the journal entry cap");
        Require(JournalSanitizer.Redact(new string('\n',10_000),multiline:true).Length<=JournalSanitizer.MaxMessageChars,
            "Continuation indentation cannot expand beyond the journal entry cap");
        Require(JournalSanitizer.Redact("🎬 видео")=="🎬 видео" && JournalSanitizer.Redact("broken \ud800")=="broken �",
            "Valid Unicode is retained and broken UTF-16 cannot break file encoding");
        Require(JournalSanitizer.Redact(null)=="" && JournalSanitizer.Redact("")=="","Empty input is harmless");
        string Engine(string raw)=>"event=output stream=stderr component=video:137 percent=12.5 text="+JsonSerializer.Serialize(raw);
        foreach(string raw in new[]{
            "Cookie: SID=wrapped-cookie-secret",
            "Authorization: Bearer wrapped-auth-secret",
            "request headers: {\"Cookie\":\"SID=wrapped-json-cookie\",\"Authorization\":\"Bearer wrapped-json-auth\"}",
            "Cookie: SID=first-header-secret\nAuthorization: Bearer second-header-secret\nnext useful diagnostic",
            "--password \"wrapped secret password\" --cookies \"C:\\Users\\Sensitive Name\\cookies.txt\"",
            "https://user:wrapped-pass@example.org/Видео?token=wrapped-url-secret#wrapped-fragment"
        })
        {
            string redacted=JournalSanitizer.Redact(Engine(raw));
            foreach(string secret in new[]{"wrapped-cookie-secret","wrapped-auth-secret","wrapped-json-cookie","wrapped-json-auth",
                "first-header-secret","second-header-secret","wrapped secret password","Sensitive Name","wrapped-pass","wrapped-url-secret","wrapped-fragment"})
                Require(!redacted.Contains(secret),"Serialized engine output must redact decoded secrets: "+secret);
            Require(redacted.Contains("component=video:137") && redacted.Contains("percent=12.5"),
                "Serialized child output must retain trace correlation and progress fields");
        }
        Require(JournalSanitizer.Redact(Engine("https://example.org/Видео?token=private")).Contains("https://example.org/Видео?token=[скрыто]"),
            "Decoded raw output retains host/path after JSON escaping");
        Require(JournalSanitizer.Redact(Engine("before\r\nCookie: SID=wrapped-secret\nafter"),multiline:true)
            .Contains("before"+Environment.NewLine+"    Cookie=[скрыто]"+Environment.NewLine+"    after"),
            "Serialized child text is decoded once before redaction and readable continuation formatting");
        foreach(string raw in new[]{
            @"WARNING: skipping cookie file entry due to invalid length 8: '.example.org\tTRUE\t/\tTRUE\t1790000000\tSID\tPRIVATE_COOKIE\textra'",
            "WARNING: skipping cookie file entry due to invalid length 8: '.example.org\tTRUE\t/\tTRUE\t1790000000\tSID\tPRIVATE_COOKIE\textra'",
            "WARNING: skipping cookie file entry due to invalid expires at: 'PRIVATE_COOKIE without any valid delimiters'",
            @"ERROR: http.cookiejar.LoadError: invalid Netscape format cookies file 'cookies.txt': '.example.org\tTRUE\t/\tTRUE\t1790000000\tSID\tPRIVATE_COOKIE'",
            @"Parse failure: '.example.org\tTRUE\t/\tTRUE\t1790000000\tSID\tPRIVATE_COOKIE'",
            "ERROR: failed to load cookies file: malformed content PRIVATE_COOKIE",
            "ERROR: could not read cookies file: malformed content PRIVATE_COOKIE",
            "WARNING: cookie file entry was empty: PRIVATE_COOKIE"
        })
        {
            Require(!JournalSanitizer.Redact(raw).Contains("PRIVATE_COOKIE"),
                "Cookie parsing warnings and failures must never echo raw or Python-repr credential rows");
            string redacted=JournalSanitizer.Redact(Engine(raw));
            Require(!redacted.Contains("PRIVATE_COOKIE") && redacted.Contains("component=video:137") && redacted.Contains("percent=12.5"),
                "JSON-wrapped malformed cookie diagnostics must hide values while retaining trace correlation");
        }
        Require(JournalSanitizer.Redact(@"WARNING: skipping cookie file entry due to invalid length 8: 'PRIVATE_COOKIE'")
            .Contains("due to invalid length 8:"),"Safe cookie-parser reason and field count remain available for diagnosis");
        Require(!JournalSanitizer.Redact("event=output text=\"--password \\u0022private incomplete").Contains("private incomplete"),
            "A truncated escaped raw-output field must fail closed");
        Console.WriteLine("PASS: journal credential redaction, Unicode, line-injection defense and bounded diagnostic text");
    }

    private static void Require(bool value,string message)
    {
        if(!value)throw new InvalidOperationException("Journal sanitizer test failed: "+message);
    }
}
