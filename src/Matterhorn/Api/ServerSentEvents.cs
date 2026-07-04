using System.Text.Json;
using System.Threading.Channels;
using Akka.Actor;
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
        app.MapGet("/api/events", async (HttpContext ctx, ActorSystem system) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var channel = Channel.CreateUnbounded<string>();
            var bridge = system.ActorOf(SseBridgeActor.Props(channel.Writer));
            system.EventStream.Subscribe(bridge, typeof(DeviceStateChanged));
            system.EventStream.Subscribe(bridge, typeof(DeviceListChanged));
            try
            {
                await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
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
    }
}
