using System.Text.Json;

using SonarQube.Mcp.Http;
using SonarQube.Mcp.Http.Models;

using Xunit;

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

    [Fact]
    public void TheNestedShapeIsRead()
    {
        var envelope = new PagedEnvelopeDto { Paging = new PagingDto { PageIndex = 2, PageSize = 3, Total = 47 } };

        var page = PagedResult.From(envelope, TwoItems, requestedPage: 9, requestedPageSize: 9);

        Assert.Equal(2, page.Page);
        Assert.Equal(3, page.PageSize);
        Assert.Equal(47, page.Total);
        Assert.Equal(TwoItems, page.Items);
    }

    [Fact]
    public void TheFlatShapeIsRead()
    {
        var envelope = new PagedEnvelopeDto { P = 2, Ps = 3, Total = 47 };

        var page = PagedResult.From(envelope, TwoItems, requestedPage: 9, requestedPageSize: 9);

        Assert.Equal(2, page.Page);
        Assert.Equal(3, page.PageSize);
        Assert.Equal(47, page.Total);
    }

    [Fact]
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

        Assert.Equal(2, page.Page);
        Assert.Equal(3, page.PageSize);
        Assert.Equal(47, page.Total);
    }

    [Fact]
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

        Assert.Equal(nested, flat);
        Assert.Equal(flat, both);
    }

    [Fact]
    public void APartialNestedObjectFallsBackFieldByField()
    {
        // Resolved per field rather than by picking a shape wholesale, so a response that carries
        // half a paging object still contributes the half it has.
        var envelope = new PagedEnvelopeDto { P = 4, Ps = 8, Total = 99, Paging = new PagingDto { PageIndex = 2 } };

        var page = PagedResult.From(envelope, TwoItems, requestedPage: 1, requestedPageSize: 1);

        Assert.Equal(2, page.Page);
        Assert.Equal(8, page.PageSize);
        Assert.Equal(99, page.Total);
    }

    [Fact]
    public void AnEnvelopeWithNoPagingAtAllEchoesTheRequest()
    {
        var page = PagedResult.From(new PagedEnvelopeDto(), TwoItems, requestedPage: 3, requestedPageSize: 25);

        Assert.Equal(3, page.Page);
        Assert.Equal(25, page.PageSize);

        // Never guessed from the item count: a full page would then look like the last one, and
        // hasMore is computed from this.
        Assert.Null(page.Total);
    }

    [Fact]
    public void ANullEnvelopeEchoesTheRequestAndYieldsAnEmptyPage()
    {
        var page = PagedResult.From<string>(envelope: null, items: null, requestedPage: 3, requestedPageSize: 25);

        Assert.Empty(page.Items);
        Assert.Equal(3, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.Null(page.Total);
    }

    [Fact]
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
        Assert.NotNull(issues.Paging);
        Assert.NotNull(issues.Total);

        // rules/search: flat only.
        Assert.Null(rules.Paging);
        Assert.Equal(1, rules.Total);

        // components/tree: nested only.
        Assert.NotNull(tree.Paging);
        Assert.Null(tree.Total);

        Assert.Equal(1, PagedResult.From(issues, issues.Issues, 1, 2).Page);
        Assert.Equal(1, PagedResult.From(rules, rules.Rules, 1, 1).Page);
        Assert.Equal(1048, PagedResult.From(tree, tree.Components, 1, 3).Total);
    }
}
