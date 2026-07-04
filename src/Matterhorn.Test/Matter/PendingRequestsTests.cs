using Matterhorn.Matter;

namespace Matterhorn.Test.Matter;

public class PendingRequestsTests
{
    [Fact]
    public async Task Register_then_Complete_resolves_the_awaiting_task()
    {
        var pending = new PendingRequests();
        var task = pending.Register(7);
        Assert.False(task.IsCompleted);

        Assert.True(pending.Complete(new ServerResult(7, 42, null)));

        var result = await task;
        Assert.Equal(42ul, result.NodeId);
    }

    [Fact]
    public void Complete_for_unknown_message_id_returns_false()
    {
        var pending = new PendingRequests();
        Assert.False(pending.Complete(new ServerResult(99, null, null)));
    }

    [Fact]
    public async Task FailAll_faults_every_pending_task()
    {
        var pending = new PendingRequests();
        var a = pending.Register(1);
        var b = pending.Register(2);

        pending.FailAll(new IOException("socket closed"));

        await Assert.ThrowsAsync<IOException>(() => a);
        await Assert.ThrowsAsync<IOException>(() => b);
    }

    [Fact]
    public async Task Complete_only_resolves_the_matching_message_id()
    {
        var pending = new PendingRequests();
        var first = pending.Register(1);
        var second = pending.Register(2);

        pending.Complete(new ServerResult(2, 55, null));

        Assert.False(first.IsCompleted);
        Assert.Equal(55ul, (await second).NodeId);
    }
}
