namespace VideoDownloader.Native;

/// <summary>One due check per source, conservative restart and bounded host request frequency.</summary>
internal sealed class SubscriptionSchedulePlanner(TimeProvider? timeProvider=null)
{
    internal const int MinIntervalMinutes=5,MaxIntervalMinutes=7*24*60;
    internal static readonly TimeSpan HostSpacing=TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock=timeProvider??TimeProvider.System;
    private readonly Dictionary<string,Entry> _entries=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Host> _hosts=new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _previousTime;
    private sealed record Settings(string Url,string Feed,int Minutes);
    private sealed class Entry(Settings settings,DateTimeOffset due,DateTimeOffset? attempt)
    {
        internal Settings Settings=settings;
        internal DateTimeOffset Due=due;
        internal DateTimeOffset? Attempt=attempt;
        internal int Failures;
        internal bool Running;
    }
    private sealed class Host {internal DateTimeOffset Next;internal int Failures;}
    internal DateTimeOffset UtcNow=>_clock.GetUtcNow();
    internal void Reset(){_entries.Clear();_hosts.Clear();_previousTime=null;}
    internal void Synchronize(IEnumerable<SubscriptionDefinition> definitions)
    {
        var now=UtcNow;
        // A clock correction must not freeze a schedule until the old wall time.
        if(_previousTime is {} previous && now<previous-TimeSpan.FromMinutes(1))Reset();
        _previousTime=now;
        var active=definitions.Where(source=>source.ScheduleEnabled && source.Initialized && !source.Paused)
            .ToDictionary(source=>source.Id,StringComparer.Ordinal);
        foreach(string id in _entries.Keys.Where(id=>!active.ContainsKey(id)).ToArray())_entries.Remove(id);
        foreach(var source in active.Values)
        {
            var settings=new Settings(source.Url,source.FeedUrl,source.ScheduleIntervalMinutes);
            if(!_entries.TryGetValue(source.Id,out var entry) || entry.Settings!=settings)
                _entries[source.Id]=new(settings,now+TimeSpan.FromMinutes(settings.Minutes),source.LastAttemptUtc);
            else if(!entry.Running && entry.Attempt!=source.LastAttemptUtc)
            {
                // A manual check also resets the interval. No first-start catch-up
                // happens without a separate permission to check at startup.
                entry.Attempt=source.LastAttemptUtc;entry.Failures=0;
                entry.Due=now+TimeSpan.FromMinutes(settings.Minutes);
            }
        }
        var hosts=active.Values.Select(source=>new Uri(source.Url).IdnHost).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(string host in _hosts.Keys.Where(host=>!hosts.Contains(host)).ToArray())_hosts.Remove(host);
    }
    internal DateTimeOffset? NextCheckUtc(string id)
    {
        if(!_entries.TryGetValue(id,out var entry))return null;
        var host=HostFor(entry.Settings.Url);
        return entry.Due>host.Next?entry.Due:host.Next;
    }
    internal string? TakeDue()
    {
        var now=UtcNow;
        var due=_entries.Where(pair=>!pair.Value.Running && pair.Value.Due<=now &&
                HostFor(pair.Value.Settings.Url).Next<=now)
            .OrderBy(pair=>pair.Value.Due).ThenBy(pair=>pair.Key,StringComparer.Ordinal).FirstOrDefault();
        if(due.Key is null)return null;
        due.Value.Running=true;due.Value.Due=now+TimeSpan.FromMinutes(due.Value.Settings.Minutes);
        HostFor(due.Value.Settings.Url).Next=now+HostSpacing;
        return due.Key;
    }
    internal void Complete(string id,bool success,DateTimeOffset? attempt,bool cancelled=false)
    {
        if(!_entries.TryGetValue(id,out var entry))return;
        var now=UtcNow;entry.Running=false;entry.Attempt=attempt;
        var host=HostFor(entry.Settings.Url);
        if(success){entry.Failures=0;host.Failures=0;}
        else if(!cancelled){entry.Failures=Math.Min(8,entry.Failures+1);host.Failures=Math.Min(7,host.Failures+1);}
        int minutes=Math.Min(MaxIntervalMinutes,entry.Settings.Minutes*(1<<entry.Failures));
        entry.Due=now+TimeSpan.FromMinutes(minutes);
        var hostDelay=TimeSpan.FromMinutes(Math.Min(60,1<<Math.Max(0,host.Failures-1)));
        if(host.Next<now+hostDelay)host.Next=now+hostDelay;
    }
    private Host HostFor(string url)
    {
        string key=new Uri(url).IdnHost;
        if(!_hosts.TryGetValue(key,out var host))_hosts[key]=host=new();
        return host;
    }
}
