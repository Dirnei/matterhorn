using System.Collections.Concurrent;

namespace Matterhorn.Matter;

/// <summary>
/// Correlates request/response commands over the single python-matter-server WebSocket: a caller
/// <see cref="Register"/>s the message id it's about to send and awaits the returned task; the
/// receive loop <see cref="Complete"/>s it when the matching reply arrives. On
/// disconnect the loop calls <see cref="FailAll"/> so in-flight callers fault instead of hanging.
/// </summary>
public sealed class PendingRequests
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<ServerResult>> _pending = new();

    public Task<ServerResult> Register(int messageId)
    {
        var tcs = new TaskCompletionSource<ServerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[messageId] = tcs;
        return tcs.Task;
    }

    public bool Complete(ServerResult result) =>
        _pending.TryRemove(result.MessageId, out var tcs) && tcs.TrySetResult(result);

    public void FailAll(Exception error)
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetException(error);
    }
}
