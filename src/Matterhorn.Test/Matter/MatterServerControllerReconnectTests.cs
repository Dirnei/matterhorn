using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Matterhorn.Matter;

namespace Matterhorn.Test.Matter;

/// <summary>
/// Exercises the live <see cref="MatterServerController"/> against a throwaway in-process WebSocket
/// server, so the reconnect behaviour is covered without a real matter-server. Regression for a
/// controller-host reboot leaving the bridge permanently disconnected.
/// </summary>
public class MatterServerControllerReconnectTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Reconnects_and_keeps_streaming_after_the_server_drops()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/ws/");
        listener.Start();

        var connections = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Server: accept a connection, emit one event, then hang up — every time. A client that only
        // connects once sees exactly one connection; a reconnecting one drives the count up.
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().WaitAsync(stop.Token); }
                catch { break; }

                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                Interlocked.Increment(ref connections);
                using var ws = wsCtx.WebSocket;
                var frame = Encoding.UTF8.GetBytes("""{"event":"node_removed","data":7}""");
                try
                {
                    await ws.SendAsync(frame, WebSocketMessageType.Text, true, stop.Token);
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "drop", stop.Token);
                }
                catch { /* client may already be gone */ }
            }
        });

        var controller = new MatterServerController($"ws://localhost:{port}/ws/");
        var events = new List<MatterEvent>();
        try
        {
            await foreach (var e in controller.ConnectAndListen(stop.Token))
            {
                events.Add(e);
                if (Volatile.Read(ref connections) >= 3) { stop.Cancel(); break; }
            }
        }
        catch (OperationCanceledException) { /* our own cancel to end the test */ }

        listener.Stop();
        await server;

        // >= 3 accepts means it came back at least twice after the first drop — a single-shot client
        // would be stuck at 1. And the event from each session made it through.
        Assert.True(connections >= 3, $"expected the client to reconnect (>=3 connections), got {connections}");
        Assert.All(events, e => Assert.IsType<NodeRemoved>(e));
        Assert.Equal(7UL, ((NodeRemoved)events[0]).NodeId);
    }
}
