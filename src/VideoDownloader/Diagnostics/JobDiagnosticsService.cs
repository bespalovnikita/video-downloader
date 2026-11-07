using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

/// <summary>Optional, bounded snapshots. No method changes a queue or profile.</summary>
internal sealed class JobDiagnosticsService
{
    internal const int MaxEvents=2000,MaxSnapshotBytes=2*1024*1024,MaxReadBytes=15*1024*1024,MaxExportBytes=8*1024*1024;
    private static readonly SemaphoreSlim ReadSlots=new(2,2);
    private readonly DiagnosticJournal _journal;
    private readonly string _ytDlp;
    private readonly Func<CancellationToken,Task<IReadOnlyList<DiagnosticComponentVersion>>>? _versions;
    internal JobDiagnosticsService(DiagnosticJournal journal,string ytDlp,
        Func<CancellationToken,Task<IReadOnlyList<DiagnosticComponentVersion>>>? versions=null)
    {_journal=journal;_ytDlp=ytDlp;_versions=versions;}

    internal async Task<JobDiagnosticSnapshot> ReadAsync(JobDiagnosticTarget target,CancellationToken cancellationToken)
    {
        ValidateTarget(target);cancellationToken.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var pending=Task.Run(()=>ReadCoreAsync(target,deadline.Token),CancellationToken.None);
        try{return await pending.WaitAsync(deadline.Token).ConfigureAwait(false);}
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
        {Observe(pending);throw;}
        catch(OperationCanceledException)
        {
            Observe(pending);var fallback=new SnapshotBuilder(target);
            var memory=_journal.CaptureJobEvents();foreach(var entry in memory.Events)fallback.Add(entry);
            fallback.Warnings.Add("Чтение файлов не завершилось за пять секунд; показаны события из памяти.");
            fallback.Truncated=true;return fallback.Build(_journal,memory.Sequence);
        }
    }
    private static void Observe(Task task)=>_=task.ContinueWith(done=>_ = done.Exception,CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);

