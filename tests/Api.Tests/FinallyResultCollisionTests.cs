using System.Net;
using Alba;
using Idempotency.Sample;

namespace Idempotency.Tests;

/// <summary>
/// Pins the open question the <c>Finally</c> hook reintroduced: an endpoint returning a bare
/// <see cref="Microsoft.AspNetCore.Http.IResult" /> generates a chain that does not compile.
/// </summary>
/// <remarks>
/// This asserts a defect, so it goes red when Wolverine fixes one. That is the point — it is the artifact's
/// evidence that the shape is still broken, and the signal to delete both it and
/// <see cref="WidgetEndpoints.Rename" /> once it stops being true.
///
/// Nothing fails at startup. Code generation is per chain and lazy, so the host boots, every other endpoint works,
/// and only a request that reaches this one discovers that its chain never compiled. Under
/// <c>codegen write</c> the same chain fails the build outright, with <c>CS0841</c> and <c>CS0136</c> on the two
/// <c>result</c> locals.
/// </remarks>
public class FinallyResultCollisionTests(TestHost fixture) : IntegrationTestBase(fixture)
{
    [Test]
    public async Task An_endpoint_returning_a_bare_IResult_cannot_generate_a_chain_that_compiles()
    {
        var widget = await Body<Widget>(CreateWidget());

        await Scenario(scenario =>
        {
            scenario.Post.Json(new RenameWidget("renamed")).ToUrl($"/widgets/{widget.Id}/name");
            scenario.StatusCodeShouldBe(HttpStatusCode.InternalServerError);
        }, NewKey());
    }

    private Task<IScenarioResult> CreateWidget() =>
        Scenario(scenario =>
        {
            scenario.Post.Json(new CreateWidget($"collision-{Guid.CreateVersion7()}", 3)).ToUrl("/widgets");
            scenario.StatusCodeShouldBe(HttpStatusCode.Created);
        });
}
