using Akka.Actor;
namespace Matterhorn.Scenes;
/// <summary>DI handle to the scenes supervisor for the REST facade.</summary>
public sealed record ScenesRef(IActorRef Ref);
