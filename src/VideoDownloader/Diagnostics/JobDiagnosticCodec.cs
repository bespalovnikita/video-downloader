using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

internal static class JobDiagnosticCodec
{
    internal const int MaxLineBytes=64*1024;
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {MaxDepth=12,Converters={new JsonStringEnumConverter()}};
    private static bool Digest(string? value,int length)=>value is not null && value.Length==length &&
        value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Id(string? value,int max=128)=>value is {Length:>0} && value.Length<=max &&
        Regex.IsMatch(value,@"\A[A-Za-z0-9_.-]+\z",RegexOptions.CultureInvariant)?value:"";
    internal static JobDiagnosticEvent? Capture(JournalRecord record,string journal,long sequence,DateTimeOffset time)
    {
        if(!Digest(record.Identity,64) || record.Job!=record.Identity[..12])return null;
        return new(1,journal,sequence,time,record.Level,Id(record.Category),Id(record.Session),record.Job,record.Identity,
            Id(record.Worker),Id(record.Event,64),Positive(record.Attempt),Positive(record.Invocation),
            DiagnosticPrivacy.Text(ErrorMessages.ForLog(record.Message),true),record.Settings?.Safe());
    }
    private static int? Positive(int? value)=>value is >0 and <=1_000_000?value:null;
    internal static byte[] Encode(JobDiagnosticEvent entry)=>JsonSerializer.SerializeToUtf8Bytes(entry,Json);
    internal static JobDiagnosticEvent? Decode(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length is 0 or >MaxLineBytes)return null;
        try
        {
            var entry=JsonSerializer.Deserialize<JobDiagnosticEvent>(bytes,Json);
            if(entry is null || entry.Version!=1 || !Digest(entry.Journal,32) || !Digest(entry.Identity,64) ||
                entry.Job!=entry.Identity[..12] || entry.Sequence<=0 || entry.CapturedAt.Year is <1970 or >3000 ||
                !Enum.IsDefined(entry.Level) || entry.Message is null || entry.Message.Length>16*1024)return null;
            return entry with {Category=Id(entry.Category),Session=Id(entry.Session),Worker=Id(entry.Worker),Event=Id(entry.Event,64),
                Attempt=Positive(entry.Attempt),Invocation=Positive(entry.Invocation),
                Message=DiagnosticPrivacy.Text(entry.Message,true),Settings=entry.Settings?.Safe()};
        }
        catch(Exception error) when(error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {return null;}
    }
}
