using Matter2Mqtt.Bridge;

namespace Matter2Mqtt.Test.Bridge;

public class FriendlyNameTests
{
    [Fact]
    public void Default_slugifies_product_and_appends_node_endpoint()
    {
        Assert.Equal("essentials_bulb_12345_1", FriendlyName.Default("Essentials Bulb", 12345, 1));
    }

    [Fact]
    public void Default_without_product_uses_node_prefix()
    {
        Assert.Equal("node_12345_2", FriendlyName.Default(null, 12345, 2));
    }
}
