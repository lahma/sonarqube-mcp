using System.Net;
using System.Text;
using System.Text.Json;

using SonarQube.Mcp.Authentication;
using SonarQube.Mcp.Http;
using SonarQube.Mcp.Http.Models;

using Xunit;

namespace SonarQube.Mcp.Tests.Http;

/// <summary>
/// Covers <see cref="SonarApiClient"/> against a stub transport: the exact URL and body it puts on
/// the wire, the DTOs it reads back from the golden fixtures, and how it turns a failure into an
/// exception.
/// </summary>
/// <remarks>
/// <para>
/// URLs are asserted through <see cref="Uri.AbsolutePath"/> and the parsed query rather than
/// <see cref="Uri.ToString()"/>, which unescapes and would happily report a hostile component key
/// as if it had never been escaped.
/// </para>
/// <para>
/// The stub replaces the innermost handler, so every test here also runs the real
/// <c>AuthenticationHandler</c> and <c>RetryHandler</c>. That is deliberate: "the header is
/// attached" and "a 429 is retried with the body intact" are properties of the assembled chain, and
/// testing the handlers in isolation would prove neither.
/// </para>
/// </remarks>
public class SonarApiClientTests
{
    private const string Project = "quartznet_quartznet";
    private const string EmptyJson = "{}";

    /// <summary>A file component key: a colon, slashes, and nothing that needs escaping otherwise.</summary>
    private const string FileKey = "quartznet_quartznet:src/Quartz/Core/QuartzScheduler.cs";

    private static readonly string[] Ncloc = ["ncloc"];

    /// <summary>Page sizes the client refuses outright, and the reason each is out of range.</summary>
    public static TheoryData<int> RejectedPageSizes => new() { -1, 0, 501, 100_000 };

