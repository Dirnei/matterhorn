using System.Text.Json;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Hosting;
using Matterhorn.Bridge;
using Matterhorn.Devices;

namespace Matterhorn.Api;

/// <summary>
/// <c>GET /api/events</c> — a Server-Sent Events stream that pushes device state changes and
/// device-list changes to the UI. Each browser connection gets a bridge actor
/// subscribed to the actor-system EventStream; its messages are streamed as SSE frames.
/// </summary>
public static class ServerSentEvents
{
    public static void MapDeviceEvents(this WebApplication app)
    {
        app.MapGet("/api/events", async (HttpContext ctx, ActorSystem system, ActorRegistry registry) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var channel = Channel.CreateUnbounded<string>();
            var bridge = system.ActorOf(SseBridgeActor.Props(channel.Writer));
            system.EventStream.Subscribe(bridge, typeof(DeviceStateChanged));
            system.EventStream.Subscribe(bridge, typeof(DeviceListChanged));
            system.EventStream.Subscribe(bridge, typeof(LogEntry));
            system.EventStream.Subscribe(bridge, typeof(Matterhorn.Groups.GroupListChanged));
            try
            {
                await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                // Replay recent log history (one chronological stream, both categories) so a
                // reload / late connection isn't blank.
                var buffer = registry.Get<LogBufferActor>();
                var snap = await buffer.Ask<LogSnapshot>(new GetLogSnapshot(), TimeSpan.FromSeconds(2), ctx.RequestAborted);
                foreach (var e in snap.Entries)
                {
                    await ctx.Response.WriteAsync($"data: {SseBridgeActor.LogFrame(e)}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }

                await foreach (var msg in channel.Reader.ReadAllAsync(ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync($"data: {msg}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally
            {
                system.EventStream.Unsubscribe(bridge);
                system.Stop(bridge);
            }
        });
    }
}

/// <summary>Forwards EventStream device events to one SSE connection's channel as JSON frames.</summary>
public sealed class SseBridgeActor : ReceiveActor
{
    public static Props Props(ChannelWriter<string> writer) =>
        Akka.Actor.Props.Create(() => new SseBridgeActor(writer));

    public SseBridgeActor(ChannelWriter<string> writer)
    {
        Receive<DeviceStateChanged>(e => writer.TryWrite(
            $"{{\"type\":\"state\",\"device\":{JsonSerializer.Serialize(e.FriendlyName)},\"state\":{e.StateJson}}}"));
        Receive<DeviceListChanged>(_ => writer.TryWrite("{\"type\":\"devices\"}"));
        Receive<LogEntry>(e => writer.TryWrite(LogFrame(e)));
        Receive<Matterhorn.Groups.GroupListChanged>(_ => writer.TryWrite("{\"type\":\"groups\"}"));
    }

    /// <summary>Renders one log line as the dashboard SSE frame. Shared by live (this actor) and
    /// the snapshot replay in <see cref="ServerSentEvents"/>.</summary>
    public static string LogFrame(LogEntry e) =>
        $"{{\"type\":\"log\",\"ts\":\"{e.Ts:HH:mm:ss}\"," +
        $"\"category\":\"{(e.Category == LogCategory.Activity ? "activity" : "raw")}\"," +
        $"\"kind\":{JsonSerializer.Serialize(e.Kind)}," +
        $"\"msg\":{JsonSerializer.Serialize(e.Message)}," +
        $"\"device\":{JsonSerializer.Serialize(e.Device)}," +
        $"\"level\":\"{e.Level.ToString().ToLowerInvariant()}\"}}";
}
