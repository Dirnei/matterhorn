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

// Contract-first controllers (generated from contracts/matter2mqtt.openapi.yaml). The generated
// DTOs carry their snake_case wire names via [JsonPropertyName]; we only omit null fields so the
// exposes shape matches the MQTT projection (spec §7).
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

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

// Serve the authored contract and Swagger UI (both public — the API key only guards /api).
var contractPath = Path.Combine(app.Environment.ContentRootPath, "openapi", "matter2mqtt.yaml");
app.MapGet("/openapi/matter2mqtt.yaml", () => Results.File(contractPath, "application/yaml"));
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/openapi/matter2mqtt.yaml", "Matter2Mqtt API v1");
    c.RoutePrefix = "swagger";
    c.DocumentTitle = "Matter2Mqtt API";
});

app.MapControllers();
app.Run();

public partial class Program { }
