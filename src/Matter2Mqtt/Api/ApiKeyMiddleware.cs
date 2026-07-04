using Akka.Actor;

namespace Matter2Mqtt.Api;

/// <summary>Holds the gateway actor reference for the REST layer.</summary>
public sealed record GatewayRef(IActorRef Ref);

/// <summary>Supplies the configured API key (seam so tests can substitute one).</summary>
public interface IConfigureApiKey { string? ApiKey { get; } }
public sealed record StaticApiKey(string? ApiKey) : IConfigureApiKey;

/// <summary>Rejects requests missing a valid <c>X-Api-Key</c> when a key is configured (spec §8).</summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IConfigureApiKey key)
{
    public async Task Invoke(HttpContext ctx)
    {
        // Only the API surface is protected; docs (/swagger, /openapi) stay public.
        if (ctx.Request.Path.StartsWithSegments("/api") && key.ApiKey is { Length: > 0 } expected)
        {
            // Header for normal calls; query param for EventSource (SSE), which can't set headers.
            var provided = ctx.Request.Headers["X-Api-Key"].ToString();
            if (string.IsNullOrEmpty(provided)) provided = ctx.Request.Query["api_key"].ToString();
            if (provided != expected) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        }
        await next(ctx);
    }
}
