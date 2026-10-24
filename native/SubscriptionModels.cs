using System.Text.Json.Serialization;

namespace VideoDownloader.Native;

public sealed class SubscriptionDefinition
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public bool DownloadExisting { get; set; }
    public bool Initialized { get; set; }
    public bool Paused { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public string LastError { get; set; } = "";
    public string LastWarning { get; set; } = "";
}

public sealed class SubscriptionItem
{
    public string SubscriptionId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string State { get; set; } = "New";
    public DateTimeOffset DiscoveredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool InitialQueuePending { get; set; }
    // Global history proves success but does not identify its subscription.
    public bool SuccessAttributed { get; set; }
    public string DuplicateSubscriptionId { get; set; } = "";
    public bool DuplicateNotified { get; set; }
    [JsonIgnore] public string StateLabel => State switch
    {
        "Baseline" => "Исходный снимок",
        "New" => "Новое",
        "Queued" => "В очереди",
        "Processed" => "Обработано",
        "AlreadyProcessed" => "Обработано в другой подписке",
        "Skipped" => "Пропущено",
        _ => State
    };
}

public sealed class SubscriptionSnapshot
{
    public int Version { get; set; } = 1;
    public List<SubscriptionDefinition> Subscriptions { get; set; } = new();
    public List<SubscriptionItem> Items { get; set; } = new();
}

public sealed record DiscoveredMedia(string Url, string Title);
public sealed record SubscriptionScan(string Title, IReadOnlyList<DiscoveredMedia> Items, string FeedUrl, string Warning, string SuggestedName = "");
