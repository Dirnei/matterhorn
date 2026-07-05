using System.Threading.Channels;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Api;
using Matterhorn.Bridge;
using Matterhorn.Devices;

namespace Matterhorn.Test.Api;

public class SseBridgeActorTests : TestKit
{
    [Fact]
    public async Task Forwards_state_change_as_sse_json()
    {
        var ch = Channel.CreateUnbounded<string>();
        var actor = Sys.ActorOf(SseBridgeActor.Props(ch.Writer));

        actor.Tell(new DeviceStateChanged("lamp", """{"state":"ON","brightness":128}"""));

        var msg = await ch.Reader.ReadAsync();
        Assert.Equal("""{"type":"state","device":"lamp","state":{"state":"ON","brightness":128}}""", msg);
    }

    [Fact]
    public async Task Forwards_device_list_change()
    {
        var ch = Channel.CreateUnbounded<string>();
        var actor = Sys.ActorOf(SseBridgeActor.Props(ch.Writer));

        actor.Tell(new DeviceListChanged());

        Assert.Equal("""{"type":"devices"}""", await ch.Reader.ReadAsync());
    }

    [Fact]
    public async Task Forwards_group_list_change()
    {
        var ch = Channel.CreateUnbounded<string>();
        var actor = Sys.ActorOf(SseBridgeActor.Props(ch.Writer));

        actor.Tell(new Matterhorn.Groups.GroupListChanged());

        var msg = await ch.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("""{"type":"groups"}""", msg);
    }

    [Fact]
    public async Task Forwards_log_entry_as_sse_json()
    {
        var ch = Channel.CreateUnbounded<string>();
        var actor = Sys.ActorOf(SseBridgeActor.Props(ch.Writer));

        var ts = new DateTimeOffset(2026, 7, 5, 14, 3, 47, TimeSpan.Zero);
        actor.Tell(new LogEntry(ts, LogCategory.Raw, "attribute_updated", "9/1/6/0 = false", null, LogLevel.Info));

        var msg = await ch.Reader.ReadAsync();
        Assert.Equal(
            """{"type":"log","ts":"14:03:47","category":"raw","kind":"attribute_updated","msg":"9/1/6/0 = false","device":null,"level":"info"}""",
            msg);
    }
}
