using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;

namespace Matterhorn.Test.Bridge;

public class LogBufferActorTests : TestKit
{
    private static LogEntry Entry(LogCategory cat, int n) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(n), cat, "k", $"msg{n}", null, LogLevel.Info);

    [Fact]
    public void Snapshot_merges_activity_and_raw_into_one_chronological_stream()
    {
        var buf = Sys.ActorOf(LogBufferActor.Props());
        buf.Ask<LogSnapshot>(new GetLogSnapshot()).Wait();   // ensure subscribed before publishing
        Sys.EventStream.Publish(Entry(LogCategory.Activity, 1));
        Sys.EventStream.Publish(Entry(LogCategory.Raw, 2));
        Sys.EventStream.Publish(Entry(LogCategory.Activity, 3));

        AwaitAssert(() =>
        {
            var snap = buf.Ask<LogSnapshot>(new GetLogSnapshot()).Result;
            // One stream, ordered by timestamp, containing BOTH categories — this is the Raw view.
            Assert.Equal(new[] { "msg1", "msg2", "msg3" }, snap.Entries.Select(e => e.Message));
            Assert.Equal(
                new[] { LogCategory.Activity, LogCategory.Raw, LogCategory.Activity },
                snap.Entries.Select(e => e.Category));
        });
    }

    [Fact]
    public void Raw_events_evict_oldest_beyond_cap()
    {
        var buf = Sys.ActorOf(LogBufferActor.Props());
        buf.Ask<LogSnapshot>(new GetLogSnapshot()).Wait();   // ensure subscribed before publishing
        for (var i = 0; i < 205; i++) Sys.EventStream.Publish(Entry(LogCategory.Raw, i));

        AwaitAssert(() =>
        {
            var raw = buf.Ask<LogSnapshot>(new GetLogSnapshot()).Result
                .Entries.Where(e => e.Category == LogCategory.Raw).ToList();
            Assert.Equal(200, raw.Count);
            Assert.Equal("msg5", raw[0].Message);   // 0..4 evicted
            Assert.Equal("msg204", raw[^1].Message);
        });
    }

    [Fact]
    public void Activity_events_evict_oldest_beyond_cap()
    {
        var buf = Sys.ActorOf(LogBufferActor.Props());
        buf.Ask<LogSnapshot>(new GetLogSnapshot()).Wait();   // ensure subscribed before publishing
        for (var i = 0; i < 105; i++) Sys.EventStream.Publish(Entry(LogCategory.Activity, i));

        AwaitAssert(() =>
        {
            var activity = buf.Ask<LogSnapshot>(new GetLogSnapshot()).Result
                .Entries.Where(e => e.Category == LogCategory.Activity).ToList();
            Assert.Equal(100, activity.Count);
            Assert.Equal("msg5", activity[0].Message);
            Assert.Equal("msg104", activity[^1].Message);
        });
    }
}
