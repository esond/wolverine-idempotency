using System.Net;
using Alba;
using Idempotency.Sample;
using Marten;

namespace Idempotency.Tests;

/// <summary>
/// The mechanism as an integrator meets it: the header on a real endpoint, over HTTP.
/// </summary>
public class IdempotencyEndpointTests(TestHost fixture) : IntegrationTestBase(fixture)
{
    [Test]
    public async Task Repeating_a_key_replays_the_created_response_without_repeating_the_work()
    {
        var name = NewName();
        var key = NewKey();

        var first = await CreateWidget(name, key: key);
        var replay = await CreateWidget(name, key: key);

        using (Assert.Multiple())
        {
            await Assert.That(await replay.ReadAsTextAsync()).IsEqualTo(await first.ReadAsTextAsync());
            await Assert.That(LocationOf(replay)).IsEqualTo(LocationOf(first));
            await Assert.That(WasReplayed(replay)).IsTrue();
            await Assert.That(WasReplayed(first)).IsFalse();
            await Assert.That(await CountWidgets(name)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Repeating_a_key_with_a_different_body_is_unprocessable()
    {
        var key = NewKey();

        await CreateWidget(NewName(), key: key);

        await Scenario(scenario =>
        {
            scenario.Post.Json(new CreateWidget(NewName(), 3)).ToUrl("/widgets");
            scenario.StatusCodeShouldBe(HttpStatusCode.UnprocessableEntity);
        }, key);
    }

    [Test]
    public async Task Omitting_the_key_leaves_repeated_requests_doing_the_work_twice()
    {
        var name = NewName();

        await CreateWidget(name);
        await CreateWidget(name);

        await Assert.That(await CountWidgets(name)).IsEqualTo(2);
    }

    [Test]
    public async Task A_request_that_fails_validation_frees_its_key_for_a_corrected_retry()
    {
        var key = NewKey();

        await Scenario(scenario =>
        {
            scenario.Post.Json(new CreateWidget(NewName(), 0)).ToUrl("/widgets");
            scenario.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        }, key);

        // Same key, corrected body, with no delay in between — the release must already have landed. A held key
        // would answer 422 for the changed fingerprint instead.
        await CreateWidget(NewName(), key: key);
    }

    [Test]
    public async Task Repeating_a_key_replays_a_no_content_response_without_a_body()
    {
        var widget = await Body<Widget>(CreateWidget(NewName()));
        var key = NewKey();

        await ArchiveWidget(widget.Id, key);
        var replay = await ArchiveWidget(widget.Id, key);

        using (Assert.Multiple())
        {
            await Assert.That(await replay.ReadAsTextAsync()).IsEmpty();
            await Assert.That(replay.Context.Response.ContentType).IsNull();
            await Assert.That(WasReplayed(replay)).IsTrue();
        }
    }

    [Test]
    public async Task Repeating_a_key_replays_an_aggregate_response_without_appending_a_second_event()
    {
        var order = await PlaceOrder();
        var key = NewKey();

        var first = await ApproveOrder(order.Id, key);
        var replay = await ApproveOrder(order.Id, key);

        using (Assert.Multiple())
        {
            await Assert.That(await replay.ReadAsTextAsync()).IsEqualTo(await first.ReadAsTextAsync());
            await Assert.That(WasReplayed(replay)).IsTrue();
            await Assert.That(WasReplayed(first)).IsFalse();
            await Assert.That(await CountApprovals(order.Id)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task An_aggregate_response_describes_the_document_its_own_transaction_committed()
    {
        // The body is projected from events the transaction has queued but not committed, so the audit metadata a
        // caller is answered with is only right if the request stamps those events itself.
        var order = await PlaceOrder();

        var answered = await Body<Order>(ApproveOrder(order.Id, NewKey()));
        var stored = await LoadOrder(order.Id);

        using (Assert.Multiple())
        {
            await Assert.That(answered.Status).IsEqualTo(OrderStatus.Approved);
            await Assert.That(answered.Status).IsEqualTo(stored.Status);
            await Assert.That(answered.Version).IsEqualTo(stored.Version);
            await Assert.That(answered.LastModified).IsEqualTo(stored.LastModified);
        }
    }

    [Test]
    public async Task An_endpoint_marked_required_refuses_a_request_without_a_key()
    {
        var order = await PlaceOrder();

        await Scenario(scenario =>
        {
            scenario.Post.Url($"/orders/{order.Id}/approve");
            scenario.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Test]
    public async Task Supplying_a_key_as_an_anonymous_caller_is_a_bad_request()
    {
        // There is no principal to scope the key under, so honouring it would let one anonymous caller replay
        // another's response.
        await Host.Scenario(scenario =>
        {
            scenario.WithRequestHeader(IdempotencyHeaderNames.IdempotencyKey, NewKey());

            scenario.Post.Json(new CreateWidget(NewName(), 3)).ToUrl("/widgets");

            scenario.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Test]
    public async Task Two_principals_may_hold_the_same_key_without_reaching_each_other()
    {
        var name = NewName();
        var key = NewKey();

        await CreateWidget(name, key: key);
        var other = await CreateWidget(name, key: key, tenant: "tenant-b");

        using (Assert.Multiple())
        {
            await Assert.That(WasReplayed(other)).IsFalse();
            await Assert.That(await CountWidgets(name)).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Reusing_a_key_across_different_query_strings_does_not_replay()
    {
        // The query string is part of what a request asks for. Were the key scoped to the path alone, the second
        // request would be handed the first one's response and any check reading that parameter would never run.
        var name = NewName();
        var key = NewKey();

        await CreateWidget(name, key: key, attempt: "first");
        await CreateWidget(name, key: key, attempt: "second");

        await Assert.That(await CountWidgets(name)).IsEqualTo(2);
    }

    [Test]
    public async Task Supplying_a_malformed_key_is_a_bad_request()
    {
        await Scenario(scenario =>
        {
            scenario.Post.Json(new CreateWidget(NewName(), 3)).ToUrl("/widgets");
            scenario.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        }, new string('k', IdempotencyKeyHeader.MaxLength + 1));
    }

    [Test]
    public async Task A_response_whose_body_must_not_be_kept_replays_the_status_and_location_alone()
    {
        var widget = await Body<Widget>(CreateWidget(NewName()));
        var key = NewKey();

        var first = await MintToken(widget.Id, key);
        var replay = await MintToken(widget.Id, key);

        using (Assert.Multiple())
        {
            await Assert.That(await first.ReadAsTextAsync()).IsNotEmpty();
            await Assert.That(await replay.ReadAsTextAsync()).IsEmpty();
            await Assert.That(LocationOf(replay)).IsEqualTo(LocationOf(first));
            await Assert.That(WasReplayed(replay)).IsTrue();
        }
    }

    [Test]
    public async Task An_opted_out_endpoint_ignores_the_key_it_is_sent()
    {
        var key = NewKey();

        var first = await Preview(key);
        var second = await Preview(key);

        using (Assert.Multiple())
        {
            await Assert.That(WasReplayed(second)).IsFalse();
            await Assert.That(await second.ReadAsTextAsync()).IsEqualTo(await first.ReadAsTextAsync());
        }
    }

    private Task<IScenarioResult> CreateWidget(string name, string? key = null, string tenant = Tenant,
        string? attempt = null) =>
        Scenario(scenario =>
        {
            var post = scenario.Post.Json(new CreateWidget(name, 3)).ToUrl("/widgets");

            if (attempt is not null)
                post.QueryString(nameof(attempt), attempt);

            scenario.StatusCodeShouldBe(HttpStatusCode.Created);
        }, key, tenant);

    private Task<IScenarioResult> ArchiveWidget(Guid id, string key) =>
        Scenario(scenario =>
        {
            scenario.Post.Url($"/widgets/{id}/archive");
            scenario.StatusCodeShouldBe(HttpStatusCode.NoContent);
        }, key);

    private Task<IScenarioResult> MintToken(Guid id, string key) =>
        Scenario(scenario =>
        {
            scenario.Post.Url($"/widgets/{id}/tokens");
            scenario.StatusCodeShouldBe(HttpStatusCode.Created);
        }, key);

    private Task<IScenarioResult> Preview(string key) =>
        Scenario(scenario =>
        {
            scenario.Post.Json(new CreateWidget("preview", 3)).ToUrl("/widgets/preview");
            scenario.StatusCodeShouldBeOk();
        }, key);

    private Task<OrderPlacedResponse> PlaceOrder() =>
        Body<OrderPlacedResponse>(Scenario(scenario =>
        {
            scenario.Post.Json(new PlaceOrder(NewName(), 42m)).ToUrl("/orders");
            scenario.StatusCodeShouldBe(HttpStatusCode.Created);
        }));

    private Task<IScenarioResult> ApproveOrder(Guid id, string key) =>
        Scenario(scenario =>
        {
            scenario.Post.Url($"/orders/{id}/approve");
            scenario.StatusCodeShouldBeOk();
        }, key);

    private async Task<Order> LoadOrder(Guid id)
    {
        await using var session = Store.QuerySession();

        return (await session.LoadAsync<Order>(id))!;
    }

    private async Task<int> CountApprovals(Guid id)
    {
        await using var session = Store.QuerySession();

        var events = await session.Events.FetchStreamAsync(id);

        return events.Count(@event => @event.Data is OrderApproved);
    }

    private async Task<int> CountWidgets(string name)
    {
        await using var session = Store.QuerySession();

        return await session.Query<Widget>().CountAsync(widget => widget.Name == name);
    }

    private static string NewName() => $"widget-{Guid.CreateVersion7()}";

    private static string? LocationOf(IScenarioResult result) => result.Context.Response.Headers.Location.ToString();
}