    /// <summary>
    /// Every embedded fixture, by file name. Driven off the assembly's resources rather than a
    /// hand-kept list, so a fixture added without a deserialisation mapping fails the suite.
    /// </summary>
    public static TheoryData<string> FixtureNames
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var (name, _) in SonarFixtures.AllJson())
            {
                data.Add(name);
            }

            return data;
        }
    }

    // ------------------------------------------------------------------ URLs: projects, components

    [Fact]
    public async Task SearchComponentsAsksForProjectsInOneOrganization()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.SearchComponentsAsync(
            "quartznet",
            query: "quartz",
            page: 2,
            pageSize: 25,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/components/search", RequestUrl.Path(request.Uri));
        Assert.Equal("quartznet", RequestUrl.QueryValue(request.Uri, "organization"));

        // Undocumented on this action but verified to work, and load-bearing: without it the search
        // also returns directories and files.
        Assert.Equal("TRK", RequestUrl.QueryValue(request.Uri, "qualifiers"));
        Assert.Equal("quartz", RequestUrl.QueryValue(request.Uri, "q"));
        Assert.Equal("2", RequestUrl.QueryValue(request.Uri, "p"));
        Assert.Equal("25", RequestUrl.QueryValue(request.Uri, "ps"));
    }

    [Fact]
    public async Task SearchComponentsOmitsEveryUnsetParameter()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.SearchComponentsAsync("quartznet", cancellationToken: TestContext.Current.CancellationToken);

        // "unset" and "set to empty" are different requests; only the two constants survive.
        Assert.Equal(["organization", "qualifiers"], RequestUrl.QueryNames(Assert.Single(stub.Requests).Uri));
    }

    [Fact]
    public async Task GetComponentsTreeSendsStrategyQualifiersAndScope()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.GetComponentsTreeAsync(
            Project,
            strategy: "leaves",
            qualifiers: ["FIL", "UTS"],
            query: "Scheduler",
            branch: "main",
            page: 1,
            pageSize: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/components/tree", RequestUrl.Path(request.Uri));
        Assert.Equal(Project, RequestUrl.QueryValue(request.Uri, "component"));
        Assert.Equal("leaves", RequestUrl.QueryValue(request.Uri, "strategy"));

        // Comma-joined into ONE parameter, not repeated: SonarQube keeps only one of a repeated pair.
        Assert.Equal("FIL,UTS", RequestUrl.QueryValue(request.Uri, "qualifiers"));
        Assert.Equal("Scheduler", RequestUrl.QueryValue(request.Uri, "q"));
        Assert.Equal("main", RequestUrl.QueryValue(request.Uri, "branch"));
    }

    [Fact]
    public async Task ListBranchesTargetsTheUnpaginatedEndpoint()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.ListBranchesAsync(Project, TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/project_branches/list", RequestUrl.Path(request.Uri));
        Assert.Equal(["project"], RequestUrl.QueryNames(request.Uri));
    }

    [Fact]
    public async Task ListPullRequestsTargetsTheUnpaginatedEndpoint()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.ListPullRequestsAsync(Project, TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/project_pull_requests/list", RequestUrl.Path(request.Uri));
        Assert.Equal(["project"], RequestUrl.QueryNames(request.Uri));
    }

    [Fact]
    public async Task GetQualityGateProjectStatusScopesToAPullRequest()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("qualitygates-project-status-error.json"));
        using var client = TestClient.Create(stub);

        var response = await client.GetQualityGateProjectStatusAsync(
            Project,
            pullRequest: "3267",
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/qualitygates/project_status", RequestUrl.Path(request.Uri));
        Assert.Equal(Project, RequestUrl.QueryValue(request.Uri, "projectKey"));
        Assert.Equal("3267", RequestUrl.QueryValue(request.Uri, "pullRequest"));
        Assert.Null(RequestUrl.QueryValue(request.Uri, "branch"));

        Assert.Equal("ERROR", response.ProjectStatus?.Status);
        Assert.Equal(5, response.ProjectStatus?.Conditions?.Count);
    }

    // ------------------------------------------------------------------ URLs: issues

    [Fact]
    public async Task SearchIssuesSendsTheFullFilterSetAsCommaJoinedLists()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.SearchIssuesAsync(
            componentKeys: [Project],
            issueStatuses: ["OPEN", "CONFIRMED"],
            impactSeverities: ["HIGH", "BLOCKER"],
            impactSoftwareQualities: ["RELIABILITY"],
            rules: ["csharpsquid:S2259"],
            tags: ["cwe", "design"],
            languages: ["cs"],
            assignees: ["ada@github"],
            createdAfter: "2026-01-01",
            inNewCodePeriod: true,
            sortBy: "CREATION_DATE",
            ascending: false,
            additionalFields: ["rules"],
            branch: "main",
            page: 1,
            pageSize: 50,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/issues/search", RequestUrl.Path(request.Uri));
        Assert.Equal(Project, RequestUrl.QueryValue(request.Uri, "componentKeys"));
        Assert.Equal("OPEN,CONFIRMED", RequestUrl.QueryValue(request.Uri, "issueStatuses"));
        Assert.Equal("HIGH,BLOCKER", RequestUrl.QueryValue(request.Uri, "impactSeverities"));
        Assert.Equal("RELIABILITY", RequestUrl.QueryValue(request.Uri, "impactSoftwareQualities"));
        Assert.Equal("csharpsquid:S2259", RequestUrl.QueryValue(request.Uri, "rules"));
        Assert.Equal("cwe,design", RequestUrl.QueryValue(request.Uri, "tags"));
        Assert.Equal("cs", RequestUrl.QueryValue(request.Uri, "languages"));
        Assert.Equal("ada@github", RequestUrl.QueryValue(request.Uri, "assignees"));
        Assert.Equal("2026-01-01", RequestUrl.QueryValue(request.Uri, "createdAfter"));
        Assert.Equal("CREATION_DATE", RequestUrl.QueryValue(request.Uri, "s"));
        Assert.Equal("false", RequestUrl.QueryValue(request.Uri, "asc"));
        Assert.Equal("rules", RequestUrl.QueryValue(request.Uri, "additionalFields"));

        // inNewCodePeriod is this server's name for it; the wire parameter is sinceLeakPeriod.
        Assert.Equal("true", RequestUrl.QueryValue(request.Uri, "sinceLeakPeriod"));
        Assert.Null(RequestUrl.QueryValue(request.Uri, "inNewCodePeriod"));
    }

    [Fact]
    public async Task SearchIssuesFetchesOneIssueByKey()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-search-single.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchIssuesAsync(
            issues: ["AZ_xePOumT_q4T_1FWf8"],
            additionalFields: ["transitions", "comments", "rules", "users"],
            pageSize: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("AZ_xePOumT_q4T_1FWf8", RequestUrl.QueryValue(request.Uri, "issues"));
        Assert.Equal("transitions,comments,rules,users", RequestUrl.QueryValue(request.Uri, "additionalFields"));

        var issue = Assert.Single(response.Issues!);
        Assert.Equal("AZ_xePOumT_q4T_1FWf8", issue.Key);
        Assert.Equal("OPEN", issue.IssueStatus);
        Assert.Equal("lahma@github", issue.Assignee);
    }

    [Fact]
    public async Task SearchIssuesRefusesAListValueContainingAComma()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        // A comma inside a value would be split by SonarQube into two filters the caller never
        // asked for — a silently wrong result rather than an error.
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.SearchIssuesAsync(
            tags: ["one,two"],
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("comma", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tags", exception.Message, StringComparison.Ordinal);
        Assert.Empty(stub.Requests);
    }

    // ------------------------------------------------------------------ URLs: hotspots

    [Fact]
    public async Task SearchHotspotsSendsTheUndocumentedBranchScope()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("hotspots-search-page.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchHotspotsAsync(
            Project,
            files: [FileKey],
            status: "TO_REVIEW",
            onlyMine: false,
            branch: "main",
            page: 1,
            pageSize: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/hotspots/search", RequestUrl.Path(request.Uri));
        Assert.Equal(Project, RequestUrl.QueryValue(request.Uri, "projectKey"));
        Assert.Equal(FileKey, RequestUrl.QueryValue(request.Uri, "files"));
        Assert.Equal("TO_REVIEW", RequestUrl.QueryValue(request.Uri, "status"));
        Assert.Equal("false", RequestUrl.QueryValue(request.Uri, "onlyMine"));
        Assert.Equal("main", RequestUrl.QueryValue(request.Uri, "branch"));

        // The search endpoint's assignee is a UUID, unlike the issue and hotspots/show forms.
        Assert.Equal("AYgE5F7pEoXHSow6lKjD", response.Hotspots![0].Assignee);
    }

    [Fact]
    public async Task ShowHotspotUsesTheHotspotParameterName()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("hotspots-show.json"));
        using var client = TestClient.Create(stub);

        var response = await client.ShowHotspotAsync("AZvu8ZyfNsnCVHe5poFs", TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/hotspots/show", RequestUrl.Path(request.Uri));

        // Not "hotspotKey", which is what the tool parameter is called.
        Assert.Equal(["hotspot"], RequestUrl.QueryNames(request.Uri));

        // The show shape: objects where the search returns strings, and a login where it returns a UUID.
        Assert.Equal("quartznet_quartznet", response.Project?.Key);
        Assert.Equal("appsettings.json", response.Component?.Name);
        Assert.Equal("lahma@github", response.Assignee);
        Assert.False(response.CanChangeStatus);
    }

    // ------------------------------------------------------------------ URLs: measures and metrics

    [Fact]
    public async Task GetComponentMeasuresRequestsPeriodsAndReadsTheOmissions()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("measures-component.json"));
        using var client = TestClient.Create(stub);

        var response = await client.GetComponentMeasuresAsync(
            Project,
            ["ncloc", "coverage", "new_coverage", "sqale_rating"],
            additionalFields: ["periods"],
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/measures/component", RequestUrl.Path(request.Uri));
        Assert.Equal("ncloc,coverage,new_coverage,sqale_rating", RequestUrl.QueryValue(request.Uri, "metricKeys"));

        // "periods", plural. The singular is a 400.
        Assert.Equal("periods", RequestUrl.QueryValue(request.Uri, "additionalFields"));

        // Four requested, two returned: a metric with no data is silently omitted, which is the
        // whole reason the tool layer has to compute missingMetrics.
        var measures = response.Component!.Measures!;
        Assert.Equal(2, measures.Count);
        Assert.DoesNotContain(measures, m => m.Metric == "new_coverage");
        Assert.Equal("1.0", measures.Single(m => m.Metric == "sqale_rating").Value);
    }

    [Fact]
    public async Task GetComponentMeasuresReadsNewCodeValuesOutOfPeriods()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("measures-component-periods.json"));
        using var client = TestClient.Create(stub);

        var response = await client.GetComponentMeasuresAsync(
            Project,
            ["new_violations"],
            additionalFields: ["periods"],
            pullRequest: "3267",
            cancellationToken: TestContext.Current.CancellationToken);

        var measure = Assert.Single(response.Component!.Measures!);

        // The new-code number lives in periods[0], and there is no `value` at all.
        Assert.Null(measure.Value);
        Assert.Equal("13", Assert.Single(measure.Periods!).Value);
        Assert.Equal(1, measure.Periods![0].Index);
    }

    [Fact]
    public async Task GetComponentTreeMeasuresComposesTheThreeMetricSortParametersTogether()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("measures-component-tree.json"));
        using var client = TestClient.Create(stub);

        _ = await client.GetComponentTreeMeasuresAsync(
            Project,
            ["ncloc", "complexity"],
            strategy: "leaves",
            qualifiers: ["FIL"],
            sortByMetric: "ncloc",
            ascending: false,
            page: 1,
            pageSize: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/measures/component_tree", RequestUrl.Path(request.Uri));
        Assert.Equal("metric", RequestUrl.QueryValue(request.Uri, "s"));
        Assert.Equal("ncloc", RequestUrl.QueryValue(request.Uri, "metricSort"));

        // Without this, components with no value for the metric sort to the top and the
        // "worst files" answer is a list of files with no data.
        Assert.Equal("withMeasuresOnly", RequestUrl.QueryValue(request.Uri, "metricSortFilter"));
        Assert.Equal("false", RequestUrl.QueryValue(request.Uri, "asc"));
    }

    [Fact]
    public async Task GetComponentTreeMeasuresOmitsTheSortParametersWhenNoMetricIsNamed()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.GetComponentTreeMeasuresAsync(
            Project,
            Ncloc,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Null(RequestUrl.QueryValue(request.Uri, "s"));
        Assert.Null(RequestUrl.QueryValue(request.Uri, "metricSort"));
        Assert.Null(RequestUrl.QueryValue(request.Uri, "metricSortFilter"));
    }

    [Fact]
    public async Task SearchMeasuresHistoryUsesTheMetricsParameterNotMetricKeys()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("measures-search-history.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchMeasuresHistoryAsync(
            Project,
            ["ncloc", "coverage"],
            from: "2026-01-01",
            page: 1,
            pageSize: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/measures/search_history", RequestUrl.Path(request.Uri));

        // This endpoint alone spells it "metrics".
        Assert.Equal("ncloc,coverage", RequestUrl.QueryValue(request.Uri, "metrics"));
        Assert.Null(RequestUrl.QueryValue(request.Uri, "metricKeys"));
        Assert.Equal("2026-01-01", RequestUrl.QueryValue(request.Uri, "from"));

        // An analysis where the metric had no data sends a date and no value; that gap must survive
        // as null rather than being read as a zero.
        var coverage = response.Measures!.Single(m => m.Metric == "coverage");
        Assert.All(coverage.History!, entry => Assert.Null(entry.Value));
        Assert.All(coverage.History!, entry => Assert.NotNull(entry.Date));
    }

    [Fact]
    public async Task SearchMetricsSendsNoFieldSelector()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("metrics-search.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchMetricsAsync(
            pageSize: 500,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/metrics/search", RequestUrl.Path(request.Uri));
        Assert.Equal(["ps"], RequestUrl.QueryNames(request.Uri));

        // `f` does not accept "type" — asking for it is a 400 — and omitting `f` returns every
        // field anyway, `type` included. This assertion is what stops a well-meaning edit from
        // reintroducing the field list.
        Assert.Null(RequestUrl.QueryValue(request.Uri, "f"));

        Assert.Equal(155, response.Total);
        Assert.Equal("INT", response.Metrics!.Single(m => m.Key == "accepted_issues").Type);
    }

    // ------------------------------------------------------------------ URLs: rules and sources

    [Fact]
    public async Task SearchRulesAsksForDescriptionSectionsByRuleKey()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("rules-search-rule-key.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchRulesAsync(
            "quartznet",
            "csharpsquid:S2259",
            TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        // rules/search, not rules/show: only this action has an `f` parameter on SonarQube Cloud.
        Assert.Equal("/api/rules/search", RequestUrl.Path(request.Uri));
        Assert.Equal("csharpsquid:S2259", RequestUrl.QueryValue(request.Uri, "rule_key"));
        Assert.Equal(SonarApiClient.RuleFields, RequestUrl.QueryValue(request.Uri, "f"));
        Assert.Contains("descriptionSections", SonarApiClient.RuleFields, StringComparison.Ordinal);

        // The degraded anonymous response: the request succeeds and the sections simply are not there.
        var rule = Assert.Single(response.Rules!);
        Assert.Null(rule.DescriptionSections);
        Assert.NotNull(rule.RequiredEntitlements);
        Assert.Equal("Null pointers should not be dereferenced", rule.Name);
    }

    [Fact]
    public async Task SearchRulesReadsSectionsWhenTheyArePresent()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("rules-search-with-sections.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchRulesAsync(
            "quartznet",
            "csharpsquid:S2259",
            TestContext.Current.CancellationToken);

        var rule = Assert.Single(response.Rules!);
        var sections = rule.DescriptionSections!;

        Assert.Equal(4, sections.Count);
        Assert.Equal("introduction", sections[0].Key);
        Assert.Equal("csharp", sections.Single(s => s.Key == "how_to_fix").Context?.Key);
    }

    [Fact]
    public async Task GetSourceLinesSendsTheComponentKeyAsKeyAndReadsTheCoverageGaps()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("sources-lines.json"));
        using var client = TestClient.Create(stub);

        var response = await client.GetSourceLinesAsync(
            FileKey,
            from: 940,
            to: 945,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/sources/lines", RequestUrl.Path(request.Uri));
        Assert.Equal(FileKey, RequestUrl.QueryValue(request.Uri, "key"));
        Assert.Equal("940", RequestUrl.QueryValue(request.Uri, "from"));
        Assert.Equal("945", RequestUrl.QueryValue(request.Uri, "to"));

        Assert.Equal(6, response.Sources!.Count);

        // Absent, not zero: this project publishes no coverage, and "not measured" and "measured
        // and uncovered" call for opposite actions.
        Assert.All(response.Sources!, line => Assert.Null(line.LineHits));

        // `code` is syntax-highlighted HTML, which is why the tool layer drops it.
        Assert.Contains("<span", response.Sources![3].Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAuthenticationTargetsTheProbeEndpoint()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"valid":true}""");
        using var client = TestClient.Create(stub);

        var response = await client.ValidateAuthenticationAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/authentication/validate", RequestUrl.Path(request.Uri));
        Assert.Empty(RequestUrl.Query(request.Uri));
        Assert.True(response.Valid);
    }

    // ------------------------------------------------------------------ escaping

    [Theory]
    [InlineData("proj:src/Widget.cs", "proj%3Asrc%2FWidget.cs")]
    [InlineData("proj:src/My Widget.cs", "proj%3Asrc%2FMy%20Widget.cs")]
    [InlineData("proj:src/Widget#1.cs", "proj%3Asrc%2FWidget%231.cs")]
    [InlineData("proj:a&b=c", "proj%3Aa%26b%3Dc")]
    public async Task ComponentKeysAreEscapedExactlyOnceOnTheWire(string key, string expectedEncoding)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.GetComponentsTreeAsync(key, cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        // The raw path-and-query: a single round of escaping, so "%" itself is never doubled.
        Assert.Equal($"/api/components/tree?component={expectedEncoding}", RequestUrl.PathAndQuery(request.Uri));

        // And it decodes back to exactly what was passed in.
        Assert.Equal(key, RequestUrl.QueryValue(request.Uri, "component"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("proj:../../etc/passwd")]
    [InlineData("proj:src/./Widget.cs")]
    public async Task ComponentKeysThatWouldCollapseAPathAreRefusedBeforeSending(string key)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetComponentsTreeAsync(
            key,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("component key", exception.Message, StringComparison.Ordinal);
        Assert.Empty(stub.Requests);
    }

    // ------------------------------------------------------------------ form bodies

    [Fact]
    public async Task DoIssueTransitionPostsAFormEncodedBody()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-do_transition.json"));
        using var client = TestClient.Create(stub);

        _ = await client.DoIssueTransitionAsync(
            "AZ_xePOumT_q4T_1FWf8",
            "accept",
            comment: "Intentional here — see PR 3268.",
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/issues/do_transition", RequestUrl.Path(request.Uri));

        // Form parameters, never query parameters: the URL carries nothing at all.
        Assert.Empty(RequestUrl.Query(request.Uri));
        Assert.Equal("application/x-www-form-urlencoded", request.Headers["Content-Type"]);

        // Byte-exact: spaces are "+", the em dash is percent-encoded UTF-8, and nothing is
        // double-escaped.
        const string expected =
            "issue=AZ_xePOumT_q4T_1FWf8&transition=accept&comment=Intentional+here+%E2%80%94+see+PR+3268.";

        Assert.Equal(expected, request.Body);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), request.BodyBytes);
    }

    [Fact]
    public async Task DoIssueTransitionOmitsAnAbsentComment()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-do_transition.json"));
        using var client = TestClient.Create(stub);

        _ = await client.DoIssueTransitionAsync(
            "AZ_xePOumT_q4T_1FWf8",
            "accept",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "issue=AZ_xePOumT_q4T_1FWf8&transition=accept",
            Assert.Single(stub.Requests).Body);
    }

    [Fact]
    public async Task AssignIssueSendsAnEmptyAssigneeToUnassign()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-assign.json"));
        using var client = TestClient.Create(stub);

        _ = await client.AssignIssueAsync("AZ_xePOumT_q4T_1FWf8", cancellationToken: TestContext.Current.CancellationToken);

        // "assignee=" with nothing after it is what unassigns; omitting the parameter would leave
        // the current assignee in place, which is the opposite of what a null argument means here.
        Assert.Equal("issue=AZ_xePOumT_q4T_1FWf8&assignee=", Assert.Single(stub.Requests).Body);
    }

    [Fact]
    public async Task AssignIssueSendsTheLoginVerbatim()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-assign.json"));
        using var client = TestClient.Create(stub);

        var response = await client.AssignIssueAsync(
            "AZ_xePOumT_q4T_1FWf8",
            "lahma@github",
            TestContext.Current.CancellationToken);

        // "@" is not in RFC 3986's unreserved set, so it is percent-encoded — a login is not a
        // pre-escaped value.
        Assert.Equal("issue=AZ_xePOumT_q4T_1FWf8&assignee=lahma%40github", Assert.Single(stub.Requests).Body);
        Assert.Equal("lahma@github", response.Issue?.Assignee);
    }

    [Fact]
    public async Task AddIssueCommentPostsTheTextAsAFormParameter()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-add_comment.json"));
        using var client = TestClient.Create(stub);

        var response = await client.AddIssueCommentAsync(
            "AZ_xePOumT_q4T_1FWf8",
            "Fixed by the naming pass.",
            TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/api/issues/add_comment", RequestUrl.Path(request.Uri));
        Assert.Equal("issue=AZ_xePOumT_q4T_1FWf8&text=Fixed+by+the+naming+pass.", request.Body);

        Assert.Equal(2, response.Issue?.Comments?.Count);
    }

    [Fact]
    public async Task ChangeHotspotStatusAcceptsA204WithNoBody()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueNoBody(HttpStatusCode.NoContent);
        using var client = TestClient.Create(stub);

        // No return value and no exception: there is nothing to deserialise, and trying to would be
        // the bug this test exists to prevent.
        await client.ChangeHotspotStatusAsync(
            "AZvu8ZyfNsnCVHe5poFs",
            "REVIEWED",
            resolution: "SAFE",
            comment: "Config sample, not a credential.",
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/hotspots/change_status", RequestUrl.Path(request.Uri));
        Assert.Equal(
            "hotspot=AZvu8ZyfNsnCVHe5poFs&status=REVIEWED&resolution=SAFE&comment=Config+sample%2C+not+a+credential.",
            request.Body);
    }

    [Fact]
    public async Task AFormBodyIsResentUnchangedAfterA429()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        stub.Enqueue(HttpStatusCode.TooManyRequests);
        stub.EnqueueJson(SonarFixtures.Read("issues-add_comment.json"));

        using var client = TestClient.Create(stub, time);

        _ = await client.AddIssueCommentAsync(
            "AZ_xePOumT_q4T_1FWf8",
            "retry me",
            TestContext.Current.CancellationToken);

        // Two requests, byte-identical bodies. This is what ByteArrayContent buys and what a
        // stream-backed body would have lost.
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal("issue=AZ_xePOumT_q4T_1FWf8&text=retry+me", stub.Requests[0].Body);
        Assert.Equal(stub.Requests[0].BodyBytes, stub.Requests[1].BodyBytes);
        Assert.Equal("application/x-www-form-urlencoded", stub.Requests[1].Headers["Content-Type"]);
    }

    // ------------------------------------------------------------------ headers

    [Fact]
    public async Task EveryRequestCarriesTheBearerTokenAndTheProductUserAgent()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.ListBranchesAsync(Project, TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("Bearer test-token", request.Headers["Authorization"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.Contains("sonarqube-mcp/", request.Headers["User-Agent"], StringComparison.Ordinal);
        Assert.Contains("github.com/lahma/sonarqube-mcp", request.Headers["User-Agent"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestWithNoTokenConfiguredGoesOutAnonymously()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("components-search.json"));
        using var client = TestClient.CreateAnonymous(stub);

        // Not an error: public projects are readable without a credential, and every golden fixture
        // in this suite was captured exactly this way.
        var response = await client.SearchComponentsAsync(
            "quartznet",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(stub.Requests).Headers.ContainsKey("Authorization"));
        Assert.Equal("quartznet_quartznet", Assert.Single(response.Components!).Key);
    }

    // ------------------------------------------------------------------ failures

    [Fact]
    public async Task AnEmptyBodied401WithATokenProducesAUsableMessageAndNoNullDereference()
    {
        using var stub = new StubHttpMessageHandler();

        // A bad token's 401 has Content-Length: 0 and no Content-Type. Verified live.
        stub.EnqueueNoBody(HttpStatusCode.Unauthorized);

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Null(exception.Error);
        Assert.Equal(string.Empty, exception.RawBody);
        Assert.Null(SonarApiException.Detail(exception));

        // The message is still a complete sentence naming the status, with nothing appended from
        // the envelope that is not there.
        Assert.Equal("SonarQube Cloud API returned HTTP 401 (Unauthorized).", exception.Message);
    }

    [Fact]
    public async Task A401WithNoTokenConfiguredBecomesTheSetYourTokenInstruction()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("error-401-authentication-required.json"), HttpStatusCode.Unauthorized);

        using var client = TestClient.CreateAnonymous(stub);

        // The same status means two different things. With no token it is not "your credential was
        // rejected", it is "you never configured one", and only one of those is actionable.
        var exception = await Assert.ThrowsAsync<AuthenticationRequiredException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        Assert.Contains("SONARQUBE_TOKEN", exception.Message, StringComparison.Ordinal);
        Assert.Contains("https://sonarcloud.io/account/security", exception.Message, StringComparison.Ordinal);
        Assert.Contains("User Token", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SONARQUBE_ORG", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sonarqube-mcp status", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A401WithATokenStaysAnApiExceptionSoTheFunnelCanTellThemApart()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("error-401-authentication-required.json"), HttpStatusCode.Unauthorized);

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        Assert.Equal("Authentication is required", SonarApiException.Detail(exception));
    }

    [Fact]
    public async Task A400QuotesEveryMessageSonarQubeSent()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("error-400-page-size.json"), HttpStatusCode.BadRequest);

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);

        // Quoted verbatim, because SonarQube's own 400 text names the parameter, the value and the rule.
        Assert.Equal("'ps' value (501) must be less than 500", SonarApiException.Detail(exception));
        Assert.Contains("'ps' value (501)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailJoinsSeveralMessagesWithASemicolon()
    {
        var exception = new SonarApiException(
            HttpStatusCode.BadRequest,
            new ErrorEnvelopeDto
            {
                Errors =
                [
                    new ErrorDto { Msg = "first thing" },
                    new ErrorDto { Msg = "  second thing  " },
                    new ErrorDto { Msg = "   " },
                ],
            },
            rawBody: null,
            retryAttempts: 0);

        // A 400 routinely carries more than one message; reading only the first drops half the
        // reason the request was rejected.
        Assert.Equal("first thing; second thing", SonarApiException.Detail(exception));
    }

    [Fact]
    public async Task A404KeepsSonarQubesOwnComponentNotFoundText()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("error-404-component-not-found.json"), HttpStatusCode.NotFound);

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.GetComponentMeasuresAsync(
            "quartznet_quartznet:no/such/File.cs",
            Ncloc,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Contains("not found", SonarApiException.Detail(exception)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonJsonErrorBodyIsKeptRawRatherThanLost()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>", "text/html");
        stub.Enqueue(HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>", "text/html");
        stub.Enqueue(HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>", "text/html");
        stub.Enqueue(HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>", "text/html");

        using var client = TestClient.Create(stub, new ManualTimeProvider());

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        Assert.Null(exception.Error);
        Assert.Contains("502 Bad Gateway", exception.RawBody, StringComparison.Ordinal);
        Assert.Equal(3, exception.RetryAttempts);
        Assert.Contains("after 3 retries", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirectBecomesAnErrorNamingTheLocation()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.Found, "https://example.invalid/api/x"));

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Found, exception.StatusCode);
        Assert.Contains("https://example.invalid/api/x", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SONARQUBE_URL", exception.Message, StringComparison.Ordinal);

        // Exactly one request: the redirect is refused, never followed, so the credential cannot be
        // stripped and re-sent to whatever host the response named.
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ASuccessStatusWithAnUnparsableBodyIsReportedAsSuch()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("{ this is not json");

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Contains("could not parse", exception.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task ASuccessStatusWithAnEmptyBodyIsReportedAsSuch()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("null");

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        Assert.Contains("empty body", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ retries

    [Fact]
    public async Task RetriesFollowAnExponentialScheduleAndSpendNoRealTime()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        stub.Enqueue(HttpStatusCode.ServiceUnavailable);
        stub.Enqueue(HttpStatusCode.ServiceUnavailable);
        stub.Enqueue(HttpStatusCode.ServiceUnavailable);
        stub.EnqueueJson(EmptyJson);

        using var client = TestClient.Create(stub, time);

        _ = await client.ListBranchesAsync(Project, TestContext.Current.CancellationToken);

        Assert.Equal(4, stub.Requests.Count);
        Assert.Equal(3, time.Delays.Count);

        // 500 ms, 1 s, 2 s, each with ±25 % jitter so a burst of parallel calls does not come back
        // in lockstep.
        AssertDelayNear(TimeSpan.FromMilliseconds(500), time.Delays[0]);
        AssertDelayNear(TimeSpan.FromMilliseconds(1000), time.Delays[1]);
        AssertDelayNear(TimeSpan.FromMilliseconds(2000), time.Delays[2]);
    }

    [Fact]
    public async Task TheRetryBudgetIsFourAttemptsInTotal()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        for (var i = 0; i < 5; i++)
        {
            stub.Enqueue(HttpStatusCode.GatewayTimeout);
        }

        using var client = TestClient.Create(stub, time);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        Assert.Equal(RetryHandler.MaxAttempts, stub.Requests.Count);
        Assert.Equal(RetryHandler.MaxAttempts - 1, exception.RetryAttempts);
    }

    [Fact]
    public async Task A404IsNotRetried()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        stub.EnqueueJson(SonarFixtures.Read("error-404-component-not-found.json"), HttpStatusCode.NotFound);

        using var client = TestClient.Create(stub, time);

        _ = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        // A 404 is a deterministic answer; reissuing it only spends budget against a rate limit
        // nobody can see.
        Assert.Single(stub.Requests);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task ARetryAfterHeaderOverridesTheBackoffSchedule()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        stub.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", "5");
            return response;
        });
        stub.EnqueueJson(EmptyJson);

        using var client = TestClient.Create(stub, time);

        _ = await client.ListBranchesAsync(Project, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(time.Delays));
    }

    [Fact]
    public async Task AnHttpDateRetryAfterIsMeasuredAgainstTheInjectedClock()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        time.SetUtcNow(now);

        stub.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.Add("Retry-After", now.AddSeconds(30).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            return response;
        });
        stub.EnqueueJson(EmptyJson);

        using var client = TestClient.Create(stub, time);

        _ = await client.ListBranchesAsync(Project, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(time.Delays));
    }

    [Fact]
    public async Task ARetryAfterLongerThanAMinuteEndsTheAttemptRatherThanBlocking()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        stub.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", "120");
            return response;
        });

        using var client = TestClient.Create(stub, time);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.Current.CancellationToken));

        // Nothing sits on a two-minute wait inside a tool call; the caller is told the number instead.
        Assert.Single(stub.Requests);
        Assert.Empty(time.Delays);
        Assert.Equal(120, exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task ATransportFailureIsRetried()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        stub.Enqueue(_ => throw new HttpRequestException("connection reset"));
        stub.EnqueueJson(EmptyJson);

        using var client = TestClient.Create(stub, time);

        _ = await client.ListBranchesAsync(Project, TestContext.Current.CancellationToken);

        Assert.Equal(2, stub.Requests.Count);
        Assert.Single(time.Delays);
    }

    // ------------------------------------------------------------------ client-side guards

    [Theory]
    [MemberData(nameof(RejectedPageSizes))]
    public async Task PageSizesOutsideSonarQubesRangeAreRefusedBeforeSending(int pageSize)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchIssuesAsync(
            pageSize: pageSize,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("pageSize", exception.ParamName);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task APageBelowOneIsRefused()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchIssuesAsync(
            page: 0,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("page", exception.ParamName);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData(101, 100)]
    [InlineData(21, 500)]
    [InlineData(10_001, 2)]
    public async Task PagingPastTheTenThousandResultCapIsRefusedWithoutARequest(int page, int pageSize)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchIssuesAsync(
            page: page,
            pageSize: pageSize,
            cancellationToken: TestContext.Current.CancellationToken));

        // Turning the API's terse "10100th result asked" into an instruction is the whole point of
        // enforcing this client-side as well.
        Assert.Contains("narrow the search", exception.Message, StringComparison.Ordinal);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(1, 500)]
    [InlineData(20, 500)]
    public async Task PagingUpToTheCapIsAllowed(int page, int pageSize)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.SearchIssuesAsync(
            page: page,
            pageSize: pageSize,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ComponentTreeMeasuresRefusesMoreThanFifteenMetricKeys()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var metrics = Enumerable.Range(0, 16).Select(i => $"metric_{i}").ToArray();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetComponentTreeMeasuresAsync(
            Project,
            metrics,
            cancellationToken: TestContext.Current.CancellationToken));

        // 15 is the API's own maxValuesAllowed on this action; exceeding it is a 400 whose text
        // reads like a value error rather than a count one.
        Assert.Contains("15", exception.Message, StringComparison.Ordinal);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task ComponentMeasuresAllowsSixteenMetricKeys()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        var metrics = Enumerable.Range(0, 16).Select(i => $"metric_{i}").ToArray();

        // The cap differs per endpoint: measures/component has no maxValuesAllowed at all, so the
        // tighter tree limit must not leak across.
        _ = await client.GetComponentMeasuresAsync(
            Project,
            metrics,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task AnEmptyMetricKeyListIsRefused()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetComponentMeasuresAsync(
            Project,
            [],
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("listMetrics", exception.Message, StringComparison.Ordinal);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankIssueKeyIsRefusedBeforeAWriteIsSent(string issueKey)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => client.AddIssueCommentAsync(
            issueKey,
            "text",
            TestContext.Current.CancellationToken));

        Assert.Empty(stub.Requests);
    }

    // ------------------------------------------------------------------ fixtures

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void EveryEmbeddedFixtureDeserialisesThroughTheSourceGeneratedContext(string fixture)
    {
        var json = SonarFixtures.Read(fixture);

        // Reflection-based serialization is off in the csproj, so anything reached here that is
        // missing from SonarWireJsonContext throws rather than quietly falling back.
        object? value = fixture switch
        {
            "issues-search-page.json" or "issues-search-empty.json" or "issues-search-single.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.IssuesSearchResponseDto),
            "issues-do_transition.json" or "issues-assign.json" or "issues-add_comment.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.IssueOperationResponseDto),
            "hotspots-search-page.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.HotspotsSearchResponseDto),
            "hotspots-show.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.HotspotShowResponseDto),
            "measures-component.json" or "measures-component-periods.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.MeasuresComponentResponseDto),
            "measures-component-tree.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.MeasuresComponentTreeResponseDto),
            "measures-search-history.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.MeasuresHistoryResponseDto),
            "metrics-search.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.MetricsSearchResponseDto),
            "qualitygates-project-status-none.json" or "qualitygates-project-status-error.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.ProjectStatusResponseDto),
            "components-search.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.ComponentsSearchResponseDto),
            "components-tree-leaves.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.ComponentsTreeResponseDto),
            "project-branches-list.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.BranchesListResponseDto),
            "project-pull-requests-list.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.PullRequestsListResponseDto),
            "rules-search-rule-key.json" or "rules-search-with-sections.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.RulesSearchResponseDto),
            "sources-lines.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.SourcesLinesResponseDto),
            var name when name.StartsWith("error-", StringComparison.Ordinal) =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.ErrorEnvelopeDto),

            // Not a fallthrough: a fixture added without a mapping fails here rather than being
            // silently excluded from the only test that proves it parses.
            _ => throw new InvalidOperationException(
                $"Fixture '{fixture}' has no deserialisation mapping. Add one, and record the capture in MANIFEST.md."),
        };

        Assert.NotNull(value);
    }

    [Fact]
    public void EveryFixtureListedInTheDesignIsPresent()
    {
        var names = SonarFixtures.AllJson().Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        string[] required =
        [
            "issues-search-page.json", "issues-search-empty.json", "issues-search-single.json",
            "hotspots-search-page.json", "hotspots-show.json",
            "measures-component.json", "measures-component-periods.json", "measures-component-tree.json",
            "measures-search-history.json", "metrics-search.json",
            "qualitygates-project-status-none.json", "qualitygates-project-status-error.json",
            "components-search.json", "components-tree-leaves.json",
            "project-branches-list.json", "project-pull-requests-list.json",
            "sources-lines.json", "rules-search-rule-key.json", "rules-search-with-sections.json",
            "issues-do_transition.json", "issues-assign.json", "issues-add_comment.json",
            "error-400-page-size.json", "error-400-result-cap.json",
            "error-401-authentication-required.json", "error-404-component-not-found.json",
        ];

        Assert.All(required, name => Assert.Contains(name, names));
    }

    [Fact]
    public void TheEmptyBodied401IsDocumentedEvenThoughItHasNoJsonToStore()
    {
        // The one response in the golden set with nothing to serialise. The note file is how the
        // difference between the two kinds of 401 stays written down.
        var note = SonarFixtures.Read("error-401-empty-body.txt");

        Assert.Contains("Content-Length: 0", note, StringComparison.Ordinal);
        Assert.Contains("error-401-authentication-required.json", note, StringComparison.Ordinal);
    }

    [Fact]
    public void TimestampsWithSonarQubesBasicFormatOffsetSurviveDeserialisation()
    {
        var response = JsonSerializer.Deserialize(
            SonarFixtures.Read("issues-search-page.json"),
            SonarWireJsonContext.Default.IssuesSearchResponseDto);

        // "+0000" — no colon — is what System.Text.Json's built-in reader rejects outright.
        var creation = response!.Issues![0].CreationDate;

        Assert.Equal(new DateTimeOffset(2026, 8, 11, 15, 35, 10, TimeSpan.Zero), creation);
    }

    [Fact]
    public void TheHotspotShowCommentsArrayIsReadFromItsSingularWireName()
    {
        var response = JsonSerializer.Deserialize(
            SonarFixtures.Read("hotspots-show.json"),
            SonarWireJsonContext.Default.HotspotShowResponseDto);

        // Spelled "comment" on this endpoint alone. The obvious plural silently deserialises to null.
        Assert.NotNull(response!.Comment);
        Assert.Empty(response.Comment);
        Assert.NotNull(response.Changelog);
    }

    private static void AssertDelayNear(TimeSpan expected, TimeSpan actual)
    {
        // ±25 % jitter, asserted as a band rather than a value: the jitter is the point, so pinning
        // an exact delay would either need a seeded Random or be flaky.
        Assert.InRange(actual, expected * 0.75, expected * 1.25);
    }
}
