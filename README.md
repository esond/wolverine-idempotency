# HTTP request idempotency on the Critter Stack

A working extraction of how we do `Idempotency-Key` on Wolverine HTTP + Marten, pulled out of a payments API into
a runnable sample so it can be reviewed.

It is here for feedback. The mechanism is in production, the tests pass, and there are still four or five places
where we reasoned our way to an answer instead of finding one — those are listed at the bottom under
[Open questions](#open-questions). If the answer to any of them is "there's a supported hook for that", the code
should get smaller.

## What we're after

Every POST accepts a client-generated `Idempotency-Key` header:

| Situation | Response |
| --- | --- |
| New key | Reserve it, run the work, store the response |
| Same key, first request finished | Replay that response byte for byte, with `Idempotent-Replayed: true` |
| Same key, first request still in flight | `409` |
| Same key, different request body | `422` |
| Same key, first request failed | Key is free again; the corrected retry works |

And underneath all of it: **the idempotency record and the work it guards commit together, or neither does.**

That last line is the whole design. Put the record anywhere else — a cache, a second store, a second transaction —
and the order is forced: reserve the key, run the work, write the response last, because the response does not
exist any earlier. A process that dies between the commit and that last write leaves the work done with the key
still marked in flight. The next retry past the hold expiry does the work a second time.

The record lives in the same Postgres, written through the caller's own `IDocumentSession`. There is one commit.

## Why byte-for-byte, and not key-as-identity

The first reaction this design gets is a good one: *why not make the client's key the identity of the command,
operation, or event stream, let the domain enforce once-only, and answer a retry with the current state of the
resource?* No response storage, no serialization, no fight with the transaction boundary.

**For a POST that starts a stream, that is the better answer, and we would use it.** `POST /orders` with the key as
the stream id needs none of this machinery.

It stops generalizing in three places, and we needed something that covered every POST rather than the ones that
happen to fit:

1. **Requests that resolve to a set or a range have no identity to be.** One of ours replays webhook deliveries and
   names either a list of event ids or a time window. There is no entity whose id the key could be. A second
   mechanism for the second body shape is two mechanisms to keep in step.
2. **Most POSTs mutate an aggregate that already exists.** Approve, cancel, expire, confirm. The stream id is
   already taken by the resource; the key cannot be it.
3. **Current state is a different answer than the one the first caller got.** If a concurrent writer moved the
   aggregate on, replaying "current state" hands the retrying caller something the original request never produced,
   under the same status code. Byte-for-byte plus an explicit `Idempotent-Replayed` header makes the weaker claim
   honestly: *this is the result you already have*, not *the work happened again*.

Only 2xx responses are stored. A `404`, a validation failure, or a refused arm of a `Results<…>` union describes
the request that was sent, not work that happened — so the key is released and the caller can fix the body and
retry under the same key.

## The shape

Everything is frames in the endpoint's own generated chain. There is no ASP.NET middleware, no request-scoped
bridging service, and no second mechanism to keep in step with the first.

```
IdempotencyPolicy                 IHttpPolicy. Walks every POST chain at code-generation time, refuses the
                                  ones that cannot honour the guarantee, and attaches the middleware closed
                                  over that chain's own response type.

IdempotencyMiddleware.Before      Hashes the raw request body, reserves the key, and short-circuits with a
                                  replay or a refusal by returning an IResult.

IdempotencyMiddleware.After       Queues the completion record on the caller's IDocumentSession. Does not
                                  save — the chain's own commit frame carries both.

Response.OnStarting               Releases the key for any request that did not complete.
```

| File | What it is |
| --- | --- |
| `IdempotencyPolicy.cs` | The `IHttpPolicy`, and the two build-time guards |
| `IdempotencyMiddleware.cs` | Reserve / complete / release, plus the three closed-over subclasses |
| `IdempotencyStore.cs` | Reserve on its own session, complete on the caller's, release, and the ownership-token predicate |
| `CommittedAggregate.cs` | The response marker that projects the aggregate *before* the commit |
| `ResponseDescriber.cs` | Reads status, body, content type and `Location` off whatever the handler returned |
| `ReplayedResult.cs` | Writes a stored response back out unchanged |
| `IdempotencyRecord.cs` | The Marten document |
| `IdempotencyScope.cs` | Composes principal + route + supplied key into the document id |

Three things are worth calling out because they are not obvious from the code:

**The policy runs before the frames it competes with exist.** The fingerprint has to read the request body before
anything deserializes it, and a Wolverine middleware frame is inserted at the *head* of a chain — so
`AddPolicy<IdempotencyPolicy>()` has to be the **last** registration. Nothing enforces that, and getting it wrong
does not fail: the hash is taken over an already-drained stream, every request digests zero bytes, and every
fingerprint comparison passes. So `Fingerprint` throws if it reads nothing from a request that declared a body.

**A reservation and a completion use different sessions on purpose.** The reservation commits on its own session,
because a concurrent duplicate has to be able to see it. The completion enrolls in the caller's session, because
that is what makes it all-or-nothing with the work.

**Every write after the reservation is conditioned on an ownership token.** A request that stalls past
`ReservationTimeout` loses its key to a later request. Deleting by id alone would let the stalled request remove
its successor's record. Conditioned on the token, the delete matches nothing, the insert behind it collides, and
the stalled request's whole transaction rolls back — taking its work with it, which is the correct outcome.

## The aggregate problem, and `CommittedAggregate<T>`

This is the part that sent us to Discord.

An endpoint returning `UpdatedAggregate` hands Wolverine a marker, and Wolverine writes the response by re-fetching
the aggregate **after** the transaction commits. The idempotency completion rides that same transaction, so it runs
*before* the body exists. There is nothing to store. Storing the aggregate that is already in scope instead gets its
state as loaded at the start of the request, which would replay a stale body as though it were the real response.

`CommittedAggregate<T>` is an `IResponseAware` marker that projects the aggregate from the events the session has
**already queued but not yet committed**, using `session.Events.ProjectLatest<T>()`:

```csharp
[WolverinePost("/orders/{orderId}/approve")]
public static (CommittedAggregate<Order>, OrderApproved) Approve([WriteAggregate] Order order,
    IDocumentSession session) =>
    (new CommittedAggregate<Order>(), new OrderApproved());
```

The body now exists before the commit, so the completion record can store it. It is also the stronger answer on its
own terms: the projection sees exactly this transaction's events, where a post-commit re-read would absorb a
concurrent writer's.

One trap comes with it. Marten stamps an event's `Timestamp` and `UserName` **as it saves**, so a projection running
ahead of the commit reads events carrying neither, and an aggregate that derives audit metadata from its last event
answers with the year 1. `CommittedAggregate<T>` stamps the pending events with the values Marten would have
written; Marten leaves already-set values alone, so the answer and the committed document agree. There is a test for
exactly this.

## Build-time guards

Two things fail code generation rather than degrade quietly:

- **A POST that cannot commit a Marten transaction.** Its completion record would insert into a session nothing
  flushes — a key that reads as reserved forever and a replay that never comes. An endpoint that genuinely cannot
  hold a transaction marks itself `[IdempotencyOptOut]` (see `/widgets/preview`).
- **A POST whose response its chain never puts in scope.** This is exactly the `UpdatedAggregate` shape above, and
  the error message names `CommittedAggregate<T>` as the fix.

The alternative is a runtime tier of endpoints where the header is silently inert — a guarantee that holds
everywhere except where nobody checked.

## Open questions

These are the reason the repo exists. Each is something we observed and worked around; if there is a supported hook
we missed, we would rather use it.

**1. Is `Response.OnStarting` the right place to release a key?**
A Wolverine `Finally` cannot be paired with a `Before` that returns `IResult` — the frame wrapping the two never
resolves the continuation's `HttpContext`, and code generation fails. `Response.OnStarting` is what we fell back
to, and it happens to be *better* (a key released as the response starts survives an instant retry, where one
released as the pipeline unwinds races it). But we did not choose it, we were left with it. Is there a supported
unwinding hook that pairs with a short-circuiting `Before`?

**2. Should `[WriteAggregate]` make a chain `IsTransactional`?**
`chain.IsTransactional` is decided during Wolverine's own transaction-detection pass, which completes chain by
chain *before* any `IHttpPolicy` runs — so the middleware's own `IDocumentSession` parameter never counts toward
it. Fine. But it is also **false** for a chain whose only Marten shape is `[WriteAggregate]`, even though such a
chain does get a commit frame. The `Approve` endpoint above carries an unused `IDocumentSession` purely to make the
guard pass. Is there a supported way for a policy to ask *"will this chain get a commit frame?"* rather than
inferring it?

**3. Is `UseForResponse` + `MiddlewarePolicy` frame ordering meant to be managed by hand?**
Both **append** to `chain.Postprocessors` rather than positioning within it. So the `CommittedAggregate<T>`
projection frame lands behind the commit on the first pass, and behind the `After` middleware that reads its result
on the second. We re-hoist it to index 0 after every pass that appends (`CommittedAggregate.HoistProjection`). Is
there a supported way to position a frame relative to the commit frame?

**4. Is there any way to observe the aggregate Wolverine re-fetches for an `UpdatedAggregate` response?**
We assume no — a hook that runs after the response is written but still inside the handler's transaction is close
to self-contradictory. `CommittedAggregate<T>` is our answer. We would like to know whether projecting pending
events ahead of the save is a sound thing to be doing, or whether it has edges we have not hit.

**5. Reading the raw body from a chain frame.**
It works, and `EnableBuffering` + rewind is doing the job. But the ordering it depends on is set by registration
order, in reverse, and nothing checks it — which is why `Fingerprint` throws on a drained stream. Is there a
supported way to say "this frame runs before body deserialization"?

## Running it

```sh
docker compose up -d          # Postgres on 5433
dotnet test                   # 39 tests
dotnet run --project src/Api  # the sample API
```

The tests are the documentation. `tests/Api.Tests/IdempotencyEndpointTests.cs` walks the whole contract over real
HTTP with Alba.

```sh
# Create a widget twice under one key
curl -i -X POST http://localhost:5000/widgets \
  -H 'Content-Type: application/json' \
  -H 'X-Tenant: tenant-a' \
  -H 'Idempotency-Key: 019873e2-0000-7000-8000-000000000001' \
  -d '{"name":"first","size":3}'
```

Run it twice. The second response is byte-identical and carries `Idempotent-Replayed: true`.

### The sample endpoints

Each one exists to exercise a distinct arm of the mechanism.

| Endpoint | What it demonstrates |
| --- | --- |
| `POST /widgets` | Reserve → store → byte-for-byte replay. Its refusing arm frees the key for a corrected retry |
| `POST /orders/{id}/approve` | `CommittedAggregate<T>`: an aggregate response stored inside its own transaction. Also `[IdempotencyRequired]` |
| `POST /widgets/{id}/archive` | A `204`: why completion is a timestamp and not "the body is null" |
| `POST /widgets/{id}/tokens` | `[IdempotencyOmitsResponseBody]` — a one-time secret replays status and `Location` alone |
| `POST /widgets/preview` | `[IdempotencyOptOut]` — no transaction, so no guarantee, and it says so |

### Sample scaffolding, not part of the mechanism

`TenantAuthenticationHandler` reads an `X-Tenant` header and calls it a principal. It stands in for real
authentication because the key is scoped to the caller: two tenants sending the same key must not reach each
other's stored response, and there is a test for that. Do not copy it.

## Known trade-offs

- **A key is held for a bounded time, and a request that outlives its hold loses it.** `ReservationTimeout` has to
  exceed the worst-case duration of every endpoint that can reserve a key. Set below it, a slow but healthy request
  loses its reservation mid-flight and its own completion rolls the work back. The metric to watch is `takeover`.
- **Work that rolls back leaves its key held** until the reservation expires. The completion is queued before the
  save, so a save that fails for an unrelated reason rolls the record back while the reservation reads as complete
  in memory. It self-heals on the same timer a process crash does.
- **A handler that calls `SaveChangesAsync` itself gets a weaker guarantee.** The build-time check proves the
  framework will append a commit frame; it cannot see that the handler already committed. Those endpoints get work
  in one transaction and the record in another — the exact split this design exists to close — and no check will
  say so.
- **A request that never starts a response leaves its key held.** The release runs as the response starts, so an
  abandoned connection keeps its key until the hold expires.
- **A multipart body is not fingerprinted.** Its boundary is randomly generated, so the same logical upload sent
  twice is different bytes and hashing would refuse the retry as a different request. The key alone identifies
  those requests. (The sample has no upload endpoint; the production one does.)
- **`PurgeExpiredIdempotencyRecords` is housekeeping, not correctness.** Expiry is read when a key is looked up, so
  a purge that never runs costs space alone. Wire it to whatever scheduler the host already has.

## License

MIT.
