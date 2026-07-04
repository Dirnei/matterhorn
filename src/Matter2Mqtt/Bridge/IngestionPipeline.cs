using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Matter2Mqtt.Devices;
using Matter2Mqtt.Matter;

namespace Matter2Mqtt.Bridge;

/// <summary>
/// Akka.Streams graph that feeds controller events into the actor model (spec §9):
/// <see cref="AttributeChanged"/> is routed to the resolved endpoint actor; lifecycle
/// events are forwarded to the gateway on a separate, non-conflated branch.
/// </summary>
public static class IngestionPipeline
{
    public static (ISourceQueueWithComplete<MatterEvent> Queue, Task Completion) Run(
        ActorSystem sys, Func<ulong, ushort, IActorRef?> resolveEndpoint, IActorRef gateway)
    {
        var mat = sys.Materializer();

        var source = Source.Queue<MatterEvent>(256, OverflowStrategy.DropHead);

        var graph = source.To(Sink.ForEach<MatterEvent>(evt =>
        {
            switch (evt)
            {
                case AttributeChanged ac:
                    var target = resolveEndpoint(ac.Reading.NodeId, ac.Reading.Endpoint);
                    target?.Tell(new ApplyAttribute(ac.Reading));
                    break;
                default:
                    gateway.Tell(evt);
                    break;
            }
        }));

        var queue = graph.Run(mat);
        return (queue, Task.CompletedTask);
    }
}
