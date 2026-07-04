using System.Text.Json;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Matterhorn.Matter;

namespace Matterhorn.Test.Bridge;

public class IngestionPipelineTests : TestKit
{
    [Fact]
    public async Task Forwards_attribute_events_to_gateway()
    {
        var gateway = CreateTestProbe();
        var (queue, _) = IngestionPipeline.Run(Sys, gateway.Ref);

        await queue.OfferAsync(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.OnOff, 0,
            JsonDocument.Parse("true").RootElement)));

        var msg = gateway.ExpectMsg<AttributeChanged>(TimeSpan.FromSeconds(3));
        Assert.Equal(MatterClusters.OnOff, msg.Reading.ClusterId);
    }

    [Fact]
    public async Task Forwards_lifecycle_events_to_gateway()
    {
        var gateway = CreateTestProbe();
        var (queue, _) = IngestionPipeline.Run(Sys, gateway.Ref);

        var info = new EndpointInfo(5, 1, "V", "P", 1, 1, "OnOffLight", true, new uint[] { MatterClusters.OnOff });
        await queue.OfferAsync(new NodeAdded(info));

        gateway.ExpectMsg<NodeAdded>(TimeSpan.FromSeconds(3));
    }
}
