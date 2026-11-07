using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace VideoDownloader.Native;

internal sealed record JournalLimits(int MemoryEntries=5000,int MemoryBytes=2*1024*1024,
    int FileBytes=5*1024*1024,int FileQueueEntries=2048);

/// <summary>One bounded application journal. Capture never performs file I/O or touches WPF.</summary>
public sealed class DiagnosticJournal : IAsyncDisposable,IDisposable
{
    private sealed record Entry(long Sequence,string Text,int Bytes,JobDiagnosticEvent? Diagnostic=null);
    private readonly object _gate=new();
    private readonly LinkedList<Entry> _entries=new();
    private readonly Channel<Entry> _fileQueue;
    private readonly Task _writer;
    private readonly JournalLimits _limits;
    private readonly string _directory;
    private long _sequence,_uiNext=1,_droppedFileEntries,_reportedDropped;
    private int _memoryBytes,_profile;
    private bool _closed;
    private string _lastFileError="";
    private FileStream? _output;
    private FileStream? _structuredOutput;
    private readonly string _journalId=Guid.NewGuid().ToString("N");
    private long _omittedStructured;
    private long _queuedBytes;
    private string _structuredError="";
    public string FilePath { get; }
    internal string StructuredFilePath=>Path.Combine(_directory,"job-events.jsonl");
    internal long Sequence {get {lock(_gate)return _sequence;}}
    internal long OmittedStructuredEntries=>Interlocked.Read(ref _omittedStructured);
    internal long PendingFileBytes {get {lock(_gate)return Interlocked.Read(ref _queuedBytes);}}
    internal string StructuredFileError=>Volatile.Read(ref _structuredError);
    internal IReadOnlyList<JobDiagnosticEvent> SnapshotJobEvents()
        =>CaptureJobEvents().Events;
    internal (IReadOnlyList<JobDiagnosticEvent> Events,long Sequence) CaptureJobEvents()
    {lock(_gate)return (_entries.Where(entry=>entry.Diagnostic is not null).Select(entry=>entry.Diagnostic!).ToArray(),_sequence);}
    public string LastFileError => Volatile.Read(ref _lastFileError);
    public long DroppedFileEntries => Interlocked.Read(ref _droppedFileEntries);

    public DiagnosticJournal(string directory,JournalProfile profile=JournalProfile.Standard)
        : this(directory,profile,new JournalLimits()) { }

