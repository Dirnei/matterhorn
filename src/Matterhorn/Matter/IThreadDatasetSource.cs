namespace Matterhorn.Matter;

/// <summary>Where the Thread credentials come from, and whether we have any.</summary>
/// <param name="Dataset">Hex TLV to hand the controller, or null when Thread onboarding is unavailable.</param>
/// <param name="Source">"otbr", "configured", or "none" — for the dashboard.</param>
/// <param name="BorderRouter">The border router the dataset came from, when it was fetched.</param>
/// <param name="Reason">Why there is no dataset, when there isn't one.</param>
public sealed record ThreadCredentials(string? Dataset, string Source, string? BorderRouter = null, string? Reason = null)
{
    public static readonly ThreadCredentials None = new(null, "none", Reason: "no Thread border router configured");
    public bool Available => Dataset is not null;
}

/// <summary>
/// Supplies the Thread operational dataset — the credentials a joining device needs, the Thread
/// equivalent of a Wi-Fi password. Resolving must never throw: a border router that is unreachable
/// disables Thread onboarding, it does not break the bridge.
/// </summary>
public interface IThreadDatasetSource
{
    Task<ThreadCredentials> Resolve(CancellationToken ct);
}

/// <summary>Fixed credentials, for tests and dev.</summary>
public sealed class FixedThreadDatasetSource(ThreadCredentials credentials) : IThreadDatasetSource
{
    public FixedThreadDatasetSource(string? dataset)
        : this(dataset is null ? ThreadCredentials.None : new ThreadCredentials(dataset, "configured")) { }

    public Task<ThreadCredentials> Resolve(CancellationToken ct) => Task.FromResult(credentials);
}
