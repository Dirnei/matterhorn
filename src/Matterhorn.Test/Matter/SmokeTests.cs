using Matterhorn.Configuration;
using Matterhorn.Matter;
using Microsoft.Extensions.Configuration;

namespace Matterhorn.Test.Matter;

public class SmokeTests
{
    [Fact]
    public void Cluster_ids_are_defined()
    {
        Assert.Equal(0x0006u, MatterClusters.OnOff);
        Assert.Equal(0x0300u, MatterClusters.ColorControl);
    }

    [Fact]
    public void NamesFile_defaults_and_reads_from_config()
    {
        var defaults = MatterhornConfig.FromConfiguration(
            new ConfigurationBuilder().Build());
        Assert.Equal("data/names.json", defaults.NamesFile);

        var custom = MatterhornConfig.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:NamesFile"] = "/data/n.json" }).Build());
        Assert.Equal("/data/n.json", custom.NamesFile);
    }
}