    internal DiagnosticJournal(string directory,JournalProfile profile,JournalLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if(limits.MemoryEntries<1 || limits.MemoryBytes<256 || limits.FileBytes<256 || limits.FileQueueEntries<1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _directory=Path.GetFullPath(directory);FilePath=Path.Combine(_directory,"journal.log");
        _limits=limits;_profile=(int)JournalProfileCatalog.Parse(JournalProfileCatalog.ToId(profile));
        _fileQueue=Channel.CreateBounded<Entry>(new BoundedChannelOptions(limits.FileQueueEntries)
        {
            SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations=false
        });
        _writer=Task.Run(WriteFilesAsync);
        Append(new JournalRecord(JournalLevel.Information,"journal","Профиль журнала: "+JournalProfileCatalog.Display(profile)),true);
    }

    public JournalProfile Profile
    {
        get => (JournalProfile)Volatile.Read(ref _profile);
        set
        {
            var normalized=JournalProfileCatalog.Parse(JournalProfileCatalog.ToId(value));
            if(Interlocked.Exchange(ref _profile,(int)normalized)==(int)normalized)return;
            Append(new JournalRecord(JournalLevel.Information,"journal","Профиль журнала: "+JournalProfileCatalog.Display(normalized)),true);
        }
    }
    public bool IsEnabled(JournalLevel level) => level switch
    {
        JournalLevel.Error or JournalLevel.Warning => true,
        JournalLevel.Information => Profile>=JournalProfile.Standard,
        JournalLevel.Progress => Profile>=JournalProfile.Progress,
        JournalLevel.Verbose => Profile==JournalProfile.Detailed,
        _ => false
    };
    public void Write(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if(IsEnabled(record.Level))Append(record,true);
    }
    public void WriteMessage(string legacy)
    {
        if(string.IsNullOrWhiteSpace(legacy))return;
        Write(new JournalRecord(Classify(legacy),"app",legacy));
    }
    private static JournalLevel Classify(string value)
    {
        string text=value.TrimStart();
        if(new[]{"WARNING","Transient error on ","Format unavailable at ","Preview ","Thumbnail "}
            .Any(prefix=>text.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))return JournalLevel.Warning;
        if(new[]{"ERROR","Failed:","Cannot ","Removal failed", "Dependency operation failed", "Shutdown cleanup failed",
                "Ошибка", "Не удалось", "System."}
            .Any(prefix=>text.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))return JournalLevel.Error;
        return JournalLevel.Information;
    }

    private string? Append(JournalRecord record,bool toFile)
    {
        // Localize before masking so the original retained diagnostic is masked too.
        string raw=record.Message ?? "";
        if(raw.Length>16*1024)raw=raw[..(16*1024)];
        string message=JournalSanitizer.Redact(ErrorMessages.ForLog(raw),multiline:true);
        string category=Context(record.Category),session=Context(record.Session),job=Context(record.Job),worker=Context(record.Worker);
        lock(_gate)
        {
            if(_closed && toFile)return null;
            long sequence=++_sequence;
            string context=(session.Length>0?" session="+session:"")+(job.Length>0?" job="+job:"")+(worker.Length>0?" worker="+worker:"");
            string timestamp=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture);
            string text=$"[{timestamp}] [{record.Level}/{category}]{context} {message}";
            int bytes=Encoding.UTF8.GetByteCount(text+Environment.NewLine);
            var diagnostic=JobDiagnosticCodec.Capture(record with {Message=raw},_journalId,sequence,DateTimeOffset.UtcNow);
            int diagnosticBytes=diagnostic is null?0:JobDiagnosticCodec.Encode(diagnostic).Length+1;
            if(diagnostic is not null && bytes+diagnosticBytes>_limits.MemoryBytes)
            {diagnostic=null;diagnosticBytes=0;Interlocked.Increment(ref _omittedStructured);}
            // The entry cap also works with small diagnostic limits used by the real I/O tests.
            if(bytes>_limits.MemoryBytes)
            {
                int length=Math.Min(text.Length,_limits.MemoryBytes/4-1);
                if(length>0 && char.IsHighSurrogate(text[length-1]))length--;
                text=text[..Math.Max(0,length)]+"…";bytes=Encoding.UTF8.GetByteCount(text+Environment.NewLine);
            }
            bytes+=diagnosticBytes;
            var captured=new Entry(sequence,text,bytes,diagnostic);
            _entries.AddLast(captured);_memoryBytes+=bytes;
            while(_entries.Count>_limits.MemoryEntries || _memoryBytes>_limits.MemoryBytes)
            {
                _memoryBytes-=_entries.First!.Value.Bytes;_entries.RemoveFirst();
            }
            if(toFile)
            {
                long queued=Interlocked.Add(ref _queuedBytes,captured.Bytes);
                if(queued>_limits.MemoryBytes*2L || !_fileQueue.Writer.TryWrite(captured))
                {Interlocked.Add(ref _queuedBytes,-captured.Bytes);Interlocked.Increment(ref _droppedFileEntries);}
            }
            return text;
        }
    }
    private static string Context(string? value)
    {
        string text=JournalSanitizer.Redact(value);
        return text.Length<=128?text:text[..128];
    }
    public string Snapshot()
    {
        lock(_gate)return _entries.Count==0?"":string.Join(Environment.NewLine,_entries.Select(entry=>entry.Text))+Environment.NewLine;
    }
    public IReadOnlyList<string> DrainPending(int max=200)
    {
        if(max<1)return [];
        lock(_gate)
        {
            var result=new List<string>(Math.Min(max,_entries.Count+1));
            if(_entries.First is {} first && _uiNext<first.Value.Sequence)
            {
                result.Add("[journal] Старые строки журнала удалены из памяти: "+(first.Value.Sequence-_uiNext)+". Доступные файлы журнала находятся в папке logs.");
                _uiNext=first.Value.Sequence;
            }
            foreach(var entry in _entries)
            {
                if(result.Count>=max)break;
                if(entry.Sequence<_uiNext)continue;
                result.Add(entry.Text);_uiNext=entry.Sequence+1;
            }
            return result;
        }
    }
    public void Clear()
    {
        lock(_gate){_entries.Clear();_memoryBytes=0;_uiNext=_sequence+1;}
    }

