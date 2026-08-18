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

**1. ~~Is `Response.OnStarting` the right place to release a key?~~ Answered — the unwinding hook exists now.**
A Wolverine `Finally` could not be paired with a `Before` that returns `IResult`. Adding `public Task Finally()` to
`IdempotencyMiddleware` killed code generation:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at Wolverine.Http.CodeGen.MaybeEndWithResultFrame.GenerateCode(GeneratedMethod method, ISourceWriter writer)
      in src/Http/Wolverine.Http/CodeGen/ResultContinuationPolicy.cs:69
```

[JasperFx/wolverine#3895](https://github.com/JasperFx/wolverine/pull/3895) fixed it, in 6.25.5. Every idempotent
chain now wraps everything after `Before` in a `try`/`finally`, and the short-circuit `return` unwinds through it.

`OnStarting` stays the release, because the `finally` runs *after* the response is written — a client already
holding the refusal can beat it to the retry, which is
`A_request_that_fails_validation_frees_its_key_for_a_corrected_retry` failing. `Finally` is the backstop for the one
case `OnStarting` cannot see: a request the host never answered, whose key used to stay held until its reservation
expired.

**2. ~~Should `[WriteAggregate]` make a chain `IsTransactional`?~~ Answered — the flag is trustworthy now.**
`chain.IsTransactional` was false for a chain whose only Marten shape is `[WriteAggregate]`, while that chain did
get a commit frame. The policy carried a second check for that shape (`IdempotencyPolicy.WritesAggregate`),
duplicating the one fragment of the detection the flag missed; an earlier revision made every such handler carry an
unused `IDocumentSession` instead.

[JasperFx/wolverine#3893](https://github.com/JasperFx/wolverine/issues/3893) was fixed by
[#3901](https://github.com/JasperFx/wolverine/pull/3901), in 6.26.0, and 6.27.0
([#3911](https://github.com/JasperFx/wolverine/pull/3911)) closed the same disagreement for a chain made
transactional by an `IMartenOp` return. `IdempotencyPolicy` asks `chain.IsTransactional` and nothing else.

**3. Is frame placement by variable dependency a contract?**
We shipped a workaround that re-hoisted the `CommittedAggregate<T>` projection frame to the head of
`chain.Postprocessors`, reasoning that `UseForResponse` and Wolverine's middleware policy both append behind the
commit. Community review showed it was dead code: JasperFx places frames by variable dependency, not list position,
and the generated chain is byte-identical with the frame hoisted, left at the tail, or never moved. The order the
guarantee needs — project, complete, then `SaveChangesAsync` — comes out right because the completion middleware
consumes the projected response. Nothing documents that placement as a contract. Is it one?

**4. Can the aggregate Wolverine re-fetches for an `UpdatedAggregate` response be observed?**
We assume not, and wrote `CommittedAggregate<T>` instead. Is projecting pending, uncommitted events ahead of the
save a sound thing to be doing?

**5. Reading the raw body from a chain frame.**
`EnableBuffering` plus a rewind works, but the ordering it depends on comes from registration order, in reverse, and
nothing checks it. Is there a supported way to say "this frame runs before body deserialization"?

**6. A `Finally` alongside an endpoint returning a bare `IResult` generates a chain that will not compile.**
Wolverine names a generated local after the type that produced it, so an endpoint returning
`Results<NoContent, NotFound>` gets `resultsOfNoContentAndNotFound` and never meets the `result` that
`IdempotencyMiddleware.Before` returns. A bare `IResult` gets `result` too. Without a `Finally` the arranger sees
both in one scope and renames the earlier one; the `try` that `Finally` introduces puts them in nested scopes,
where that rename no longer applies but C# still forbids the shadowing:

```csharp
var result = await idempotencyResponseMiddlewareOfResult.Before(httpContext);   // outer scope
try
{
    var result = await WidgetEndpoints.Rename(id, command, documentSession);    // CS0136, and CS0841 above it
```

So question 1's fix reintroduced the same shape's problem one layer down. `POST /widgets/{id}/name` is the
reproduction. Nothing fails at startup — code generation is per chain and lazy, so the host boots and only a
request that reaches that endpoint discovers its chain never compiled, as a 500. Under `codegen write` it fails
the build. Reproduced on 6.29.0 and not filed upstream.

Community review confirmed 1 and 2 as Wolverine bugs — 1 doubling as the ask for an unwinding hook its workaround
stood in for — and 5 as a feature request. 1 was filed as
[JasperFx/wolverine#3892](https://github.com/JasperFx/wolverine/issues/3892) and fixed in 6.25.5; 2 as
[JasperFx/wolverine#3893](https://github.com/JasperFx/wolverine/issues/3893) and fixed in 6.26.0. 5 and 6 are not
filed. The workaround sites in the code point back here.

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

Response.OnStarting               Releases the key for any request that did not complete, ahead of the
                                  response the caller would retry on.

IdempotencyMiddleware.Finally     Backstop for a request the host never answered, which OnStarting cannot
                                  see. Runs from the chain's own `finally`.
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
public static (CommittedAggregate<Order>, OrderApproved) Approve([WriteAggregate] Order order) =>
    (new CommittedAggregate<Order>(), new OrderApproved());
```

Marten assigns an event's `Version`, `Timestamp` and `UserName` as it saves, so a projection running ahead of the
commit reads events carrying none of them. `CommittedAggregate<T>` stamps the pending events with the values Marten
is going to write — versions count up from the stream's expected server version, and Marten leaves already-set
values alone — so the response and the committed document agree.

That set is also the boundary. An event's global `Sequence` is drawn from a database sequence inside the save, so
no pre-commit value can match it, and headers are parsed back out of JSON only after the save writes them. An
aggregate deriving state from either is not supported behind `CommittedAggregate<T>`.

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
dotnet test                   # 51 tests
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
| `POST /widgets/{id}/name` | Open question 6. A bare `IResult` return, so its chain does not compile — it answers 500 |

`TenantAuthenticationHandler` stands in for real authentication, because the key is scoped to the caller. Two
tenants sending the same key must not reach each other's stored response. Do not copy it.

## Known trade-offs

- **A request that outlives its hold loses its key.** `ReservationTimeout` has to exceed the worst-case duration of
  every endpoint that can reserve one. Losing the hold costs nothing by itself: a completion whose key was never
  taken over finds its own token still on the record, expired or not, and commits normally. The rollback fires only
  when a later request actually took the key — at which point a duplicate dispatch is live, and letting both
  transactions commit is the double execution the mechanism exists to prevent. Nothing durable is lost either way;
  the transaction that rolls back never committed. The metric to watch is `takeover`.
- **Work that rolls back leaves its key held** until the reservation expires. It self-heals on the same timer a
  process crash does. Wolverine 6.29.0's `AfterCommit` hook looks like the fix — treat a completion as real only
  once the commit lands — and is not. On an HTTP chain its frame is emitted *after* the response-writing
  postprocessor, so it runs after `OnStarting` has already decided, and a failure between the commit and the
  response write would skip it and let the unwind release a key whose work did commit.
- **A handler that calls `SaveChangesAsync` itself gets a weaker guarantee.** The build-time check proves the
  framework will append a commit frame; it cannot see that the handler already committed. Those endpoints get the
  work in one transaction and the record in another, and no check will say so.
- **A multipart body is not fingerprinted.** Its boundary is randomly generated, so the same logical upload sent
  twice is different bytes. The key alone identifies those requests.
- **A replayed `Content-Type` is equivalent, not identical.** It drops the `charset` parameter, because the
  description is built from the result object before the writer emits the header. The body is byte-identical.
- **`PurgeExpiredIdempotencyRecords` is housekeeping, not correctness.** Expiry is read at lookup, so a purge that
  never runs costs space alone.

## License

MIT.
