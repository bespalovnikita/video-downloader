using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VideoDownloader.Native.Compatibility;

/// <summary>Reads legacy worker identity and terminal events without modifying their source files.</summary>
internal static class LegacySessionReader
{
    internal static void Reconcile(SessionSnapshot session)
    {
        // A live legacy worker owns this checkpoint and may still hold its
        // append-only event stream open. Latch the observation before any read.
        session.LegacyEngineWasActive = IsEngineAlive(session.Engine);
        if (!session.LegacyEngineWasActive) MergeResultEvents(session);
    }

    internal static bool IsEngineAlive(object? engine)
    {
        if (engine is not JsonElement record || record.ValueKind!=JsonValueKind.Object ||
            !record.TryGetProperty("Pid",out var pidToken) || pidToken.ValueKind!=JsonValueKind.Number ||
            !pidToken.TryGetInt32(out int pid) ||
            pid<=0 || !record.TryGetProperty("StartedUtc",out var startedToken) ||
            startedToken.ValueKind!=JsonValueKind.String ||
            !DateTimeOffset.TryParse(startedToken.GetString(),out var recorded))
            return false;
        try
        {
            using var process=Process.GetProcessById(pid);
            return process.ProcessName.Equals("pwsh",StringComparison.OrdinalIgnoreCase) &&
                Math.Abs((process.StartTime.ToUniversalTime()-recorded.UtcDateTime).TotalSeconds)<3;
        }
        catch(ArgumentException){return false;}
        catch(InvalidOperationException){return false;}
        catch(System.ComponentModel.Win32Exception){return false;}
    }

    private static void MergeResultEvents(SessionSnapshot session)
    {
        if (session.Engine is not JsonElement engine || engine.ValueKind!=JsonValueKind.Object ||
            !engine.TryGetProperty("EventsPath",out var eventsProp) ||
            eventsProp.ValueKind!=JsonValueKind.String) return;
        var path=eventsProp.GetString();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        // An old append-only events file can contain a truncated tail line.
        // Ignore malformed event lines, but never infer success from progress.
        if (new FileInfo(path).Length>128*1024*1024) return;
        var rows=session.Queue.ToDictionary(row=>row.Url,StringComparer.Ordinal);
        foreach(var line in File.ReadLines(path,Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))continue;
            try
            {
                using var document=JsonDocument.Parse(line);
                var ev=document.RootElement;
                if (ev.ValueKind!=JsonValueKind.Object ||
                    !ev.TryGetProperty("Kind",out var kind) || kind.ValueKind!=JsonValueKind.String || kind.GetString()!="Result" ||
                    !ev.TryGetProperty("Url",out var urlValue) || urlValue.ValueKind!=JsonValueKind.String ||
                    !rows.TryGetValue(urlValue.GetString()??"",out var row))
                    continue;
                if (row.DeferredToNextStart) continue;
                // Validate terminal fields before changing the row. A partial
                // or malformed Result is not proof of success, failure or cancel.
                if (!ev.TryGetProperty("Success",out var success) ||
                    success.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                bool hasCancelled=ev.TryGetProperty("Cancelled",out var cancelled);
                if (hasCancelled && cancelled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                if (success.ValueKind==JsonValueKind.True && hasCancelled && cancelled.ValueKind==JsonValueKind.True) continue;
                if (ev.TryGetProperty("Path",out var media) && media.ValueKind!=JsonValueKind.String) continue;
                bool hasOwned=ev.TryGetProperty("OwnedPaths",out var owned);
                if (hasOwned && (owned.ValueKind!=JsonValueKind.Array ||
                    owned.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.String))) continue;
                if (ev.TryGetProperty("CleanupPaths",out var cleanup) &&
                    (cleanup.ValueKind!=JsonValueKind.Array || cleanup.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.String))) continue;
                if (new[]{"OutputRoot","StageDir","CleanupError"}.Any(name=>
                    ev.TryGetProperty(name,out var value) && value.ValueKind!=JsonValueKind.String)) continue;
                row.AttemptFinished=true;
                if (ev.TryGetProperty("OutputRoot",out var output)) row.OutputRoot=output.GetString()??"";
                if (engine.TryGetProperty("RunDir",out var run) && run.ValueKind==JsonValueKind.String)
                    row.RunDir=run.GetString()??"";
                if (ev.TryGetProperty("StageDir",out var stage)) row.StageDir=stage.GetString()??"";
                if (cleanup.ValueKind==JsonValueKind.Array)
                    row.CleanupPaths=cleanup.EnumerateArray().Select(v=>v.GetString()!).ToList();
                if (ev.TryGetProperty("CleanupError",out var error)) row.CleanupError=error.GetString()??"";
                if (row.CleanupError.Length>0)
                {
                    row.PendingRemoval=true;row.Status="Interrupted";row.State="Cleanup failed — Remove to retry";
                    if (hasOwned) row.OwnedPaths=owned.EnumerateArray().Select(v=>v.GetString()!).ToList();
                    continue;
                }
                if (success.ValueKind==JsonValueKind.True)
                {
                    row.Status="Completed";row.State="Done";row.Progress=100;
                    if (media.ValueKind==JsonValueKind.String)
                        row.Path=media.GetString()??"";
                    if (hasOwned)
                        row.OwnedPaths=owned.EnumerateArray()
                            .Where(v=>v.ValueKind==JsonValueKind.String)
                            .Select(v=>v.GetString()??"")
                            .Where(v=>!string.IsNullOrWhiteSpace(v)).ToList();
                }
                else if (hasCancelled && cancelled.ValueKind==JsonValueKind.True)
                {
                    if (row.Status!="Completed") {row.Status="Interrupted";row.State="Cancelled";}
                }
                else if (row.Status!="Completed")
                {
                    row.Status="Failed";row.State="Failed";
                }
            }
            catch(JsonException){} // a partial JSONL tail after a crash
            catch(InvalidOperationException){} // malformed event property types
        }
    }

}
