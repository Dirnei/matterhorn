using Akka.Actor;
namespace Matterhorn.Groups;
/// <summary>DI handle to the groups supervisor for the REST facade.</summary>
public sealed record GroupsRef(IActorRef Ref);
