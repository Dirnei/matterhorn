using Akka.Actor;
using Akka.Hosting;
using HiveMQtt.Client;
using Matter2Mqtt.Api;
using Matter2Mqtt.Bridge;
using Matter2Mqtt.Configuration;
using Matter2Mqtt.Matter;
using Matter2Mqtt.Mqtt;

var builder = WebApplication.CreateBuilder(args);
var cfg = Matter2MqttConfig.FromConfiguration(builder.Configuration);
var topics = new MqttTopics(cfg.BaseTopic);

// MQTT client. Connect in the background so a temporarily-unreachable broker never blocks
// or fails host startup; HiveMqttPublisher tolerates publishing while disconnected.
var mqttClient = new HiveMQClient(new HiveMQClientOptionsBuilder()
    .WithBroker(cfg.MqttHost).WithPort(cfg.MqttPort)
    .WithClientId($"matter2mqtt-{Guid.NewGuid():N}").Build());
_ = Task.Run(async () =>
{
    try { await mqttClient.ConnectAsync(); }
    catch (Exception ex) { Console.Error.WriteLine($"MQTT connect failed at startup: {ex.Message}"); }
});

// Controller seam (swappable). "fake" is the in-memory dev/test controller (spec §11).
IMatterController controller = cfg.ControllerKind switch
{
    "fake" => new FakeMatterController(),
    _ => new PythonMatterServerController(cfg.ControllerWsUrl),
};

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(topics);
builder.Services.AddSingleton(mqttClient);
builder.Services.AddSingleton(controller);
builder.Services.AddSingleton<IMqttPublisher, HiveMqttPublisher>();
builder.Services.AddSingleton<IConfigureApiKey>(new StaticApiKey(cfg.ApiKey));

builder.Services.AddAkka("matter2mqtt", (b, sp) => b
    .WithActors((system, registry) =>
    {
        var publisher = sp.GetRequiredService<IMqttPublisher>();
        var gw = system.ActorOf(MatterGatewayActor.Props(controller, publisher, topics), "gateway");
        registry.Register<MatterGatewayActor>(gw);
    }));
builder.Services.AddSingleton(sp =>
    new GatewayRef(sp.GetRequiredService<ActorRegistry>().Get<MatterGatewayActor>()));

var app = builder.Build();
app.UseMiddleware<ApiKeyMiddleware>();
ApiEndpoints.Map(app);
app.Run();

public partial class Program { }
