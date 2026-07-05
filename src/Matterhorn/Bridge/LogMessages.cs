namespace Matterhorn.Bridge;

/// <summary>A single dashboard log line. Published on the actor-system EventStream by the
/// gateway; retained (bounded) by <see cref="LogBufferActor"/> and forwarded to SSE.</summary>
public record LogEntry(
    DateTimeOffset Ts,
    LogCategory Category,   // Activity = human milestone, Raw = wire event
    string Kind,            // slug: "commission", "joined", "attribute_updated", ...
    string Message,         // fully-rendered line text
    string? Device,         // friendly name when known
    LogLevel Level);        // drives colour: Info | Ok | Warn

public enum LogCategory { Activity, Raw }
public enum LogLevel { Info, Ok, Warn }

/// <summary>Ask the <see cref="LogBufferActor"/> for its retained buffer (replayed on SSE connect).</summary>
public record GetLogSnapshot;
public record LogSnapshot(IReadOnlyList<LogEntry> Activity, IReadOnlyList<LogEntry> Raw);
