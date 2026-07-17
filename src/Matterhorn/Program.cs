using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;
using Akka.Hosting;
using HiveMQtt.Client;
using Matterhorn.Api;
using Matterhorn.Bridge;
using Matterhorn.Configuration;
using Matterhorn.Dev;
using Matterhorn.Groups;
using Matterhorn.Matter;
using Matterhorn.Mqtt;
using Matterhorn.Persistence;
using Matterhorn.Scenes;

var builder = WebApplication.CreateBuilder(args);
var cfg = MatterhornConfig.FromConfiguration(builder.Configuration);
var topics = new MqttTopics(cfg.BaseTopic);

// MQTT client. Connecting + (re)subscribing is owned by MqttBridgeService so a temporarily
// unreachable broker never blocks host startup; HiveMqttPublisher tolerates disconnected publishes.
var mqttClient = new HiveMQClient(new HiveMQClientOptionsBuilder()
    .WithBroker(cfg.MqttHost).WithPort(cfg.MqttPort)
    .WithClientId($"matterhorn-{Guid.NewGuid():N}").Build());

// Controller seam (swappable). "fake" is the in-memory dev/test controller.
IMatterController controller = cfg.ControllerKind switch
{
    "fake" => new FakeMatterController(),
    _ => new MatterServerController(cfg.ControllerWsUrl),
};

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(topics);
builder.Services.AddSingleton(mqttClient);
builder.Services.AddSingleton(controller);
// Thread credentials for commissioning a device that isn't on a network yet. Read from the border
// router's REST API when Thread:OtbrUrl is set, so nobody has to paste a dataset by hand.
builder.Services.AddSingleton<IThreadDatasetSource>(new ThreadDatasetSource(cfg,
    new HttpClient { Timeout = TimeSpan.FromSeconds(5) }));
builder.Services.AddSingleton<INameStore>(new JsonNameStore(cfg.NamesFile));
builder.Services.AddSingleton<IGroupStore>(new JsonGroupStore(cfg.GroupsFile));
builder.Services.AddSingleton<ISceneStore>(new JsonSceneStore(cfg.ScenesFile));
builder.Services.AddSingleton<IMqttPublisher, HiveMqttPublisher>();
builder.Services.AddSingleton<IConfigureApiKey>(new StaticApiKey(cfg.ApiKey));

// Contract-first controllers (generated from contracts/matterhorn.openapi.yaml). The generated
// DTOs carry their snake_case wire names via [JsonPropertyName]; we only omit null fields so the
// exposes shape matches the MQTT projection.
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

builder.Services.AddAkka("matterhorn", (b, sp) => b
    .WithActors((system, registry) =>
    {
        var publisher = sp.GetRequiredService<IMqttPublisher>();
        var names = sp.GetRequiredService<INameStore>();
        var gw = system.ActorOf(MatterGatewayActor.Props(controller, publisher, topics, names,
            sp.GetRequiredService<IThreadDatasetSource>()), "gateway");
        registry.Register<MatterGatewayActor>(gw);
        var logBuffer = system.ActorOf(LogBufferActor.Props(), "logbuffer");
        registry.Register<LogBufferActor>(logBuffer);
        var groups = system.ActorOf(GroupsSupervisor.Props(
            sp.GetRequiredService<IGroupStore>(), gw, publisher, topics), "groups");
        registry.Register<GroupsSupervisor>(groups);
        gw.Tell(new RegisterGroups(groups));
        var scenes = system.ActorOf(ScenesSupervisor.Props(
            sp.GetRequiredService<ISceneStore>(), gw, publisher, topics), "scenes");
        registry.Register<ScenesSupervisor>(scenes);
        gw.Tell(new RegisterScenes(scenes));
    }));
builder.Services.AddSingleton(sp =>
    new GatewayRef(sp.GetRequiredService<ActorRegistry>().Get<MatterGatewayActor>()));
builder.Services.AddSingleton(sp =>
    new GroupsRef(sp.GetRequiredService<ActorRegistry>().Get<GroupsSupervisor>()));
builder.Services.AddSingleton(sp =>
    new ScenesRef(sp.GetRequiredService<ActorRegistry>().Get<ScenesSupervisor>()));

// Owns broker connect/subscribe and routes inbound MQTT commands to the gateway.
builder.Services.AddHostedService<MqttBridgeService>();

// Dev-only: seed demo devices via the fake controller so there is something to test.
if (bool.TryParse(builder.Configuration["DevSeed"], out var devSeed) && devSeed)
    builder.Services.AddHostedService<DemoDeviceSeeder>();

var app = builder.Build();
app.UseMiddleware<ApiKeyMiddleware>();

// Debug UI (wwwroot/index.html) served at /.
app.UseDefaultFiles();
app.UseStaticFiles();

// Serve the authored contract and Swagger UI (both public — the API key only guards /api).
var contractPath = Path.Combine(app.Environment.ContentRootPath, "openapi", "matterhorn.yaml");
app.MapGet("/openapi/matterhorn.yaml", () => Results.File(contractPath, "application/yaml"));
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/openapi/matterhorn.yaml", "Matterhorn API v1");
    c.RoutePrefix = "swagger";
    c.DocumentTitle = "Matterhorn API";
});

app.MapControllers();
app.MapDeviceEvents();
app.Run();

public partial class Program { }