    private async Task WriteFilesAsync()
    {
        bool disabled=false;
        try
        {
            await foreach(var entry in _fileQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Interlocked.Add(ref _queuedBytes,-entry.Bytes);
                if(disabled)continue;
                try
                {
                    await WriteFileLineAsync(entry.Text,entry.Diagnostic).ConfigureAwait(false);
                    // Report pressure without recursively putting another item into the full queue.
                    long dropped=Interlocked.Read(ref _droppedFileEntries);
                    if(dropped>_reportedDropped)
                    {
                        long delta=dropped-_reportedDropped;_reportedDropped=dropped;
                        string warning="[journal] Очередь записи файла переполнена: пропущено "+delta+" записей (всего "+dropped+"). Уменьшите подробность журнала.";
                        Append(new JournalRecord(JournalLevel.Warning,"journal",warning),false);
                        await WriteFileLineAsync(warning,null).ConfigureAwait(false);
                    }
                }
                catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
                {
                    disabled=true;
                    string detail=JournalSanitizer.Redact(error.Message);
                    Volatile.Write(ref _lastFileError,"Не удалось записать файл журнала: "+detail);
                    Append(new JournalRecord(JournalLevel.Warning,"journal",LastFileError+". Журнал в памяти и копирование остаются доступны."),false);
                    try{await CloseOutputsAsync().ConfigureAwait(false);}
                    catch(Exception closeError) when(closeError is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        finally
        {
            try{await CloseOutputsAsync().ConfigureAwait(false);}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException)
            {Volatile.Write(ref _lastFileError,"Не удалось закрыть файл журнала: "+JournalSanitizer.Redact(error.Message));}
        }
    }
    private async Task CloseOutputsAsync()
    {
        var text=_output;var structured=_structuredOutput;_output=null;_structuredOutput=null;
        try{if(text is not null)await text.DisposeAsync().ConfigureAwait(false);}
        finally{if(structured is not null)await structured.DisposeAsync().ConfigureAwait(false);}
    }
    private async Task WriteFileLineAsync(string line,JobDiagnosticEvent? diagnostic)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(line+Environment.NewLine);
        if(bytes.Length>_limits.FileBytes)
        {
            int chars=Math.Max(0,(_limits.FileBytes-16)/4);
            if(chars>0 && char.IsHighSurrogate(line[chars-1]))chars--;
            bytes=Encoding.UTF8.GetBytes(line[..Math.Min(chars,line.Length)]+"…"+Environment.NewLine);
        }
        byte[] typed=diagnostic is null?Array.Empty<byte>():JobDiagnosticCodec.Encode(diagnostic).Concat(new byte[]{10}).ToArray();
        if(bytes.Length+typed.Length>_limits.FileBytes)
        {typed=Array.Empty<byte>();Interlocked.Increment(ref _omittedStructured);}
        long ExistingSize(string path)=>File.Exists(path)?new FileInfo(path).Length:0;
        if(_output!=null && _output.Length+(_structuredOutput?.Length??ExistingSize(StructuredFilePath))+bytes.Length+typed.Length>_limits.FileBytes)
            await CloseOutputsAsync().ConfigureAwait(false);
        if(_output==null)
        {
            RefuseLinkedAncestors(_directory);
            Directory.CreateDirectory(_directory);
            RefuseLinkedAncestors(_directory);
            foreach(string name in new[]{"journal.log","journal.1.log","journal.2.log","job-events.jsonl","job-events.1.jsonl","job-events.2.jsonl"})RefuseLink(Path.Combine(_directory,name));
            if(ExistingSize(FilePath)+ExistingSize(StructuredFilePath)+bytes.Length+typed.Length>_limits.FileBytes)
            {
                foreach(var (stem,extension) in new[]{("journal","log"),("job-events","jsonl")})
                {
                    string current=Path.Combine(_directory,stem+"."+extension),previous=Path.Combine(_directory,stem+".1."+extension),oldest=Path.Combine(_directory,stem+".2."+extension);
                    if(File.Exists(oldest))File.Delete(oldest);
                    if(File.Exists(previous))File.Move(previous,oldest);
                    if(File.Exists(current))File.Move(current,previous);
                }
            }
            _output=new FileStream(FilePath,FileMode.Append,FileAccess.Write,FileShare.Read|FileShare.Delete,4096,FileOptions.Asynchronous);
        }
        await _output.WriteAsync(bytes).ConfigureAwait(false);
        // Flush each bounded record in the background, so a diagnostic survives a process crash.
        await _output.FlushAsync().ConfigureAwait(false);
        if(typed.Length>0)
        {
            try
            {
                RefuseLink(StructuredFilePath);
                _structuredOutput??=new FileStream(StructuredFilePath,FileMode.Append,FileAccess.Write,FileShare.Read|FileShare.Delete,4096,FileOptions.Asynchronous);
                await _structuredOutput.WriteAsync(typed).ConfigureAwait(false);
                await _structuredOutput.FlushAsync().ConfigureAwait(false);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Interlocked.Increment(ref _omittedStructured);
                Volatile.Write(ref _structuredError,"Структурированный архив недоступен; доступны события из памяти.");
                if(_structuredOutput is not null)
                {try{await _structuredOutput.DisposeAsync().ConfigureAwait(false);}catch(IOException){ }finally{_structuredOutput=null;}}
            }
        }
    }
    private static void RefuseLink(string path)
    {
        // LinkTarget also detects a dangling link, for which Exists is false.
        var info=new FileInfo(path);
        if(info.LinkTarget!=null || ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0))
            throw new IOException("Путь журнала является символической ссылкой: "+path);
    }
    private static void RefuseLinkedAncestors(string path)
    {
        for(DirectoryInfo? current=new DirectoryInfo(path);current!=null;current=current.Parent)
            RefuseLink(current.FullName);
    }
    private Task Complete()
    {
        lock(_gate)
        {
            if(!_closed){_closed=true;_fileQueue.Writer.TryComplete();}
            return _writer;
        }
    }
    public async ValueTask DisposeAsync()=>await Complete().ConfigureAwait(false);
    public void Dispose()
    {
        // NativeWindow's asynchronous closing path can await DisposeAsync. A
        // defensive synchronous Dispose still never blocks the dispatcher indefinitely.
        try{Complete().Wait(TimeSpan.FromSeconds(2));}
        catch(AggregateException) { }
    }
}
