namespace VideoDownloader.Native;

/// <summary>Fail-closed managed filesystem operations for application-created files.
/// Static links and traversal are rejected. A hostile concurrent rename/junction swap
/// would require handle-relative Win32 APIs to eliminate the remaining TOCTOU window.</summary>
public static class OwnedFiles
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ?
        StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool Inside(string root, string candidate)
    {
        var r=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar;
        var p=Path.GetFullPath(candidate);
        return p.StartsWith(r,Comparison);
    }

    public static void NoReparseAncestors(string path)
    {
        string? dir=Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(dir) || File.Exists(dir))
            {
                var attrs=File.GetAttributes(dir);
                if ((attrs & FileAttributes.ReparsePoint)!=0) throw new IOException($"Unsafe junction or symlink ancestor: {dir}");
                if ((attrs & FileAttributes.Directory)==0) throw new IOException($"Not a folder: {dir}");
            }
            string? parent=Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
            if (string.IsNullOrEmpty(parent) || string.Equals(parent,dir,Comparison)) break;
            dir=parent;
        }
    }

    public static void RegularOrAbsent(string path)
    {
        NoReparseAncestors(path);
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var attr=File.GetAttributes(path);
        if ((attr & FileAttributes.ReparsePoint)!=0 || (attr & FileAttributes.Directory)!=0)
            throw new IOException($"Refusing reparse point or directory as an output file: {path}");
    }

    public static IReadOnlyList<string> DeleteOwned(string root,IEnumerable<string> paths)
    {
        // First validate every path: an external path must reject the whole batch
        // instead of partially deleting earlier files.
        var checkedPaths=paths.Where(p=>!string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var p in checkedPaths)
        {
            if (!Inside(root,p)) throw new IOException($"Refusing deletion outside download folder: {p}");
            RegularOrAbsent(p);
        }
        var removed=new List<string>();
        foreach (var p in checkedPaths)
        {
            RegularOrAbsent(p);
            if (File.Exists(p)) { File.Delete(p); removed.Add(p); }
        }
        return removed;
    }

    public static (string Path,List<string> OwnedPaths) Publish(string stage,string root,string? mediaPath)
    {
        if (!Directory.Exists(stage)) throw new DirectoryNotFoundException($"Stage absent: {stage}");
        NoReparseAncestors(Path.Combine(stage,"check"));
        NoReparseAncestors(Path.Combine(root,"check"));
        Directory.CreateDirectory(root);
        // Reject symbolic links/junction directories even if enumerating files skips them.
        foreach(var dir in Directory.EnumerateDirectories(stage,"*",SearchOption.AllDirectories))
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint)!=0)
                throw new IOException($"Staging contains a symlink directory: {dir}");
        var plan=new List<(string src,string dst)>();
        foreach(var file in Directory.EnumerateFiles(stage,"*",SearchOption.AllDirectories))
        {
            RegularOrAbsent(file);
            if (!Inside(stage,file)) throw new IOException("Stage path escaped its folder");
            var rel=Path.GetRelativePath(stage,file);
            var dst=Path.GetFullPath(Path.Combine(root,rel));
            if (!Inside(root,dst)) throw new IOException("Destination escaped its folder");
            RegularOrAbsent(dst);
            if (File.Exists(dst)) throw new IOException($"Refusing to overwrite an existing file: {dst}");
            plan.Add((file,dst));
        }
        var owned=new List<string>();
        string result="";
        try
        {
            foreach(var (src,dst) in plan)
            {
                RegularOrAbsent(src); RegularOrAbsent(dst);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                RegularOrAbsent(dst);
                File.Move(src,dst);  // does not overwrite pre-existing files
                owned.Add(dst);
                if (mediaPath!=null && string.Equals(Path.GetFullPath(mediaPath),Path.GetFullPath(src),Comparison))
                    result=dst;
            }
            if (mediaPath!=null && result.Length==0) throw new IOException("Expected downloaded media path not found in stage");
            return (result,owned);
        }
        catch
        {
            DeleteOwned(root,owned);
            throw;
        }
    }

    public static void ValidateTemplate(string template)
    {
        // Filename templates are relative yt-dlp patterns, never filesystem paths.
        if (string.IsNullOrWhiteSpace(template) || Path.IsPathRooted(template) || template.Contains("..",StringComparison.Ordinal)
            || template.Contains(':') || template.StartsWith('\\') || template.StartsWith('/'))
            throw new ArgumentException("Filename template must be relative and may not contain '..', drive letters or rooted paths.");
    }
}
