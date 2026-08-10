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
        // Appended to the tail of chain.Postprocessors, yet generated ahead of the completion middleware and the
        // commit: JasperFx places a frame by variable dependency, not list position, and the completion consumes the
        // response this frame creates. Nothing documents that placement as a contract — README open question 3.
        chain.UseForResponse(new MethodCall(typeof(CommittedAggregate<T>), nameof(Project)));
    }

    public static async Task<T?> Project(IEventStream<T> stream, IDocumentSession session,
        CancellationToken cancellationToken)
    {
        StampPendingEvents(session);

        return await session.Events.ProjectLatest<T>(stream.Id, cancellationToken);
    }

    /// <summary>
    /// Puts the metadata Marten assigns while saving onto the events this transaction has queued but not yet
    /// committed.
    /// </summary>
    /// <remarks>
    /// A queued event carries version 0, the default <see cref="DateTimeOffset" /> and no user name until the save
    /// stamps them, so a projection running ahead of the commit answers with version 0, the year 1 and the system
    /// identity. Marten leaves values that are already set alone, and the values written here are the ones it would
    /// have written — versions count up from the stream's expected server version, exactly as the save assigns them
    /// — so the projected aggregate and the rows the commit goes on to write agree.
    ///
    /// That is the full set this can make agree. An event's global <c>Sequence</c> is drawn from a database sequence
    /// inside the save, so no value written here can match it; an aggregate deriving state from it reads 0. Headers
    /// are left to the save too: reading one back parses it out of JSON, which an object assigned in memory would
    /// not satisfy, so an aggregate deriving a display name from a header gets the system actor here.
    /// </remarks>
    private static void StampPendingEvents(IDocumentSession session)
    {
        var timestamp = DateTimeOffset.UtcNow;

        foreach (var stream in session.PendingChanges.Streams())
        {
            var version = stream.ExpectedVersionOnServer ?? 0;

            foreach (var pending in stream.Events)
            {
                version++;

                if (pending.Version == 0)
                    pending.Version = version;

                if (pending.Timestamp == default)
                    pending.Timestamp = timestamp;

                pending.UserName ??= session.LastModifiedBy;
            }
        }
    }
}
#pragma warning restore CA1000
