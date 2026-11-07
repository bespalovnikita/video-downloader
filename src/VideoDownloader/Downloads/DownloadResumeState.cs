using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

// Only the download plan is retained. UI layout, clipboard/schedule settings,
// secret cookie contents and resolved media URLs never belong to this record.
public sealed record DownloadResumePlan
{
    public string OutputRoot { get; init; }="";
    public string Quality { get; init; }="Best";
    public string Codec { get; init; }="Auto";
    public string Container { get; init; }="Auto";
    public string RateLimit { get; init; }="Unlimited";
    public int FragmentCount { get; init; }=4;
    public string FilenameTemplate { get; init; }="%(title)s [%(id)s].%(ext)s";
    public string Cookies { get; init; }="";
    public string SubtitleLangs { get; init; }="ru.*,en.*";
    public string AudioTrack { get; init; }="Default";
    public string AudioLanguages { get; init; }="ru";
    public bool WriteSubs { get; init; }
    public bool AutoSubs { get; init; }
    public bool EmbedSubs { get; init; }
    public bool EmbedThumbnail { get; init; }
    public bool EmbedMetadata { get; init; }=true;
    public bool EmbedChapters { get; init; }=true;
    public bool Sponsor { get; init; }
    public bool AudioOnly { get; init; }

    public DownloadOptions ToOptions()=>new()
    {
        Output=OutputRoot,Quality=Quality,Codec=Codec,Container=Container,RateLimit=RateLimit,
        Fragments=FragmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FilenameTemplate=FilenameTemplate,Cookies=Cookies,SubtitleLangs=SubtitleLangs,
        AudioTrack=AudioTrack,AudioLanguages=AudioLanguages,WriteSubs=WriteSubs,AutoSubs=AutoSubs,
        EmbedSubs=EmbedSubs,EmbedThumbnail=EmbedThumbnail,EmbedMetadata=EmbedMetadata,
        EmbedChapters=EmbedChapters,Sponsor=Sponsor,AudioOnly=AudioOnly
    };
    internal static DownloadResumePlan Capture(DownloadOptions options,string root)=>new()
    {
        OutputRoot=root,Quality=options.Quality,Codec=options.Codec,Container=options.Container,
        RateLimit=options.RateLimit,FragmentCount=options.FragmentCount,FilenameTemplate=options.FilenameTemplate,
        Cookies=options.Cookies,SubtitleLangs=options.SubtitleLangs,AudioTrack=options.AudioTrack,
        AudioLanguages=options.AudioLanguages,WriteSubs=options.WriteSubs,AutoSubs=options.AutoSubs,
        EmbedSubs=options.EmbedSubs,EmbedThumbnail=options.EmbedThumbnail,EmbedMetadata=options.EmbedMetadata,
        EmbedChapters=options.EmbedChapters,Sponsor=options.Sponsor,AudioOnly=options.AudioOnly
    };
}

public sealed record DownloadResumeState
{
    public int Version { get; init; }=1;
    public string Token { get; init; }="";
    public string Identity { get; init; }="";
    public DownloadResumePlan Plan { get; init; }=new();
    public DownloadSelection? Selection { get; init; }
    // Hashes of component IDs, never extractor values or URLs. Immutable
    // replacement keeps checkpoint serialization independent from pipe reads.
    public IReadOnlyDictionary<string,long> ComponentBytes { get; init; }=new Dictionary<string,long>();
}

