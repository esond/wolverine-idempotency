using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Idempotency.Tests;

public class ResponseDescriberTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Describe_reads_status_body_and_location_from_a_created_result()
    {
        var description = Describe(TypedResults.Created("/widgets/1", new Widget(7)));

        using (Assert.Multiple())
        {
            await Assert.That(description!.StatusCode).IsEqualTo(StatusCodes.Status201Created);
            await Assert.That(description.Body).IsEqualTo("""{"size":7}""");
            await Assert.That(description.ContentType).IsEqualTo("application/json");
            await Assert.That(description.Location).IsEqualTo("/widgets/1");
        }
    }

    [Test]
    public async Task Describe_reads_status_and_body_from_an_ok_result()
    {
        var description = Describe(TypedResults.Ok(new Widget(7)));

        using (Assert.Multiple())
        {
            await Assert.That(description!.StatusCode).IsEqualTo(StatusCodes.Status200OK);
            await Assert.That(description.Body).IsEqualTo("""{"size":7}""");
            await Assert.That(description.Location).IsNull();
        }
    }

    [Test]
    public async Task Describe_leaves_a_no_content_result_without_a_body_or_content_type()
    {
        var description = Describe(TypedResults.NoContent());

        using (Assert.Multiple())
        {
            await Assert.That(description!.StatusCode).IsEqualTo(StatusCodes.Status204NoContent);
            await Assert.That(description.Body).IsNull();
            await Assert.That(description.ContentType).IsNull();
        }
    }

    [Test]
    public async Task Describe_reads_the_location_of_a_bodyless_accepted_result()
    {
        var description = Describe(TypedResults.Accepted("/jobs/1"));

        using (Assert.Multiple())
        {
            await Assert.That(description!.StatusCode).IsEqualTo(StatusCodes.Status202Accepted);
            await Assert.That(description.Body).IsNull();
            await Assert.That(description.Location).IsEqualTo("/jobs/1");
        }
    }

    [Test]
    public async Task Describe_unwraps_a_union_to_the_arm_it_actually_returned()
    {
        Results<Ok<Widget>, NotFound> union = TypedResults.Ok(new Widget(7));

        var description = Describe(union);

        using (Assert.Multiple())
        {
            await Assert.That(description!.StatusCode).IsEqualTo(StatusCodes.Status200OK);
            await Assert.That(description.Body).IsEqualTo("""{"size":7}""");
        }
    }

    [Test]
    public async Task Describe_declines_a_failed_arm_of_a_union()
    {
        Results<Ok<Widget>, NotFound> union = TypedResults.NotFound();

        await Assert.That(Describe(union)).IsNull();
    }

    [Test]
    public async Task Describe_declines_a_non_success_status()
    {
        await Assert.That(Describe(TypedResults.BadRequest("nope"))).IsNull();
    }

    [Test]
    public async Task Describe_reads_a_plain_object_as_a_200_with_a_body()
    {
        var description = Describe(new Widget(7));

        using (Assert.Multiple())
        {
            await Assert.That(description!.StatusCode).IsEqualTo(StatusCodes.Status200OK);
            await Assert.That(description.Body).IsEqualTo("""{"size":7}""");
            await Assert.That(description.ContentType).IsEqualTo("application/json");
        }
    }

    [Test]
    public async Task Describe_declines_a_null_plain_object()
    {
        // Wolverine's own writer turns this into the 404, so storing it here would remember a request that failed.
        await Assert.That(Describe(response: null)).IsNull();
    }

    private static CompletedResponse? Describe(object? response) =>
        ResponseDescriber.Describe(response, SerializerOptions);

    private sealed record Widget(int Size);
}
