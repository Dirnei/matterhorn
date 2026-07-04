using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Matterhorn.Matter;

namespace Matterhorn.Bridge;

/// <summary>
/// Akka.Streams graph that feeds controller events into the actor model with backpressure.
/// All events are delivered to the gateway, which owns endpoint registration and
/// routing — so <see cref="NodeAdded"/> is always processed before the attributes that follow
/// it (FIFO mailbox), and endpoint lookup stays single-threaded.
/// </summary>
public static class IngestionPipeline
{
    public static (ISourceQueueWithComplete<MatterEvent> Queue, Task Completion) Run(
        ActorSystem sys, IActorRef gateway)
    {
        var mat = sys.Materializer();
        var source = Source.Queue<MatterEvent>(256, OverflowStrategy.DropHead);
        var graph = source.To(Sink.ForEach<MatterEvent>(evt => gateway.Tell(evt)));
        var queue = graph.Run(mat);
        return (queue, Task.CompletedTask);
    }
}
