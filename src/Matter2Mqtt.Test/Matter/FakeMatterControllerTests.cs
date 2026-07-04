using System.Text.Json;
using Matter2Mqtt.Devices;
using Matter2Mqtt.Matter;

namespace Matter2Mqtt.Test.Matter;

public class FakeMatterControllerTests
{
    [Fact]
    public async Task Emitted_events_are_observed_by_listener()
    {
        var fake = new FakeMatterController();
        var received = new List<MatterEvent>();
        using var cts = new CancellationTokenSource();

        var pump = Task.Run(async () =>
        {
            await foreach (var e in fake.ConnectAndListen(cts.Token))
            {
                received.Add(e);
                if (received.Count == 1) { cts.Cancel(); break; }
            }
        });

        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.OnOff, 0,
            JsonDocument.Parse("true").RootElement)));
        await pump;

        Assert.Single(received);
        Assert.IsType<AttributeChanged>(received[0]);
    }

    [Fact]
    public async Task InvokeCommand_is_recorded()
    {
        var fake = new FakeMatterController();
        await fake.InvokeCommand(1, 1,
            new CommandSpec(MatterClusters.OnOff, "On", new Dictionary<string, object?>()), default);
        var inv = Assert.Single(fake.Invocations);
        Assert.Equal("On", inv.Cmd.CommandName);
    }

    [Fact]
    public async Task Commission_uses_hook()
    {
        var fake = new FakeMatterController { OnCommission = _ => 42 };
        Assert.Equal(42ul, await fake.Commission("MT:XXX", default));
    }
}
