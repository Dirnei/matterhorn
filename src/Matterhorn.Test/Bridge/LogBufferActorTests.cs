using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;

namespace Matterhorn.Test.Bridge;

public class LogBufferActorTests : TestKit
{
    private static LogEntry Entry(LogCategory cat, int n) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(n), cat, "k", $"msg{n}", null, LogLevel.Info);

    [Fact]
    public void Snapshot_returns_activity_and_raw_in_order()
    {
        var buf = Sys.ActorOf(LogBufferActor.Props());
        buf.Ask<LogSnapshot>(new GetLogSnapshot()).Wait();   // ensure subscribed before publishing
        Sys.EventStream.Publish(Entry(LogCategory.Activity, 1));
        Sys.EventStream.Publish(Entry(LogCategory.Raw, 2));
        Sys.EventStream.Publish(Entry(LogCategory.Activity, 3));

        AwaitAssert(() =>
        {
            var snap = buf.Ask<LogSnapshot>(new GetLogSnapshot()).Result;
            Assert.Equal(new[] { "msg1", "msg3" }, snap.Activity.Select(e => e.Message));
            Assert.Equal(new[] { "msg2" }, snap.Raw.Select(e => e.Message));
        });
    }

    [Fact]
    public void Raw_buffer_evicts_oldest_beyond_cap()
    {
        var buf = Sys.ActorOf(LogBufferActor.Props());
        buf.Ask<LogSnapshot>(new GetLogSnapshot()).Wait();   // ensure subscribed before publishing
        for (var i = 0; i < 205; i++) Sys.EventStream.Publish(Entry(LogCategory.Raw, i));

        AwaitAssert(() =>
        {
            var snap = buf.Ask<LogSnapshot>(new GetLogSnapshot()).Result;
            Assert.Equal(200, snap.Raw.Count);
            Assert.Equal("msg5", snap.Raw[0].Message);   // 0..4 evicted
            Assert.Equal("msg204", snap.Raw[^1].Message);
        });
    }

    [Fact]
    public void Activity_buffer_evicts_oldest_beyond_cap()
    {
        var buf = Sys.ActorOf(LogBufferActor.Props());
        buf.Ask<LogSnapshot>(new GetLogSnapshot()).Wait();   // ensure subscribed before publishing
        for (var i = 0; i < 105; i++) Sys.EventStream.Publish(Entry(LogCategory.Activity, i));

        AwaitAssert(() =>
        {
            var snap = buf.Ask<LogSnapshot>(new GetLogSnapshot()).Result;
            Assert.Equal(100, snap.Activity.Count);
            Assert.Equal("msg5", snap.Activity[0].Message);
            Assert.Equal("msg104", snap.Activity[^1].Message);
        });
    }
}
