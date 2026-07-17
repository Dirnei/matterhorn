using Matterhorn.Configuration;

namespace Matterhorn.Matter;

/// <summary>
/// Resolves the Thread dataset, preferring an explicitly configured one and otherwise reading it off
/// an OpenThread Border Router's REST API (<c>GET /node/dataset/active</c>, which returns the hex TLV
/// as text/plain). Fetching it means the operator configures their border router's address — which
/// they know — instead of pasting credentials, and a re-formed Thread network is picked up on its own.
/// </summary>
public sealed class ThreadDatasetSource(MatterhornConfig config, HttpClient http) : IThreadDatasetSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ThreadCredentials? _cached;

    public async Task<ThreadCredentials> Resolve(CancellationToken ct)
    {
        if (_cached is { Available: true } hit) return hit;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { Available: true } raced) return raced;
            return _cached = await Fetch(ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<ThreadCredentials> Fetch(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.ThreadDataset))
            return new ThreadCredentials(config.ThreadDataset.Trim(), "configured");

        if (string.IsNullOrWhiteSpace(config.ThreadOtbrUrl)) return ThreadCredentials.None;

        var url = $"{config.ThreadOtbrUrl.TrimEnd('/')}/node/dataset/active";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "text/plain");
            using var resp = await http.SendAsync(req, ct);

            // A border router that has not formed a network yet answers 204/404 rather than a dataset.
            if (!resp.IsSuccessStatusCode)
                return Unavailable($"border router at {config.ThreadOtbrUrl} answered {(int)resp.StatusCode} — has it formed a Thread network?");

            var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            return IsHex(body)
                ? new ThreadCredentials(body, "otbr", config.ThreadOtbrUrl)
                : Unavailable($"border router at {config.ThreadOtbrUrl} returned no usable dataset");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Unavailable($"border router at {config.ThreadOtbrUrl} is unreachable: {e.Message}");
        }
    }

    private ThreadCredentials Unavailable(string reason)
    {
        Console.Error.WriteLine($"[thread] {reason}");
        return new ThreadCredentials(null, "none", config.ThreadOtbrUrl, reason);
    }

    private static bool IsHex(string s) =>
        s.Length >= 2 && s.Length % 2 == 0 && s.All(Uri.IsHexDigit);
}
