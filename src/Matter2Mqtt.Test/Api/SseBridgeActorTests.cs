using System.Threading.Channels;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matter2Mqtt.Api;
using Matter2Mqtt.Bridge;
using Matter2Mqtt.Devices;

namespace Matter2Mqtt.Test.Api;

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
}
