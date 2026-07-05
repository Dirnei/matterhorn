using Akka.TestKit;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Mqtt;

namespace Matterhorn.Test.Mqtt;

public class MqttCommandRouterTests : TestKit
{
    private readonly MqttTopics _topics = new("matterhorn");

    private CommandTargets Targets(out TestProbe gw, out TestProbe groups, out TestProbe scenes)
    {
        gw = CreateTestProbe(); groups = CreateTestProbe(); scenes = CreateTestProbe();
        return new CommandTargets(gw.Ref, groups.Ref, scenes.Ref);
    }

    [Fact]
    public void Set_object_forwards_SetDevice_with_payload()
    {
        var targets = Targets(out var gw, out _, out _);
        MqttCommandRouter.Route(_topics, "matterhorn/lamp/set", """{"state":"ON","brightness":128}""", targets);

        var msg = gw.ExpectMsg<SetDevice>();
        Assert.Equal("lamp", msg.FriendlyName);
        Assert.Equal("ON", msg.Payload["state"].GetString());
        Assert.Equal(128, msg.Payload["brightness"].GetInt32());
    }

    [Fact]
    public void Set_single_attr_wraps_bare_value()
    {
        var targets = Targets(out var gw, out _, out _);
        MqttCommandRouter.Route(_topics, "matterhorn/lamp/set/state", "ON", targets);

        var msg = gw.ExpectMsg<SetDevice>();
        Assert.Equal("lamp", msg.FriendlyName);
        Assert.Equal("ON", msg.Payload["state"].GetString());
    }

    [Fact]
    public void Commission_request_forwards_CommissionRequest()
    {
        var targets = Targets(out var gw, out _, out _);
        MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/commission",
            """{"code":"MT:XYZ","transaction":"tx9"}""", targets);

        var msg = gw.ExpectMsg<CommissionRequest>();
        Assert.Equal("MT:XYZ", msg.Code);
        Assert.Equal("tx9", msg.Transaction);
    }

    [Fact]
    public void Remove_request_forwards_RemoveRequest()
    {
        var targets = Targets(out var gw, out _, out _);
        MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/remove",
            """{"id":"lamp","transaction":"tx1"}""", targets);

        var msg = gw.ExpectMsg<RemoveRequest>();
        Assert.Equal("lamp", msg.FriendlyName);
    }

    [Fact]
    public void Rename_request_forwards_RenameRequest()
    {
        var targets = Targets(out var gw, out _, out _);
        MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/rename",
            """{"from":"bulb_5_1","to":"lamp","transaction":"tx7"}""", targets);

        var msg = gw.ExpectMsg<RenameRequest>();
        Assert.Equal("bulb_5_1", msg.FromName);
        Assert.Equal("lamp", msg.ToName);
        Assert.Equal("tx7", msg.Transaction);
    }

    [Fact]
    public void Unrelated_topic_forwards_nothing()
    {
        var targets = Targets(out var gw, out _, out _);
        MqttCommandRouter.Route(_topics, "matterhorn/lamp", "whatever", targets);
        gw.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Group_add_request_forwards_CreateGroup()
    {
        var targets = Targets(out _, out var groups, out _);
        MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/group/add",
            """{"friendly_name":"living_room","transaction":"t1"}""", targets);

        var msg = groups.ExpectMsg<Matterhorn.Groups.CreateGroup>();
        Assert.Equal("living_room", msg.Name);
        Assert.Equal("t1", msg.Transaction);
    }

    [Fact]
    public void Group_members_add_request_forwards_AddGroupMember()
    {
        var targets = Targets(out _, out var groups, out _);
        MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/group/members/add",
            """{"group":"living_room","device":"lamp","transaction":"t2"}""", targets);

        var msg = groups.ExpectMsg<Matterhorn.Groups.AddGroupMember>();
        Assert.Equal("living_room", msg.Group);
        Assert.Equal("lamp", msg.Device);
    }
}
