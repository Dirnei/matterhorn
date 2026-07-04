using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;
using Akka.Hosting;
using HiveMQtt.Client;
using Matter2Mqtt.Api;
using Matter2Mqtt.Bridge;
using Matter2Mqtt.Configuration;
using Matter2Mqtt.Dev;
using Matter2Mqtt.Matter;
using Matter2Mqtt.Mqtt;

var builder = WebApplication.CreateBuilder(args);
var cfg = Matter2MqttConfig.FromConfiguration(builder.Configuration);
var topics = new MqttTopics(cfg.BaseTopic);

// MQTT client. Connecting + (re)subscribing is owned by MqttBridgeService so a temporarily
// unreachable broker never blocks host startup; HiveMqttPublisher tolerates disconnected publishes.
var mqttClient = new HiveMQClient(new HiveMQClientOptionsBuilder()
    .WithBroker(cfg.MqttHost).WithPort(cfg.MqttPort)
    .WithClientId($"matter2mqtt-{Guid.NewGuid():N}").Build());

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

// REST shares the MQTT JSON shape (snake_case, null fields omitted) so the two surfaces match.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddAkka("matter2mqtt", (b, sp) => b
    .WithActors((system, registry) =>
    {
        var publisher = sp.GetRequiredService<IMqttPublisher>();
        var gw = system.ActorOf(MatterGatewayActor.Props(controller, publisher, topics), "gateway");
        registry.Register<MatterGatewayActor>(gw);
    }));
builder.Services.AddSingleton(sp =>
    new GatewayRef(sp.GetRequiredService<ActorRegistry>().Get<MatterGatewayActor>()));

// Owns broker connect/subscribe and routes inbound MQTT commands to the gateway.
builder.Services.AddHostedService<MqttBridgeService>();

// Dev-only: seed demo devices via the fake controller so there is something to test.
if (bool.TryParse(builder.Configuration["DevSeed"], out var devSeed) && devSeed)
    builder.Services.AddHostedService<DemoDeviceSeeder>();

var app = builder.Build();
app.UseMiddleware<ApiKeyMiddleware>();
ApiEndpoints.Map(app);
app.Run();

public partial class Program { }
