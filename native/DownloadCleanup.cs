namespace VideoDownloader.Native;

public static class DownloadCleanup
{
    public static void Failed(DownloadItem row,string error)
    {
        row.PendingRemoval=true;row.CleanupError=error;
        row.Status="Interrupted";row.State="Cleanup failed — Remove to retry";
        row.RawError=error;
    }

    // Keep the complete manifest until every operation succeeds. Missing files
    // are harmless on retry; an unowned legacy Path never authorizes deletion.
    public static void Retry(DownloadItem row)
    {
        row.CleanupPaths=row.OwnedPaths.Concat(row.CleanupPaths)
            .Where(p=>!string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();
        try
        {
            if (row.Path.Length>0 && row.CleanupPaths.Count==0 && File.Exists(row.Path))
                throw new IOException("This download has no ownership manifest. Remove its file manually; the application cannot safely delete it.");
            if (row.CleanupPaths.Count>0)
            {
                if (string.IsNullOrWhiteSpace(row.OutputRoot)) throw new IOException("Original output folder is unavailable.");
                OwnedFiles.DeleteOwned(row.OutputRoot,row.CleanupPaths);
            }
            if (row.StageDir.Length>0)
            {
                if (row.RunDir.Length==0) throw new IOException("Original staging root is unavailable.");
                OwnedFiles.DeleteStage(row.StageDir,row.RunDir);
            }
            row.OwnedPaths=[];row.CleanupPaths=[];row.StageDir="";
            row.Path="";row.CleanupError="";
        }
        catch(Exception error) { Failed(row,error.Message);throw; }
    }
}