    private async Task<JobDiagnosticSnapshot> ReadCoreAsync(JobDiagnosticTarget target,CancellationToken token)
    {
        await ReadSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result=new SnapshotBuilder(target);long remaining=MaxReadBytes;bool found=false;
            string directory=Path.GetDirectoryName(_journal.FilePath)!;
            foreach(string name in new[]{"job-events.2.jsonl","job-events.1.jsonl","job-events.jsonl"})
            {
                token.ThrowIfCancellationRequested();string path=Path.Combine(directory,name);
                try
                {
                    OwnedFiles.NoReparseAncestors(path);OwnedFiles.RegularOrAbsent(path);
                    using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,8192,FileOptions.Asynchronous);
                    found=true;long wanted=Math.Min(file.Length,remaining);
                    if(file.Length>wanted)result.Truncated=true;
                    byte[] buffer=new byte[8192];using var line=new MemoryStream();bool tooLong=false;
                    while(wanted>0)
                    {
                        int read=await file.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,wanted)),token).ConfigureAwait(false);
                        if(read==0)break;wanted-=read;remaining-=read;
                        for(int index=0;index<read;index++)
                        {
                            if(buffer[index]==10)
                            {
                                if(tooLong)result.Malformed++;else result.Line(line.ToArray());
                                line.SetLength(0);tooLong=false;
                            }
                            else if(!tooLong)
                            {
                                if(line.Length>=JobDiagnosticCodec.MaxLineBytes){tooLong=true;line.SetLength(0);}
                                else line.WriteByte(buffer[index]);
                            }
                        }
                    }
                    if(tooLong)result.Malformed++;else if(line.Length>0)result.Line(line.ToArray());
                    if(remaining<=0){result.Truncated=true;break;}
                }
                catch(FileNotFoundException){ }
                catch(DirectoryNotFoundException){ }
                catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {result.Truncated=true;result.Warnings.Add("Один из файлов структурированного журнала недоступен; показаны остальные события.");}
            }
            token.ThrowIfCancellationRequested();
            var memory=_journal.CaptureJobEvents();foreach(var entry in memory.Events)result.Add(entry);
            if(!found)result.Warnings.Add("Старые текстовые журналы не индексируются: история попыток доступна только для новых структурированных событий.");
            return result.Build(_journal,memory.Sequence);
        }
        finally{ReadSlots.Release();}
    }

    private sealed class SnapshotBuilder(JobDiagnosticTarget target)
    {
        private readonly LinkedList<(JobDiagnosticEvent Event,int Bytes)> _entries=new();
        private readonly HashSet<(string Journal,long Sequence)> _keys=[];
        private int _bytes,_omitted;
        internal int Malformed;
        internal bool Truncated;
        internal readonly List<string> Warnings=[];
        internal void Line(byte[] bytes)
        {
            if(bytes.Length==0)return;
            var entry=JobDiagnosticCodec.Decode(bytes);
            if(entry is null){Malformed++;return;}Add(entry);
        }
        internal void Add(JobDiagnosticEvent entry)
        {
            if(entry.Identity!=target.Identity || entry.Job!=target.Job || !_keys.Add((entry.Journal,entry.Sequence)))return;
            int bytes=JobDiagnosticCodec.Encode(entry).Length+1;
            _entries.AddLast((entry,bytes));_bytes+=bytes;
            while(_entries.Count>MaxEvents || _bytes>MaxSnapshotBytes)
            {
                var first=_entries.First!.Value;_entries.RemoveFirst();_bytes-=first.Bytes;
                _keys.Remove((first.Event.Journal,first.Event.Sequence));_omitted++;
            }
        }
        internal JobDiagnosticSnapshot Build(DiagnosticJournal journal,long observedSequence)
        {
            var events=_entries.Select(value=>value.Event).GroupBy(value=>value.Journal)
                .OrderBy(group=>group.Min(value=>value.CapturedAt)).ThenBy(group=>group.Key,StringComparer.Ordinal)
                .SelectMany(group=>group.OrderBy(value=>value.Sequence)).ToArray();
            Warnings.Add("Показаны сохранившиеся события выбранной ссылки. Профиль журнала и ротация ограничивают доступную историю.");
            if(journal.DroppedFileEntries>0)Warnings.Add("Очередь записи ранее пропустила событий: "+journal.DroppedFileEntries+" (по всему журналу).");
            if(journal.OmittedStructuredEntries>0)Warnings.Add("Структурированных событий не сохранено: "+journal.OmittedStructuredEntries+" (по всему журналу).");
            if(journal.StructuredFileError.Length>0)Warnings.Add(journal.StructuredFileError);
            if(journal.LastFileError.Length>0)Warnings.Add("Файловая запись журнала недоступна; часть событий может оставаться только в памяти.");
            if(_omitted>0)Warnings.Add("Из выборки исключены более старые события: "+_omitted+". Предел: 2000 событий / 2 МиБ.");
            if(Malformed>0)Warnings.Add("Не удалось разобрать записей архива: "+Malformed+". Полнота выборки не подтверждена.");
            if(events.Length>0 && !events.Any(entry=>entry.Event=="job_queued"))
                Warnings.Add("Начальная запись задачи отсутствует: сохранённая история может быть неполной.");
            return new(target,Array.AsReadOnly(events),Array.AsReadOnly(Warnings.Distinct(StringComparer.Ordinal).ToArray()),_omitted,Malformed,
                Truncated || _omitted>0 || Malformed>0 || journal.DroppedFileEntries>0 || journal.OmittedStructuredEntries>0,observedSequence);
        }
    }

    internal async Task<DiagnosticExportResult> ExportAsync(JobDiagnosticTarget target,JobDiagnosticSnapshot snapshot,
        string destination,CancellationToken cancellationToken,bool overwrite=false)
    {
        ValidateTarget(target);cancellationToken.ThrowIfCancellationRequested();
        if(snapshot.Target.Identity!=target.Identity || snapshot.Events.Count>MaxEvents ||
            snapshot.Events.Any(entry=>entry.Identity!=target.Identity || entry.Job!=target.Job))
            throw new InvalidDataException("Пакет не относится к выбранному заданию.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var versions=await Task.Run(()=>_versions is null?ProbeVersionsAsync(deadline.Token):_versions(deadline.Token),CancellationToken.None).ConfigureAwait(false);
        return await Task.Run(async()=>
        {
            string path=Path.GetFullPath(destination);RejectNetworkDestination(path);
            OwnedFiles.NoReparseAncestors(path);OwnedFiles.RegularOrAbsent(path);
            string directory=Path.GetDirectoryName(path)??throw new IOException("Выберите папку для ZIP.");
            if(!Directory.Exists(directory))throw new DirectoryNotFoundException("Папка для ZIP не найдена.");
            if(!overwrite && File.Exists(path))throw new IOException("Файл уже существует. Выберите новое имя.");
            var safeEvents=snapshot.Events.Select(entry=>JobDiagnosticCodec.Decode(JobDiagnosticCodec.Encode(entry))
                ??throw new InvalidDataException("Некорректное диагностическое событие.")).ToArray();
            var safeTarget=target with {Label=DiagnosticPrivacy.Text(target.Label),Url=DiagnosticPrivacy.Text(target.Url),
                Status=DiagnosticPrivacy.Text(target.Status),Error=DiagnosticPrivacy.Text(target.Error,true)};
            var safeVersions=versions.Where(component=>component.Component is "Video Downloader" or "yt-dlp" or "FFmpeg" or "FFprobe")
                .DistinctBy(component=>component.Component).Take(4).Select(component=>new DiagnosticComponentVersion(
                component.Component,component.Status is "Доступен" or "Не установлен" or "Ошибка проверки версии" or "Версия неизвестна" or
                    "Таймаут проверки версии" or "Проверка недоступна"?component.Status:"Версия неизвестна",
                SafeVersion(component.Component,component.Version))).ToArray();
            string jsonl=string.Join('\n',safeEvents.Select(entry=>JsonSerializer.Serialize(entry,JobDiagnosticCodec.Json)))+(safeEvents.Length>0?"\n":"");
            var manifest=new
            {
                schema=1,application="Video Downloader",applicationVersion=SafeVersion("Video Downloader",ApplicationVersion),
                capturedAtUtc=DateTimeOffset.UtcNow,selection=safeTarget,eventCount=safeEvents.Length,
                omittedEvents=snapshot.OmittedEvents,malformedRecords=snapshot.MalformedRecords,truncated=snapshot.Truncated,
                warnings=snapshot.Warnings.Select(value=>DiagnosticPrivacy.Text(value)),components=safeVersions,
                contents=new[]{"manifest.json","events.jsonl","journal.txt","settings.json","error.txt"}
            };
            var files=new Dictionary<string,byte[]>(StringComparer.Ordinal)
            {
                ["manifest.json"]=JsonSerializer.SerializeToUtf8Bytes(manifest,JobDiagnosticCodec.Json),
                ["events.jsonl"]=Encoding.UTF8.GetBytes(jsonl),
                ["journal.txt"]=Encoding.UTF8.GetBytes(string.Join(Environment.NewLine,safeEvents.Select(entry=>entry.DisplayText))),
                ["settings.json"]=JsonSerializer.SerializeToUtf8Bytes(safeEvents.Where(entry=>entry.Settings is not null)
                    .Select(entry=>new{entry.Journal,entry.Session,entry.Invocation,entry.Attempt,entry.Settings}),JobDiagnosticCodec.Json),
                ["error.txt"]=Encoding.UTF8.GetBytes(safeTarget.Error.Length==0?"На момент открытия строки ошибка не была сохранена. Доступные ошибки перечислены в событиях.":
                    DiagnosticPrivacy.Text(ErrorMessages.ForDisplay(safeTarget.Error),true)+Environment.NewLine+"Исходная диагностика:"+Environment.NewLine+safeTarget.Error)
            };
            if(files.Sum(value=>(long)value.Value.Length)>MaxExportBytes)throw new IOException("Пакет диагностики превышает 8 МиБ.");
            string temporary=Path.Combine(directory,".vd-diagnostics-"+Guid.NewGuid().ToString("N")+".tmp");bool owned=false;
            try
            {
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,FileOptions.Asynchronous))
                {
                    owned=true;
                    using(var archive=new ZipArchive(output,ZipArchiveMode.Create,leaveOpen:true))
                    {
                        foreach(var (name,bytes) in files)
                        {
                            deadline.Token.ThrowIfCancellationRequested();var entry=archive.CreateEntry(name,CompressionLevel.Optimal);
                            using var stream=entry.Open();await stream.WriteAsync(bytes,deadline.Token).ConfigureAwait(false);
                        }
                    }
                    await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                deadline.Token.ThrowIfCancellationRequested();
                OwnedFiles.NoReparseAncestors(path);OwnedFiles.RegularOrAbsent(path);
                File.Move(temporary,path,overwrite);owned=false;
                return new DiagnosticExportResult(safeEvents.Length,new FileInfo(path).Length,Array.AsReadOnly(safeVersions));
            }
            finally
            {
                if(owned)
                    try{OwnedFiles.RegularOrAbsent(temporary);File.Delete(temporary);}
                    catch(IOException){ }catch(UnauthorizedAccessException){ }
            }
        },CancellationToken.None).ConfigureAwait(false);
    }

    internal static string ApplicationVersion=>typeof(JobDiagnosticsService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ??typeof(JobDiagnosticsService).Assembly.GetName().Version?.ToString()??"неизвестно";
    private static void ValidateTarget(JobDiagnosticTarget target)
    {
        if(target.Identity.Length!=64 || !target.Identity.All(c=>c is >= 'a' and <= 'f' or >= '0' and <= '9') || target.Job!=target.Identity[..12])
            throw new ArgumentException("Некорректная идентичность задания.");
    }
    private static void RejectNetworkDestination(string path)
    {
        if(path.StartsWith(@"\\",StringComparison.Ordinal) ||
            OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(path)!).DriveType==DriveType.Network)
            throw new IOException("Для пакета диагностики выберите локальную папку.");
    }
    private async Task<IReadOnlyList<DiagnosticComponentVersion>> ProbeVersionsAsync(CancellationToken token)
    {
        var values=new List<DiagnosticComponentVersion>{new("Video Downloader","Доступен",ApplicationVersion)};
        foreach(var (name,path,argument) in new[]{("yt-dlp",_ytDlp,"--version"),
            ("FFmpeg",DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg"),"-version"),
            ("FFprobe",DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe"),"-version")})
            values.Add(await ProbeVersionAsync(name,path,argument,token).ConfigureAwait(false));
        return values.AsReadOnly();
    }
    internal static async Task<DiagnosticComponentVersion> ProbeVersionAsync(string name,string path,string argument,CancellationToken token)
    {
        return await Task.Run(async()=>
        {
            token.ThrowIfCancellationRequested();
            if(!File.Exists(path))return new DiagnosticComponentVersion(name,"Не установлен","");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var start=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                start.ArgumentList.Add(argument);using var child=ManagedChildProcess.Start(start);
                var output=await BoundedProcessOutput.ReadAsync(child,8192,8192,deadline.Token).ConfigureAwait(false);
                if(child.Process.ExitCode!=0)return new DiagnosticComponentVersion(name,"Ошибка проверки версии","");
                string first=Encoding.UTF8.GetString(output.StandardOutput).Split('\n')[0].Trim();
                string version=name=="yt-dlp"?first:Regex.Match(first,@"\bversion\s+(\S+)",RegexOptions.CultureInvariant).Groups[1].Value;
                string safe=SafeVersion(name,version);
                return new DiagnosticComponentVersion(name,safe.Length>0?"Доступен":"Версия неизвестна",safe);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(OperationCanceledException){return new DiagnosticComponentVersion(name,"Таймаут проверки версии","");}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {return new DiagnosticComponentVersion(name,"Проверка недоступна","");}
        },CancellationToken.None).ConfigureAwait(false);
    }
    private static string SafeVersion(string name,string? value)
    {
        if(value is not {Length:>0 and <=96})return "";
        string pattern=name=="yt-dlp"?@"\A\d{4}\.\d{2}\.\d{2}(?:\.[0-9]+)?\z":
            @"\A(?:\d+\.\d+(?:\.\d+)?(?:[-+._A-Za-z0-9]*)?|N-\d+-g[0-9a-fA-F]+)\z";
        return Regex.IsMatch(value,pattern,RegexOptions.CultureInvariant)?value:"";
    }
}
