using System.Text.Json;

using NUnit.Framework;

using SonarQube.Mcp.Http;
using SonarQube.Mcp.Http.Models;

namespace SonarQube.Mcp.Tests.Http;

/// <summary>
/// Covers <see cref="PagedResult"/>: SonarQube reports paging three different ways and every caller
/// has to read one.
/// </summary>
/// <remarks>
/// All three shapes are live-verified from the same API version, so this is not defensive
/// programming against a hypothetical — <c>components/tree</c> sends only the nested object,
/// <c>rules/search</c> sends only the flat trio, and <c>issues/search</c> sends both.
/// </remarks>
public class PagedResultTests
{
    private static readonly string[] TwoItems = ["a", "b"];

    [Test]
    public void TheNestedShapeIsRead()
    {
        var envelope = new PagedEnvelopeDto { Paging = new PagingDto { PageIndex = 2, PageSize = 3, Total = 47 } };

        var page = PagedResult.From(envelope, TwoItems, requestedPage: 9, requestedPageSize: 9);

        Assert.That(page.Page, Is.EqualTo(2));
        Assert.That(page.PageSize, Is.EqualTo(3));
        Assert.That(page.Total, Is.EqualTo(47));
        Assert.That(page.Items, Is.EqualTo(TwoItems));
    }

    [Test]
    public void TheFlatShapeIsRead()
    {
        var envelope = new PagedEnvelopeDto { P = 2, Ps = 3, Total = 47 };

        var page = PagedResult.From(envelope, TwoItems, requestedPage: 9, requestedPageSize: 9);

        Assert.That(page.Page, Is.EqualTo(2));
        Assert.That(page.PageSize, Is.EqualTo(3));
        Assert.That(page.Total, Is.EqualTo(47));
    }

    [Test]
    public void WhenBothShapesArePresentTheNestedOneWins()
    {
        // issues/search sends both. They agree in practice, but one of them has to be authoritative
        // or the answer depends on property ordering.
        var envelope = new PagedEnvelopeDto
        {
            P = 5,
            Ps = 6,
            Total = 7,
            Paging = new PagingDto { PageIndex = 2, PageSize = 3, Total = 47 },
        };

        var page = PagedResult.From(envelope, TwoItems, requestedPage: 9, requestedPageSize: 9);

        Assert.That(page.Page, Is.EqualTo(2));
        Assert.That(page.PageSize, Is.EqualTo(3));
        Assert.That(page.Total, Is.EqualTo(47));
    }

    [Test]
    public void AllThreeLiveShapesCollapseToTheSameResult()
    {
        var nested = PagedResult.From(
            new PagedEnvelopeDto { Paging = new PagingDto { PageIndex = 1, PageSize = 2, Total = 1606 } },
            TwoItems, 1, 2);

        var flat = PagedResult.From(
            new PagedEnvelopeDto { P = 1, Ps = 2, Total = 1606 },
            TwoItems, 1, 2);

        var both = PagedResult.From(
            new PagedEnvelopeDto { P = 1, Ps = 2, Total = 1606, Paging = new PagingDto { PageIndex = 1, PageSize = 2, Total = 1606 } },
            TwoItems, 1, 2);

        Assert.That(flat, Is.EqualTo(nested));
        Assert.That(both, Is.EqualTo(flat));
    }

    [Test]
    public void APartialNestedObjectFallsBackFieldByField()
    {
        // Resolved per field rather than by picking a shape wholesale, so a response that carries
        // half a paging object still contributes the half it has.
        var envelope = new PagedEnvelopeDto { P = 4, Ps = 8, Total = 99, Paging = new PagingDto { PageIndex = 2 } };

        var page = PagedResult.From(envelope, TwoItems, requestedPage: 1, requestedPageSize: 1);

        Assert.That(page.Page, Is.EqualTo(2));
        Assert.That(page.PageSize, Is.EqualTo(8));
        Assert.That(page.Total, Is.EqualTo(99));
    }

    [Test]
    public void AnEnvelopeWithNoPagingAtAllEchoesTheRequest()
    {
        var page = PagedResult.From(new PagedEnvelopeDto(), TwoItems, requestedPage: 3, requestedPageSize: 25);

        Assert.That(page.Page, Is.EqualTo(3));
        Assert.That(page.PageSize, Is.EqualTo(25));

        // Never guessed from the item count: a full page would then look like the last one, and
        // hasMore is computed from this.
        Assert.That(page.Total, Is.Null);
    }

    [Test]
    public void ANullEnvelopeEchoesTheRequestAndYieldsAnEmptyPage()
    {
        var page = PagedResult.From<string>(envelope: null, items: null, requestedPage: 3, requestedPageSize: 25);

        Assert.That(page.Items, Is.Empty);
        Assert.That(page.Page, Is.EqualTo(3));
        Assert.That(page.PageSize, Is.EqualTo(25));
        Assert.That(page.Total, Is.Null);
    }

    [Test]
    public void TheLivePagingShapesAreReadStraightOffTheFixtures()
    {
        // The claim under test is about the wire, so it is asserted against the wire.
        var issues = JsonSerializer.Deserialize(
            SonarFixtures.Read("issues-search-page.json"),
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;

        var rules = JsonSerializer.Deserialize(
            SonarFixtures.Read("rules-search-rule-key.json"),
            SonarWireJsonContext.Default.RulesSearchResponseDto)!;

        var tree = JsonSerializer.Deserialize(
            SonarFixtures.Read("components-tree-leaves.json"),
            SonarWireJsonContext.Default.ComponentsTreeResponseDto)!;

        // issues/search: both shapes.
        Assert.That(issues.Paging, Is.Not.Null);
        Assert.That(issues.Total, Is.Not.Null);

        // rules/search: flat only.
        Assert.That(rules.Paging, Is.Null);
        Assert.That(rules.Total, Is.EqualTo(1));

        // components/tree: nested only.
        Assert.That(tree.Paging, Is.Not.Null);
        Assert.That(tree.Total, Is.Null);

        Assert.That(PagedResult.From(issues, issues.Issues, 1, 2).Page, Is.EqualTo(1));
        Assert.That(PagedResult.From(rules, rules.Rules, 1, 1).Page, Is.EqualTo(1));
        Assert.That(PagedResult.From(tree, tree.Components, 1, 3).Total, Is.EqualTo(1048));
    }
}
