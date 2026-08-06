# HTTP request idempotency on the Critter Stack

`Idempotency-Key` on Wolverine HTTP + Marten, extracted from a production payments API into a runnable sample.

It is here for review. The [open questions](#open-questions) are places where we worked around something instead of
finding a supported hook for it. Each one is reproduced in the repo.

## The contract

Every POST accepts a client-generated `Idempotency-Key` header:

| Situation | Response |
| --- | --- |
| New key | Reserve it, run the work, store the response |
| Same key, first request finished | Replay that response body byte for byte, with `Idempotent-Replayed: true` |
| Same key, first request still in flight | `409` |
| Same key, different request body | `422` |
| Same key, first request failed | Key is free again; the corrected retry works |

The idempotency record and the work it guards commit together or neither does. The record is a Marten document
written through the caller's own `IDocumentSession`, so there is one commit. Only 2xx responses are stored.

## Open questions

**1. Is `Response.OnStarting` the right place to release a key?**
A Wolverine `Finally` cannot be paired with a `Before` that returns `IResult`. Add
`public Task Finally(HttpContext httpContext)` to `IdempotencyMiddleware` and code generation dies:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at Wolverine.Http.CodeGen.MaybeEndWithResultFrame.GenerateCode(GeneratedMethod method, ISourceWriter writer)
      in src/Http/Wolverine.Http/CodeGen/ResultContinuationPolicy.cs:69
```

Is the pairing meant to work, and is there a supported unwinding hook for a short-circuiting `Before`?

**2. Should `[WriteAggregate]` make a chain `IsTransactional`?**
`chain.IsTransactional` is false for a chain whose only Marten shape is `[WriteAggregate]`, and that chain does get
a commit frame. Drop the `IDocumentSession` parameter from `Approve`, comment out the policy, and
`dotnet run --project src/Api -- codegen write` emits:

```csharp
(var committedAggregateOfOrder, var orderApproved) = OrderEndpoints.Approve(stream_order.Aggregate);
var order_response = await CommittedAggregate<Order>.Project(stream_order, documentSession, ...);
await documentSession.SaveChangesAsync(httpContext.RequestAborted);   // <- the frame IsTransactional says isn't there
```

So `Approve` carries an unused `IDocumentSession` purely to pass our own guard. Is there a supported way for a
policy to ask whether a chain will get a commit frame?

**3. Is `UseForResponse` + `MiddlewarePolicy` frame ordering meant to be managed by hand?**
Both append to `chain.Postprocessors` rather than positioning within it, so the `CommittedAggregate<T>` projection
frame lands behind the commit on the first pass, and behind the `After` middleware that reads its result on the
second. We re-hoist it to index 0 after every appending pass (`CommittedAggregate.HoistProjection`). Is there a
supported way to position a frame relative to the commit frame?

**4. Can the aggregate Wolverine re-fetches for an `UpdatedAggregate` response be observed?**
We assume not, and wrote `CommittedAggregate<T>` instead. Is projecting pending, uncommitted events ahead of the
save a sound thing to be doing?

**5. Reading the raw body from a chain frame.**
`EnableBuffering` plus a rewind works, but the ordering it depends on comes from registration order, in reverse, and
nothing checks it. Is there a supported way to say "this frame runs before body deserialization"?

## How it works

Frames in the endpoint's own generated chain. No ASP.NET middleware, no request-scoped bridging service.

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

**The policy has to be the last registration.** The fingerprint reads the request body before anything deserializes
it, and a Wolverine middleware frame is inserted at the *head* of a chain. Getting it wrong does not fail: the hash
is taken over a drained stream, every request digests zero bytes, and every fingerprint matches. So `Fingerprint`
throws if it reads nothing from a request that declared a body.

**A reservation and a completion use different sessions.** The reservation commits on its own session, because a
concurrent duplicate has to see it. The completion enrolls in the caller's session, because that is what makes it
all-or-nothing with the work.

**Every write after the reservation is conditioned on an ownership token.** A request that stalls past
`ReservationTimeout` loses its key to a later request. Conditioned on the token, the stalled request's delete
matches nothing, its insert collides, and its whole transaction rolls back — taking its work with it.

### `CommittedAggregate<T>`

An endpoint returning `UpdatedAggregate` hands Wolverine a marker, and Wolverine writes the response by re-fetching
the aggregate *after* the transaction commits. The completion rides that same transaction, so it runs before the
body exists. `CommittedAggregate<T>` is an `IResponseAware` marker that projects the aggregate from the events the
session has queued but not yet committed, via `session.Events.ProjectLatest<T>()`:

```csharp
[WolverinePost("/orders/{orderId}/approve")]
public static (CommittedAggregate<Order>, OrderApproved) Approve([WriteAggregate] Order order,
    IDocumentSession session) =>
    (new CommittedAggregate<Order>(), new OrderApproved());
```

Marten stamps an event's `Timestamp` and `UserName` as it saves, so a projection running ahead of the commit reads
events carrying neither. `CommittedAggregate<T>` stamps the pending events with the values Marten would have
written; Marten leaves already-set values alone, so the response and the committed document agree.

### Build-time guards

Two shapes fail code generation rather than degrade quietly:

- **A POST that cannot commit a Marten transaction.** Its completion record would insert into a session nothing
  flushes. An endpoint that genuinely cannot hold a transaction marks itself `[IdempotencyOptOut]`.
- **A POST whose response its chain never puts in scope** — the `UpdatedAggregate` shape above. The error names
  `CommittedAggregate<T>` as the fix.

## Why stored responses, and not key-as-identity

Making the client's key the identity of the command or event stream needs none of this machinery, and for a POST
that starts a stream it is the better answer. It does not generalize:

1. A request that resolves to a set or a range has no identity to be. A webhook replay naming a list of event ids
   or a time window has no entity whose id the key could be.
2. Most POSTs mutate an aggregate that already exists. The stream id is taken by the resource.
3. Current state is a different answer than the one the first caller got. If a concurrent writer moved the aggregate
   on, replaying current state under the original status code returns something the first request never produced.

## Running it

```sh
docker compose up -d          # Postgres on 5433
dotnet test                   # 50 tests
dotnet run --project src/Api  # the sample API on http://localhost:5000
```

`tests/Api.Tests/IdempotencyEndpointTests.cs` walks the whole contract over real HTTP with Alba.

```sh
curl -i -X POST http://localhost:5000/widgets \
  -H 'Content-Type: application/json' \
  -H 'X-Tenant: tenant-a' \
  -H 'Idempotency-Key: 019873e2-0000-7000-8000-000000000001' \
  -d '{"name":"first","size":3}'
```

Run it twice:

```
HTTP/1.1 201 Created                            HTTP/1.1 201 Created
Content-Type: application/json; charset=utf-8   Content-Type: application/json
Location: /widgets/019fd5c3-…                   Location: /widgets/019fd5c3-…
                                                Idempotent-Replayed: true

{"id":"019fd5c3-…","name":"first",…}            {"id":"019fd5c3-…","name":"first",…}
```

### The sample endpoints

| Endpoint | What it demonstrates |
| --- | --- |
| `POST /widgets` | Reserve → store → replay. Its refusing arm frees the key for a corrected retry |
| `POST /orders/{id}/approve` | `CommittedAggregate<T>`, and `[IdempotencyRequired]` |
| `POST /widgets/{id}/archive` | A `204`: why completion is a timestamp and not "the body is null" |
| `POST /widgets/{id}/tokens` | `[IdempotencyOmitsResponseBody]` — a one-time secret replays status and `Location` alone |
| `POST /widgets/preview` | `[IdempotencyOptOut]` — no transaction, so no guarantee |

`TenantAuthenticationHandler` stands in for real authentication, because the key is scoped to the caller. Two
tenants sending the same key must not reach each other's stored response. Do not copy it.

## Known trade-offs

- **A request that outlives its hold loses its key.** `ReservationTimeout` has to exceed the worst-case duration of
  every endpoint that can reserve one. Set it too low and a slow but healthy request loses its reservation
  mid-flight, and its own completion rolls the work back. The metric to watch is `takeover`.
- **Work that rolls back leaves its key held** until the reservation expires. It self-heals on the same timer a
  process crash does.
- **A handler that calls `SaveChangesAsync` itself gets a weaker guarantee.** The build-time check proves the
  framework will append a commit frame; it cannot see that the handler already committed. Those endpoints get the
  work in one transaction and the record in another, and no check will say so.
- **A request that never starts a response leaves its key held.** The release runs as the response starts.
- **A multipart body is not fingerprinted.** Its boundary is randomly generated, so the same logical upload sent
  twice is different bytes. The key alone identifies those requests.
- **A replayed `Content-Type` is equivalent, not identical.** It drops the `charset` parameter, because the
  description is built from the result object before the writer emits the header. The body is byte-identical.
- **`PurgeExpiredIdempotencyRecords` is housekeeping, not correctness.** Expiry is read at lookup, so a purge that
  never runs costs space alone.

## License

MIT.
