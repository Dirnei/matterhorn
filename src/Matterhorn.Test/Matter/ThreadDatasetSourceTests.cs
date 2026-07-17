using System.Net;
using Matterhorn.Configuration;
using Matterhorn.Matter;

namespace Matterhorn.Test.Matter;

public class ThreadDatasetSourceTests
{
    private const string Hex = "0e080000000000030001000300000f0510e5e4f21c05d33938c324eeed03255fca";

    private static MatterhornConfig Config(string? dataset = null, string? otbrUrl = null) => new(
        ControllerWsUrl: "ws://localhost:5580/ws", ControllerKind: "fake",
        MqttHost: "localhost", MqttPort: 1883, MqttUser: null, MqttPassword: null,
        BaseTopic: "matterhorn", RestEnabled: true, RestPort: 8090, ApiKey: null,
        ThreadDataset: dataset, ThreadOtbrUrl: otbrUrl,
        NamesFile: "names.json", GroupsFile: "groups.json", ScenesFile: "scenes.json");

    /// <summary>Answers every request with a canned response, and counts them.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string> Urls = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static StubHandler Ok(string body) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body),
    });

    [Fact]
    public async Task No_border_router_and_no_dataset_is_unavailable_not_an_error()
    {
        var src = new ThreadDatasetSource(Config(), new HttpClient(Ok(Hex)));
        var got = await src.Resolve(default);
        Assert.False(got.Available);
        Assert.Equal("none", got.Source);
        Assert.NotNull(got.Reason);
    }

    [Fact]
    public async Task Configured_dataset_wins_over_the_border_router()
    {
        // The literal is the escape hatch for a border router we can't read; it must not be
        // second-guessed by a fetch.
        var stub = Ok(Hex);
        var src = new ThreadDatasetSource(Config(dataset: "abcdef", otbrUrl: "http://otbr:8080"), new HttpClient(stub));

        var got = await src.Resolve(default);

        Assert.Equal("abcdef", got.Dataset);
        Assert.Equal("configured", got.Source);
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task Fetches_the_dataset_from_the_border_router()
    {
        var stub = Ok(Hex);
        var src = new ThreadDatasetSource(Config(otbrUrl: "http://otbr:8080"), new HttpClient(stub));

        var got = await src.Resolve(default);

        Assert.Equal(Hex, got.Dataset);
        Assert.Equal("otbr", got.Source);
        Assert.Equal("http://otbr:8080", got.BorderRouter);
        Assert.Equal("http://otbr:8080/node/dataset/active", Assert.Single(stub.Urls));
    }

    [Fact]
    public async Task Trailing_slash_on_the_url_does_not_double_up()
    {
        var stub = Ok(Hex);
        var src = new ThreadDatasetSource(Config(otbrUrl: "http://otbr:8080/"), new HttpClient(stub));
        await src.Resolve(default);
        Assert.Equal("http://otbr:8080/node/dataset/active", Assert.Single(stub.Urls));
    }

    [Fact]
    public async Task Whitespace_around_the_fetched_dataset_is_trimmed()
    {
        var src = new ThreadDatasetSource(Config(otbrUrl: "http://otbr:8080"), new HttpClient(Ok($"{Hex}\n")));
        Assert.Equal(Hex, (await src.Resolve(default)).Dataset);
    }

    [Fact]
    public async Task A_border_router_with_no_thread_network_is_unavailable()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var src = new ThreadDatasetSource(Config(otbrUrl: "http://otbr:8080"), new HttpClient(stub));

        var got = await src.Resolve(default);

        Assert.False(got.Available);
        Assert.Contains("404", got.Reason);
    }

    [Fact]
    public async Task An_unreachable_border_router_degrades_instead_of_throwing()
    {
        // A border router that is down must not break commissioning of on-network devices, let
        // alone the bridge.
        var stub = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var src = new ThreadDatasetSource(Config(otbrUrl: "http://otbr:8080"), new HttpClient(stub));

        var got = await src.Resolve(default);

        Assert.False(got.Available);
        Assert.Contains("unreachable", got.Reason);
    }

    [Fact]
    public async Task A_non_hex_body_is_rejected()
    {
        var src = new ThreadDatasetSource(Config(otbrUrl: "http://otbr:8080"), new HttpClient(Ok("<html>nope</html>")));
        Assert.False((await src.Resolve(default)).Available);
    }

    [Fact]
    public async Task A_resolved_dataset_is_cached()
    {
        var stub = Ok(Hex);
        var src = new ThreadDatasetSource(Config(otbrUrl: "http://otbr:8080"), new HttpClient(stub));

        await src.Resolve(default);
        await src.Resolve(default);

        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task A_failure_is_retried_rather_than_cached()
    {
        // Otherwise a border router that boots after Matterhorn is never picked up.
        var fail = true;
        var stub = new StubHandler(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Hex) });
        var src = new ThreadDatasetSource(Config(otbrUrl: "http://otbr:8080"), new HttpClient(stub));

        Assert.False((await src.Resolve(default)).Available);
        fail = false;
        Assert.Equal(Hex, (await src.Resolve(default)).Dataset);
    }
}
