using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Mqtt;

namespace Matterhorn.Test.Mqtt;

public class MqttCommandRouterTests : TestKit
{
    private readonly MqttTopics _topics = new("matterhorn");

    [Fact]
    public void Set_object_forwards_SetDevice_with_payload()
    {
        var gw = CreateTestProbe();
        MqttCommandRouter.Route(_topics, "matterhorn/lamp/set", """{"state":"ON","brightness":128}""", gw.Ref);

        var msg = gw.ExpectMsg<SetDevice>();
        Assert.Equal("lamp", msg.FriendlyName);
        Assert.Equal("ON", msg.Payload["state"].GetString());
        Assert.Equal(128, msg.Payload["brightness"].GetInt32());
    }

    [Fact]
    public void Set_single_attr_wraps_bare_value()
    {
        var gw = CreateTestProbe();
        MqttCommandRouter.Route(_topics, "matterhorn/lamp/set/state", "ON", gw.Ref);

        var msg = gw.ExpectMsg<SetDevice>();
        Assert.Equal("lamp", msg.FriendlyName);
        Assert.Equal("ON", msg.Payload["state"].GetString());
    }

    [Fact]
    public void Commission_request_forwards_CommissionRequest()
    {
        var gw = CreateTestProbe();
        MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/commission",
            """{"code":"MT:XYZ","transaction":"tx9"}""", gw.Ref);

        var msg = gw.ExpectMsg<CommissionRequest>();
        Assert.Equal("MT:XYZ", msg.Code);
        Assert.Equal("tx9", msg.Transaction);
    }

    [Fact]
    public void Remove_request_forwards_RemoveRequest()
    {
        var gw = CreateTestProbe();
        MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/remove",
            """{"id":"lamp","transaction":"tx1"}""", gw.Ref);

        var msg = gw.ExpectMsg<RemoveRequest>();
        Assert.Equal("lamp", msg.FriendlyName);
    }

    [Fact]
    public void Rename_request_forwards_RenameRequest()
    {
        var gw = CreateTestProbe();
        MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/rename",
            """{"from":"bulb_5_1","to":"lamp","transaction":"tx7"}""", gw.Ref);

        var msg = gw.ExpectMsg<RenameRequest>();
        Assert.Equal("bulb_5_1", msg.FromName);
        Assert.Equal("lamp", msg.ToName);
        Assert.Equal("tx7", msg.Transaction);
    }

    [Fact]
    public void Unrelated_topic_forwards_nothing()
    {
        var gw = CreateTestProbe();
        MqttCommandRouter.Route(_topics, "matterhorn/lamp", "whatever", gw.Ref);
        gw.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }
}
