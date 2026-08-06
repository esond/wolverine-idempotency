using JasperFx.CodeGeneration.Frames;
using JasperFx.Events;
using Marten;
using Wolverine;
using Wolverine.Configuration;

namespace Idempotency;

/// <summary>
/// Endpoint response marker answering with the aggregate as this request's own transaction leaves it.
/// </summary>
/// <remarks>
/// <see cref="Wolverine.Marten.UpdatedAggregate" /> re-reads the aggregate after the commit, so the body does not
/// exist until the transaction is closed. Projecting from the events already queued on the session puts the body in
/// reach of anything that must commit alongside the work it describes. It also pins the answer to this transaction:
/// a concurrent writer's events cannot reach it, where a post-commit re-read absorbs them.
///
/// Needs an <see cref="IEventStream{T}" /> in scope, which is the stream the pending events are read from. Every
/// writing arm of the aggregate handler workflow puts one there.
/// </remarks>
// IResponseAware declares ConfigureResponse as a static abstract, so CA1000 (avoid static members on generic types)
// describes the interface rather than anything this type chose.
#pragma warning disable CA1000
public sealed class CommittedAggregate<T> : IResponseAware where T : class
{
    public static void ConfigureResponse(IChain chain)
    {
        var frame = new MethodCall(typeof(CommittedAggregate<T>), nameof(Project));

        chain.UseForResponse(frame);

        chain.Tags[CommittedAggregate.FrameTag] = frame;

        CommittedAggregate.HoistProjection(chain);
    }

    public static async Task<T?> Project(IEventStream<T> stream, IDocumentSession session,
        CancellationToken cancellationToken)
    {
        StampPendingEvents(session);

        return await session.Events.ProjectLatest<T>(stream.Id, cancellationToken);
    }

    /// <summary>
    /// Puts the audit metadata Marten assigns while saving onto the events this transaction has queued but not yet
    /// committed.
    /// </summary>
    /// <remarks>
    /// A queued event carries the default <see cref="DateTimeOffset" /> and no user name until the save stamps it,
    /// so a projection deriving audit metadata from the last event answers with the year 1 and the system identity.
    /// Marten leaves values that are already set alone, and the values written here are the ones it would have
    /// written, so the projected aggregate and the rows the commit goes on to write agree.
    ///
    /// An event's headers are left to the save. Reading one back parses it out of JSON, which an object assigned in
    /// memory would not satisfy, so an aggregate deriving a display name from a header gets the system actor here.
    /// </remarks>
    private static void StampPendingEvents(IDocumentSession session)
    {
        var timestamp = DateTimeOffset.UtcNow;

        foreach (var pending in session.PendingChanges.Streams().SelectMany(stream => stream.Events))
        {
            if (pending.Timestamp == default)
                pending.Timestamp = timestamp;

            pending.UserName ??= session.LastModifiedBy;
        }
    }
}
#pragma warning restore CA1000

public static class CommittedAggregate
{
    internal const string FrameTag = nameof(CommittedAggregate);

    /// <summary>
    /// Moves the projection frame of <paramref name="chain" /> to the head of its postprocessors, ahead of the frame
    /// that commits, and does nothing for a chain that has no such frame.
    /// </summary>
    /// <remarks>
    /// Both <see cref="IChain.UseForResponse" /> and Wolverine's middleware policy append to the postprocessor list
    /// rather than position within it, so the frame lands behind the commit on the first pass and behind any
    /// <c>After</c> middleware that reads its result on the second. Every pass that appends has to be followed by
    /// another call to this.
    /// </remarks>
    public static void HoistProjection(IChain chain)
    {
        if (chain.Tags.GetValueOrDefault(FrameTag) is not Frame frame)
            return;

        chain.Postprocessors.Remove(frame);
        chain.Postprocessors.Insert(0, frame);
    }
}
