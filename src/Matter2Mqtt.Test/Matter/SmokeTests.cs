using Matter2Mqtt.Matter;

namespace Matter2Mqtt.Test.Matter;

public class SmokeTests
{
    [Fact]
    public void Cluster_ids_are_defined()
    {
        Assert.Equal(0x0006u, MatterClusters.OnOff);
        Assert.Equal(0x0300u, MatterClusters.ColorControl);
    }
}
