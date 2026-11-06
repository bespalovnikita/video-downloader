using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

public sealed record AppPackageFile(string Path,long Size,string Sha256);
public sealed record AppPackageManifest(int Schema,string Version,List<AppPackageFile> Files);

public static class AppUpdatePackage
{
    public const string ManifestName="windows-package.json";
    public const long MaxPackageBytes=1024L*1024*1024;
    public const int MaxFiles=2048;
    public static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
    public static Version ParseVersion(string text)
    {
        if(!Regex.IsMatch(text,@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$") ||
            !Version.TryParse(text,out var version))throw new InvalidDataException("Invalid application release version.");
        return version;
    }
    public static string ValidateRelativePath(string relative)
    {
        if(string.IsNullOrEmpty(relative) || relative.Length>240 || relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new InvalidDataException("Invalid package path.");
        string path=relative.Replace('\\','/');
        foreach(string part in path.Split('/'))
            if(part.Length==0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c=>c<32 || "<>:\"|?*".Contains(c)) ||
                Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",RegexOptions.IgnoreCase))
                throw new InvalidDataException("Unsafe package path.");
        bool allowed=path.Equals("VideoDownloader.exe",StringComparison.OrdinalIgnoreCase) ||
            path.Equals("yt-dlp.exe",StringComparison.OrdinalIgnoreCase) ||
            path.Equals(ManifestName,StringComparison.OrdinalIgnoreCase) ||
            (!path.Contains('/') && (path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) ||
                path.Equals("VideoDownloader.pdb",StringComparison.OrdinalIgnoreCase) ||
                path.Equals("VideoDownloader.deps.json",StringComparison.OrdinalIgnoreCase) ||
                path.Equals("VideoDownloader.runtimeconfig.json",StringComparison.OrdinalIgnoreCase))) ||
            (path.StartsWith("ui/",StringComparison.OrdinalIgnoreCase) && path.EndsWith(".xaml",StringComparison.OrdinalIgnoreCase)) ||
            (path.StartsWith("assets/",StringComparison.OrdinalIgnoreCase) && path.EndsWith(".ico",StringComparison.OrdinalIgnoreCase)) ||
            (path.StartsWith("collection-plugins/",StringComparison.OrdinalIgnoreCase) && path.EndsWith(".py",StringComparison.OrdinalIgnoreCase));
        if(!allowed)throw new InvalidDataException("File is outside the application package: "+path);
        return path;
    }
    public static string HashFile(string path)
    {
        using var stream=File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static AppPackageManifest ReadManifest(string path)
    {
        AssertNoReparse(path);
        if(new FileInfo(path).Length>1024*1024)throw new InvalidDataException("Package manifest is too large.");
        var manifest=JsonSerializer.Deserialize<AppPackageManifest>(File.ReadAllBytes(path),Json)
            ??throw new InvalidDataException("Empty package manifest.");
        ValidateManifest(manifest);
        return manifest;
    }
    public static void ValidateManifest(AppPackageManifest manifest)
    {
        if(manifest.Schema!=1 || manifest.Files is null || manifest.Files.Count is <1 or >MaxFiles)
            throw new InvalidDataException("Invalid package manifest schema or file count.");
        ParseVersion(manifest.Version);
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total=0;
        foreach(var file in manifest.Files)
        {
            string name=ValidateRelativePath(file.Path);
            if(name.Equals(ManifestName,StringComparison.OrdinalIgnoreCase) || !names.Add(name) || file.Size<=0 ||
                file.Size>MaxPackageBytes || !Regex.IsMatch(file.Sha256??"",@"^[0-9a-f]{64}$"))
                throw new InvalidDataException("Invalid or duplicate package manifest file.");
            total=checked(total+file.Size);
        }
        if(total>MaxPackageBytes || !names.Contains("VideoDownloader.exe") || !names.Contains("yt-dlp.exe") ||
            !names.Contains("ui/MainWindow.xaml"))throw new InvalidDataException("Incomplete or oversized application package.");
    }
    public static void AssertNoReparse(string path)
    {
        string? current=Path.GetFullPath(path);
        while(!string.IsNullOrEmpty(current))
        {
            if((File.Exists(current)||Directory.Exists(current)) &&
                (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Update path contains a reparse point: "+current);
            current=Path.GetDirectoryName(current);
        }
    }
    public static string FilePath(string directory,string relative)
    {
        string name=ValidateRelativePath(relative);
        return Path.Combine(Path.GetFullPath(directory),name.Replace('/',Path.DirectorySeparatorChar));
    }
    public static void ValidateDirectory(string directory,AppPackageManifest manifest)
    {
        ValidateManifest(manifest);AssertNoReparse(directory);
        var expected=new HashSet<string>(manifest.Files.Select(f=>ValidateRelativePath(f.Path)),StringComparer.OrdinalIgnoreCase){ManifestName};
        foreach(string entry in Directory.EnumerateFileSystemEntries(directory))CheckEntry(entry);
        void CheckEntry(string entry)
        {
            AssertNoReparse(entry);
            if(Directory.Exists(entry)){foreach(string child in Directory.EnumerateFileSystemEntries(entry))CheckEntry(child);return;}
            string name=Path.GetRelativePath(directory,entry).Replace('\\','/');
            if(!expected.Remove(name))throw new InvalidDataException("Unexpected package file: "+name);
        }
        if(expected.Count!=0)throw new InvalidDataException("Package files are missing.");
        foreach(var file in manifest.Files)
        {
            string path=FilePath(directory,file.Path);AssertNoReparse(path);
            if(new FileInfo(path).Length!=file.Size || HashFile(path)!=file.Sha256)
                throw new InvalidDataException("Package checksum mismatch: "+file.Path);
        }
    }
}
