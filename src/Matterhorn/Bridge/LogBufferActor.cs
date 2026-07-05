using Akka.Actor;

namespace Matterhorn.Bridge;

/// <summary>
/// Singleton ring buffer of recent <see cref="LogEntry"/> lines. Subscribes to the EventStream
/// and keeps the last N per category so a browser that connects (or reloads) can replay recent
/// history before going live. Survives page reload, not app restart.
/// </summary>
public sealed class LogBufferActor : ReceiveActor
{
    private const int ActivityCap = 100;
    private const int RawCap = 200;
    private readonly Queue<LogEntry> _activity = new();
    private readonly Queue<LogEntry> _raw = new();

    public static Props Props() => Akka.Actor.Props.Create(() => new LogBufferActor());

    public LogBufferActor()
    {
        Receive<LogEntry>(e =>
        {
            var (q, cap) = e.Category == LogCategory.Activity ? (_activity, ActivityCap) : (_raw, RawCap);
            q.Enqueue(e);
            while (q.Count > cap) q.Dequeue();
        });
        Receive<GetLogSnapshot>(_ =>
            Sender.Tell(new LogSnapshot(_activity.ToList(), _raw.ToList())));
    }

    protected override void PreStart() => Context.System.EventStream.Subscribe(Self, typeof(LogEntry));
}