internal static class DownloadResumeStore
{
    private const int MaximumOwnerBytes=128*1024;
    private static readonly StringComparison PathComparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
    internal sealed record Owner(int Version,string Token,string Identity,string PlanHash,int ProcessId,DateTime StartedUtc,string Quality,string RunDir,string StageDir);
    internal sealed record Validated(DownloadResumeState State,string Quality);
    private static string Identity(string url)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
    private static string Hash(DownloadResumeState state)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new{state.Version,state.Token,state.Identity,state.Plan,state.Selection},AppPersistence.Json)));
    internal static string ComponentIdentity(string value)=>Identity(value);
    private static string Marker(DownloadItem row)=>row.StageDir+".resume.json";

    internal static void ValidatePersisted(DownloadItem row)
    {
        if(row.ResumeState is not {} state)return;
        if(state.Version!=1 || state.Plan is null || !Regex.IsMatch(state.Token,@"\A[0-9a-f]{32}\z",RegexOptions.CultureInvariant) ||
            state.Identity!=Identity(row.Url) || string.IsNullOrWhiteSpace(state.Plan.OutputRoot) ||
            !Path.IsPathFullyQualified(state.Plan.OutputRoot) || state.Plan.FragmentCount is <1 or >8)
            throw new InvalidDataException("Retained download state is invalid.");
        string?[] values=[state.Plan.Quality,state.Plan.Codec,state.Plan.Container,state.Plan.RateLimit,state.Plan.FilenameTemplate,
            state.Plan.Cookies,state.Plan.SubtitleLangs,state.Plan.AudioTrack,state.Plan.AudioLanguages];
        if(values.Any(value=>value is null || value.Length>32767))throw new InvalidDataException("Retained download options are invalid.");
        OwnedFiles.ValidateTemplate(state.Plan.FilenameTemplate);
        AudioTrackSelection.Validate(state.Plan.ToOptions());
        state.Selection?.ValidatePersisted(row.Url);
        if(JsonSerializer.Serialize(state.Selection,AppPersistence.Json)!=JsonSerializer.Serialize(row.Selection,AppPersistence.Json))
            throw new InvalidDataException("The retained download belongs to a different format or fragment. Reset it before changing the selection.");
        if(state.ComponentBytes is null || state.ComponentBytes.Count>2048 || state.ComponentBytes.Any(pair=>pair.Value<0 ||
            !Regex.IsMatch(pair.Key,@"\A[0-9A-F]{64}\z",RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Retained component counters are invalid.");
        ValidatePaths(row);
    }

    private static void ValidatePaths(DownloadItem row)
    {
        if(string.IsNullOrWhiteSpace(row.RunDir) || string.IsNullOrWhiteSpace(row.StageDir) ||
            !Path.IsPathFullyQualified(row.RunDir) || !Path.IsPathFullyQualified(row.StageDir))
            throw new InvalidDataException("Retained staging paths are invalid.");
        string run=Path.TrimEndingDirectorySeparator(Path.GetFullPath(row.RunDir));
        string stage=Path.TrimEndingDirectorySeparator(Path.GetFullPath(row.StageDir));
        if(!Regex.IsMatch(Path.GetFileName(run),@"\Avd-native-[0-9a-f]{32}\z",RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(Path.GetFileName(stage),@"\A[0-9a-f]{32}\z",RegexOptions.CultureInvariant) ||
            !string.Equals(Path.GetDirectoryName(stage),run,PathComparison))
            throw new InvalidDataException("Retained staging does not belong to a native download.");
    }

    internal static Validated Validate(DownloadItem row,bool requireStage=true)
    {
        ValidatePersisted(row);
        var state=row.ResumeState??throw new InvalidDataException("No retained download state is available.");
        OwnedFiles.NoReparseAncestors(Path.Combine(row.StageDir,"__check__"));
        if(requireStage && !Directory.Exists(row.StageDir))throw new IOException("The retained download data is no longer available. Reset the item to start again.");
        if(Directory.Exists(row.StageDir))
            foreach(string entry in Directory.EnumerateFileSystemEntries(row.StageDir,"*",SearchOption.AllDirectories))
                if((File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0)throw new IOException("Retained staging contains a reparse point.");
        string marker=Marker(row);OwnedFiles.RegularOrAbsent(marker);
        using var input=new FileStream(marker,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>MaximumOwnerBytes)throw new InvalidDataException("Retained ownership proof is too large.");
        var owner=JsonSerializer.Deserialize<Owner>(input,AppPersistence.Json)??throw new InvalidDataException("Retained ownership proof is empty.");
        if(owner.Version!=1 || owner.Token!=state.Token || owner.Identity!=state.Identity || owner.PlanHash!=Hash(state) ||
            string.IsNullOrWhiteSpace(owner.Quality) || owner.Quality.Length>64 || owner.ProcessId<=0 || owner.StartedUtc==default ||
            !string.Equals(owner.RunDir,row.RunDir,PathComparison) || !string.Equals(owner.StageDir,row.StageDir,PathComparison))
            throw new InvalidDataException("Retained ownership proof does not match this download.");
        if(owner.ProcessId!=Environment.ProcessId && OwnerAlive(owner.ProcessId,owner.StartedUtc))
            throw new IOException("The previous owner of this retained download is still running.");
        return new(state,owner.Quality);
    }

    private static bool OwnerAlive(int pid,DateTime started)
    {
        try{using var process=Process.GetProcessById(pid);return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime()-started).TotalSeconds)<2;}
        catch(ArgumentException){return false;}
        catch(Exception error) when(error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {throw new IOException("The previous retained-download owner could not be verified.",error);}
    }

    internal static void Create(DownloadItem row,DownloadOptions options,DownloadSelection? selection,string quality)
    {
        if(row.ResumeState is not null)throw new InvalidOperationException("A retained state already exists.");
        var state=new DownloadResumeState{Token=Guid.NewGuid().ToString("N"),Identity=Identity(row.Url),
            Plan=DownloadResumePlan.Capture(options,row.OutputRoot),Selection=selection};
        row.ResumeState=state;
        bool created=false;
        try
        {
            ValidatePersisted(row);OwnedFiles.RegularOrAbsent(Marker(row));
            using var output=new FileStream(Marker(row),FileMode.CreateNew,FileAccess.Write,FileShare.None);
            created=true;
            var owner=MakeOwner(row,state,quality);
            JsonSerializer.Serialize(output,owner,AppPersistence.Json);output.Flush(true);
        }
        catch(Exception error)
        {
            if(created)
            {
                try{OwnedFiles.RegularOrAbsent(Marker(row));File.Delete(Marker(row));}
                catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException)
                {
                    // Keep the exact stage/proof references if even unlinking
                    // this call's newly created marker fails.
                    DownloadCleanup.Failed(row,"Could not remove an incomplete retained ownership proof: "+cleanup.Message);
                    throw new IOException("Retained ownership proof creation and cleanup failed.",new AggregateException(error,cleanup));
                }
            }
            row.ResumeState=null;throw;
        }
    }

    internal static void UpdateQuality(DownloadItem row,string quality)
    {
        var validated=Validate(row);var state=validated.State;
        var owner=MakeOwner(row,state,quality);
        AppPersistence.AtomicWrite(Marker(row),JsonSerializer.Serialize(owner,AppPersistence.Json));
    }

    private static Owner MakeOwner(DownloadItem row,DownloadResumeState state,string quality)
    {
        using var current=Process.GetCurrentProcess();
        return new(1,state.Token,state.Identity,Hash(state),Environment.ProcessId,current.StartTime.ToUniversalTime(),quality,row.RunDir,row.StageDir);
    }

    // This helper deliberately does not touch any published media.
    internal static void DeleteRetained(DownloadItem row)
    {
        if(row.ResumeState is null)return;
        ValidatePersisted(row);
        OwnedFiles.NoReparseAncestors(Path.Combine(row.StageDir,"__check__"));
        // A removed TEMP tree leaves no files to authorize or delete. This is
        // metadata-only recovery; an existing stage without proof still fails.
        if(Absent(row.StageDir) && Absent(Marker(row)))
        {row.StageDir="";row.ResumeState=null;return;}
        Validate(row,requireStage:false);
        OwnedFiles.DeleteStage(row.StageDir,row.RunDir);
        DeleteProof(row);
        row.StageDir="";row.ResumeState=null;
    }

    private static bool Absent(string path)
    {
        try{_ = File.GetAttributes(path);return false;}
        catch(FileNotFoundException){return true;}
        catch(DirectoryNotFoundException){return true;}
    }

    internal static void DeleteProof(DownloadItem row)
    {
        if(row.ResumeState is null)return;
        string marker=Marker(row);OwnedFiles.RegularOrAbsent(marker);
        // Validate the marker before unlinking it, even if stage cleanup has
        // already removed the directory. Missing stage never grants ownership.
        using(var input=new FileStream(marker,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            if(input.Length>MaximumOwnerBytes)throw new InvalidDataException("Retained ownership proof is too large.");
            var owner=JsonSerializer.Deserialize<Owner>(input,AppPersistence.Json);
            if(owner?.Token!=row.ResumeState.Token || owner.Identity!=row.ResumeState.Identity || owner.PlanHash!=Hash(row.ResumeState) ||
                !string.Equals(owner.RunDir,row.RunDir,PathComparison) || !string.Equals(owner.StageDir,row.StageDir,PathComparison))
                throw new InvalidDataException("Retained ownership proof changed before cleanup.");
        }
        File.Delete(marker);
    }
}
