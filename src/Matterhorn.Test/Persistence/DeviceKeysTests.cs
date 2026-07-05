using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

public class DeviceKeysTests
{
    [Fact]
    public void Format_joins_node_and_endpoint_with_underscore()
        => Assert.Equal("5_1", DeviceKeys.Format((5UL, 1)));

    [Fact]
    public void TryParse_round_trips_a_formatted_key()
    {
        Assert.True(DeviceKeys.TryParse("5_1", out var key));
        Assert.Equal((5UL, (ushort)1), key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("a_b")]
    [InlineData("5_1_2")]
    public void TryParse_rejects_malformed(string s) => Assert.False(DeviceKeys.TryParse(s, out _));
}
