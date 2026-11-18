using System.Net;
using System.Text;
using System.Text.Json;

using NUnit.Framework;

using SonarQube.Mcp.Authentication;
using SonarQube.Mcp.Http;
using SonarQube.Mcp.Http.Models;

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
    public static IEnumerable<int> RejectedPageSizes => [-1, 0, 501, 100_000];

    /// <summary>
    /// Every embedded fixture, by file name. Driven off the assembly's resources rather than a
    /// hand-kept list, so a fixture added without a deserialisation mapping fails the suite.
    /// </summary>
    public static IEnumerable<string> FixtureNames
    {
        get
        {
            var data = new List<string>();

            foreach (var (name, _) in SonarFixtures.AllJson())
            {
                data.Add(name);
            }

            return data;
        }
    }

    // ------------------------------------------------------------------ URLs: projects, components

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/components/search"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "organization"), Is.EqualTo("quartznet"));

        // Undocumented on this action but verified to work, and load-bearing: without it the search
        // also returns directories and files.
        Assert.That(RequestUrl.QueryValue(request.Uri, "qualifiers"), Is.EqualTo("TRK"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "q"), Is.EqualTo("quartz"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "p"), Is.EqualTo("2"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "ps"), Is.EqualTo("25"));
    }

    [Test]
    public async Task SearchComponentsOmitsEveryUnsetParameter()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.SearchComponentsAsync("quartznet", cancellationToken: TestContext.CurrentContext.CancellationToken);

        // "unset" and "set to empty" are different requests; only the two constants survive.
        Assert.That(stub.Requests, Has.Exactly(1).Items);
        Assert.That(RequestUrl.QueryNames(stub.Requests.Single().Uri), Is.EqualTo<string[]>(["organization", "qualifiers"]));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/components/tree"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "component"), Is.EqualTo(Project));
        Assert.That(RequestUrl.QueryValue(request.Uri, "strategy"), Is.EqualTo("leaves"));

        // Comma-joined into ONE parameter, not repeated: SonarQube keeps only one of a repeated pair.
        Assert.That(RequestUrl.QueryValue(request.Uri, "qualifiers"), Is.EqualTo("FIL,UTS"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "q"), Is.EqualTo("Scheduler"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "branch"), Is.EqualTo("main"));
    }

    [Test]
    public async Task ListBranchesTargetsTheUnpaginatedEndpoint()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.ListBranchesAsync(Project, TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/project_branches/list"));
        Assert.That(RequestUrl.QueryNames(request.Uri), Is.EqualTo<string[]>(["project"]));
    }

    [Test]
    public async Task ListPullRequestsTargetsTheUnpaginatedEndpoint()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.ListPullRequestsAsync(Project, TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/project_pull_requests/list"));
        Assert.That(RequestUrl.QueryNames(request.Uri), Is.EqualTo<string[]>(["project"]));
    }

    /// <summary>
    /// C14: the gate comes from the two gate measures, with its conditions parsed out of the JSON
    /// inside quality_gate_details, rather than from the deprecated qualitygates/project_status.
    /// </summary>
    [Test]
    public async Task GetQualityGateReadsTheTwoGateMeasuresForAPullRequest()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("measures-component-quality-gate-pr.json"));
        using var client = TestClient.Create(stub);

        var gate = await client.GetQualityGateAsync(
            Project,
            pullRequest: "4358",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/measures/component"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "component"), Is.EqualTo(Project));
        Assert.That(RequestUrl.QueryValue(request.Uri, "metricKeys"), Is.EqualTo("alert_status,quality_gate_details"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "pullRequest"), Is.EqualTo("4358"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "branch"), Is.Null);

        Assert.That(gate.Level, Is.EqualTo("OK"));
        Assert.That(gate.IgnoredConditions, Is.False);
        Assert.That(gate.Conditions?.Count, Is.EqualTo(4));
        Assert.That(gate.Conditions![3].Metric, Is.EqualTo("new_security_hotspots_reviewed"));
        Assert.That(gate.Conditions[3].Op, Is.EqualTo("LT"));
        Assert.That(gate.Conditions[3].Period, Is.EqualTo(1));
        Assert.That(gate.Conditions[3].Error, Is.EqualTo("100"));
        Assert.That(gate.Conditions[3].Actual, Is.EqualTo("100.0"));
        Assert.That(gate.Conditions[3].Level, Is.EqualTo("OK"));
    }

    [Test]
    public async Task AnUngatedScopeHasNoGateMeasuresAndReadsAsAnEmptyGate()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("measures-component-quality-gate-none.json"));
        using var client = TestClient.Create(stub);

        var gate = await client.GetQualityGateAsync(
            Project, cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(gate.Level, Is.Null);
        Assert.That(gate.Conditions, Is.Null);
    }

    [Test]
    public async Task AnIssueOfAPullRequestCarriesItsOwnScope()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-search-by-key-pullrequest.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchIssuesAsync(
            componentKeys: [Project],
            issues: ["AaCDWeEWg4L35vfD5PUF"],
            pullRequest: "3735",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(response.Issues![0].PullRequest, Is.EqualTo("3735"));
        Assert.That(response.Issues[0].Branch, Is.Null);
    }

    // ------------------------------------------------------------------ URLs: issues

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/issues/search"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "componentKeys"), Is.EqualTo(Project));
        Assert.That(RequestUrl.QueryValue(request.Uri, "issueStatuses"), Is.EqualTo("OPEN,CONFIRMED"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "impactSeverities"), Is.EqualTo("HIGH,BLOCKER"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "impactSoftwareQualities"), Is.EqualTo("RELIABILITY"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "rules"), Is.EqualTo("csharpsquid:S2259"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "tags"), Is.EqualTo("cwe,design"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "languages"), Is.EqualTo("cs"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "assignees"), Is.EqualTo("ada@github"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "createdAfter"), Is.EqualTo("2026-01-01"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "s"), Is.EqualTo("CREATION_DATE"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "asc"), Is.EqualTo("false"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "additionalFields"), Is.EqualTo("rules"));

        // inNewCodePeriod is this server's name for it; the wire parameter is sinceLeakPeriod.
        Assert.That(RequestUrl.QueryValue(request.Uri, "sinceLeakPeriod"), Is.EqualTo("true"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "inNewCodePeriod"), Is.Null);
    }

    [Test]
    public async Task SearchIssuesFetchesOneIssueByKey()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-search-single.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchIssuesAsync(
            issues: ["AZ_xePOumT_q4T_1FWf8"],
            additionalFields: ["transitions", "comments", "rules", "users"],
            pageSize: 1,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.QueryValue(request.Uri, "issues"), Is.EqualTo("AZ_xePOumT_q4T_1FWf8"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "additionalFields"), Is.EqualTo("transitions,comments,rules,users"));

        Assert.That(response.Issues!, Has.Exactly(1).Items);
        var issue = response.Issues!.Single();
        Assert.That(issue.Key, Is.EqualTo("AZ_xePOumT_q4T_1FWf8"));
        Assert.That(issue.IssueStatus, Is.EqualTo("OPEN"));
        Assert.That(issue.Assignee, Is.EqualTo("lahma@github"));
    }

    [Test]
    public async Task SearchIssuesRefusesAListValueContainingAComma()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        // A comma inside a value would be split by SonarQube into two filters the caller never
        // asked for — a silently wrong result rather than an error.
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.SearchIssuesAsync(
            tags: ["one,two"],
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("comma").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("tags").Using(StringComparison.Ordinal));
        Assert.That(stub.Requests, Is.Empty);
    }

    // ------------------------------------------------------------------ URLs: hotspots

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/hotspots/search"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "projectKey"), Is.EqualTo(Project));
        Assert.That(RequestUrl.QueryValue(request.Uri, "files"), Is.EqualTo(FileKey));
        Assert.That(RequestUrl.QueryValue(request.Uri, "status"), Is.EqualTo("TO_REVIEW"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "onlyMine"), Is.EqualTo("false"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "branch"), Is.EqualTo("main"));

        // The search endpoint's assignee is a UUID, unlike the issue and hotspots/show forms.
        Assert.That(response.Hotspots![0].Assignee, Is.EqualTo("AYgE5F7pEoXHSow6lKjD"));
    }

    [Test]
    public async Task ShowHotspotUsesTheHotspotParameterName()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("hotspots-show.json"));
        using var client = TestClient.Create(stub);

        var response = await client.ShowHotspotAsync("AZvu8ZyfNsnCVHe5poFs", TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/hotspots/show"));

        // Not "hotspotKey", which is what the tool parameter is called.
        Assert.That(RequestUrl.QueryNames(request.Uri), Is.EqualTo<string[]>(["hotspot"]));

        // The show shape: objects where the search returns strings, and a login where it returns a UUID.
        Assert.That(response.Project?.Key, Is.EqualTo("quartznet_quartznet"));
        Assert.That(response.Component?.Name, Is.EqualTo("appsettings.json"));
        Assert.That(response.Assignee, Is.EqualTo("lahma@github"));
        Assert.That(response.CanChangeStatus, Is.False);
    }

    // ------------------------------------------------------------------ URLs: measures and metrics

    [Test]
    public async Task GetComponentMeasuresRequestsPeriodsAndReadsTheOmissions()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("measures-component.json"));
        using var client = TestClient.Create(stub);

        var response = await client.GetComponentMeasuresAsync(
            Project,
            ["ncloc", "coverage", "new_coverage", "sqale_rating"],
            additionalFields: ["periods"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/measures/component"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "metricKeys"), Is.EqualTo("ncloc,coverage,new_coverage,sqale_rating"));

        // "periods", plural. The singular is a 400.
        Assert.That(RequestUrl.QueryValue(request.Uri, "additionalFields"), Is.EqualTo("periods"));

        // Four requested, two returned: a metric with no data is silently omitted, which is the
        // whole reason the tool layer has to compute missingMetrics.
        var measures = response.Component!.Measures!;
        Assert.That(measures.Count, Is.EqualTo(2));
        Assert.That(measures, Has.None.Matches<MeasureDto>(m => m!.Metric == "new_coverage"));
        Assert.That(measures.Single(m => m.Metric == "sqale_rating").Value, Is.EqualTo("1.0"));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(response.Component!.Measures!, Has.Exactly(1).Items);
        var measure = response.Component!.Measures!.Single();

        // The new-code number lives in periods[0], and there is no `value` at all.
        Assert.That(measure.Value, Is.Null);
        Assert.That(measure.Periods!, Has.Exactly(1).Items);
        Assert.That(measure.Periods!.Single().Value, Is.EqualTo("13"));
        Assert.That(measure.Periods![0].Index, Is.EqualTo(1));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/measures/component_tree"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "s"), Is.EqualTo("metric"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "metricSort"), Is.EqualTo("ncloc"));

        // Without this, components with no value for the metric sort to the top and the
        // "worst files" answer is a list of files with no data.
        Assert.That(RequestUrl.QueryValue(request.Uri, "metricSortFilter"), Is.EqualTo("withMeasuresOnly"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "asc"), Is.EqualTo("false"));
    }

    [Test]
    public async Task GetComponentTreeMeasuresOmitsTheSortParametersWhenNoMetricIsNamed()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.GetComponentTreeMeasuresAsync(
            Project,
            Ncloc,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.QueryValue(request.Uri, "s"), Is.Null);
        Assert.That(RequestUrl.QueryValue(request.Uri, "metricSort"), Is.Null);
        Assert.That(RequestUrl.QueryValue(request.Uri, "metricSortFilter"), Is.Null);
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/measures/search_history"));

        // This endpoint alone spells it "metrics".
        Assert.That(RequestUrl.QueryValue(request.Uri, "metrics"), Is.EqualTo("ncloc,coverage"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "metricKeys"), Is.Null);
        Assert.That(RequestUrl.QueryValue(request.Uri, "from"), Is.EqualTo("2026-01-01"));

        // An analysis where the metric had no data sends a date and no value; that gap must survive
        // as null rather than being read as a zero.
        var coverage = response.Measures!.Single(m => m.Metric == "coverage");

        foreach (var entry in coverage.History!)
        {
            Assert.That(entry.Value, Is.Null);
            Assert.That(entry.Date, Is.Not.Null);
        }
    }

    [Test]
    public async Task SearchMetricsSendsNoFieldSelector()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("metrics-search.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchMetricsAsync(
            pageSize: 500,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/metrics/search"));
        Assert.That(RequestUrl.QueryNames(request.Uri), Is.EqualTo<string[]>(["ps"]));

        // `f` does not accept "type" — asking for it is a 400 — and omitting `f` returns every
        // field anyway, `type` included. This assertion is what stops a well-meaning edit from
        // reintroducing the field list.
        Assert.That(RequestUrl.QueryValue(request.Uri, "f"), Is.Null);

        Assert.That(response.Total, Is.EqualTo(155));
        Assert.That(response.Metrics!.Single(m => m.Key == "accepted_issues").Type, Is.EqualTo("INT"));
    }

    // ------------------------------------------------------------------ URLs: rules and sources

    [Test]
    public async Task SearchRulesAsksForDescriptionSectionsByRuleKey()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("rules-search-rule-key.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchRulesAsync(
            "quartznet",
            "csharpsquid:S2259",
            TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        // rules/search, not rules/show: only this action has an `f` parameter on SonarQube Cloud.
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/rules/search"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "rule_key"), Is.EqualTo("csharpsquid:S2259"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "f"), Is.EqualTo(SonarApiClient.RuleFields));
        Assert.That(SonarApiClient.RuleFields, Contains.Substring("descriptionSections").Using(StringComparison.Ordinal));

        // The degraded anonymous response: the request succeeds and the sections simply are not there.
        Assert.That(response.Rules!, Has.Exactly(1).Items);
        var rule = response.Rules!.Single();
        Assert.That(rule.DescriptionSections, Is.Null);
        Assert.That(rule.RequiredEntitlements, Is.Not.Null);
        Assert.That(rule.Name, Is.EqualTo("Null pointers should not be dereferenced"));
    }

    /// <summary>
    /// The same request as the test above, captured with a user token instead of anonymously: the
    /// sections are there. That pair is the whole evidence for the entitlement story — one request,
    /// two credentials, two answers.
    /// </summary>
    [Test]
    public async Task SearchRulesReadsSectionsWhenTheyArePresent()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("rules-search-with-sections.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchRulesAsync(
            "quartznet",
            "csharpsquid:S2259",
            TestContext.CurrentContext.CancellationToken);

        Assert.That(response.Rules!, Has.Exactly(1).Items);
        var rule = response.Rules!.Single();
        var sections = rule.DescriptionSections!;

        // The wire order is the API's, not the tool's: this rule answers how_to_fix first and has no
        // introduction at all, which is why the tool orders by what the caller asked for.
        Assert.That(sections.Select(section => section.Key), Is.EqualTo<string?[]>(["how_to_fix", "root_cause", "resources"]));

        foreach (var section in sections)
        {
            Assert.That(section.Context, Is.Null);
        }
    }

    /// <summary>
    /// A rule whose fix guidance is per-framework: <c>how_to_fix</c> appears once per context, and
    /// the context is the only thing telling the two apart. Nothing about that shape is visible on a
    /// single-context rule, so it needs a capture of its own.
    /// </summary>
    [Test]
    public async Task SearchRulesReadsThePerContextSectionsOfAMultiFrameworkRule()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("rules-search-with-contexts.json"));
        using var client = TestClient.Create(stub);

        var response = await client.SearchRulesAsync(
            "quartznet",
            "javasecurity:S2076",
            TestContext.CurrentContext.CancellationToken);

        Assert.That(response.Rules!, Has.Exactly(1).Items);
        var rule = response.Rules!.Single();
        var sections = rule.DescriptionSections!;

        var contexts = sections
            .Where(section => string.Equals(section.Key, "how_to_fix", StringComparison.Ordinal))
            .Select(section => section.Context)
            .ToArray();

        Assert.That(contexts.Length, Is.EqualTo(2));
        Assert.That(contexts.Select(context => context?.Key), Is.EqualTo<string?[]>(["java_lang_package", "apache_commons"]));
        Assert.That(contexts.Select(context => context?.DisplayName), Is.EqualTo<string?[]>(["Java Lang Package", "Apache Commons"]));

        // The sections that are not per-framework carry no context at all.
        Assert.That(sections.Single(section => section.Key == "root_cause").Context, Is.Null);
    }

    [Test]
    public async Task GetSourceLinesSendsTheComponentKeyAsKeyAndReadsTheCoverageGaps()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("sources-lines.json"));
        using var client = TestClient.Create(stub);

        var response = await client.GetSourceLinesAsync(
            FileKey,
            from: 940,
            to: 945,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/sources/lines"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "key"), Is.EqualTo(FileKey));
        Assert.That(RequestUrl.QueryValue(request.Uri, "from"), Is.EqualTo("940"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "to"), Is.EqualTo("945"));

        Assert.That(response.Sources!.Count, Is.EqualTo(6));

        // Absent, not zero: this project publishes no coverage, and "not measured" and "measured
        // and uncovered" call for opposite actions.
        foreach (var line in response.Sources!)
        {
            Assert.That(line.LineHits, Is.Null);
        }

        // `code` is syntax-highlighted HTML, which is why the tool layer drops it.
        Assert.That(response.Sources![3].Code, Contains.Substring("<span").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task ValidateAuthenticationTargetsTheProbeEndpoint()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"valid":true}""");
        using var client = TestClient.Create(stub);

        var response = await client.ValidateAuthenticationAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/authentication/validate"));
        Assert.That(RequestUrl.Query(request.Uri), Is.Empty);
        Assert.That(response.Valid, Is.True);
    }

    // ------------------------------------------------------------------ escaping

    [TestCase("proj:src/Widget.cs", "proj%3Asrc%2FWidget.cs")]
    [TestCase("proj:src/My Widget.cs", "proj%3Asrc%2FMy%20Widget.cs")]
    [TestCase("proj:src/Widget#1.cs", "proj%3Asrc%2FWidget%231.cs")]
    [TestCase("proj:a&b=c", "proj%3Aa%26b%3Dc")]
    public async Task ComponentKeysAreEscapedExactlyOnceOnTheWire(string key, string expectedEncoding)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.GetComponentsTreeAsync(key, cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        // The raw path-and-query: a single round of escaping, so "%" itself is never doubled.
        Assert.That(RequestUrl.PathAndQuery(request.Uri), Is.EqualTo($"/api/components/tree?component={expectedEncoding}"));

        // And it decodes back to exactly what was passed in.
        Assert.That(RequestUrl.QueryValue(request.Uri, "component"), Is.EqualTo(key));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("..")]
    [TestCase(".")]
    [TestCase("proj:../../etc/passwd")]
    [TestCase("proj:src/./Widget.cs")]
    public async Task ComponentKeysThatWouldCollapseAPathAreRefusedBeforeSending(string key)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetComponentsTreeAsync(
            key,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("component key").Using(StringComparison.Ordinal));
        Assert.That(stub.Requests, Is.Empty);
    }

    // ------------------------------------------------------------------ form bodies

    [Test]
    public async Task DoIssueTransitionPostsAFormEncodedBody()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-do_transition.json"));
        using var client = TestClient.Create(stub);

        _ = await client.DoIssueTransitionAsync(
            "AZ_xePOumT_q4T_1FWf8",
            "accept",
            comment: "Intentional here — see PR 3268.",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/issues/do_transition"));

        // Form parameters, never query parameters: the URL carries nothing at all.
        Assert.That(RequestUrl.Query(request.Uri), Is.Empty);
        Assert.That(request.Headers["Content-Type"], Is.EqualTo("application/x-www-form-urlencoded"));

        // Byte-exact: spaces are "+", the em dash is percent-encoded UTF-8, and nothing is
        // double-escaped.
        const string expected =
            "issue=AZ_xePOumT_q4T_1FWf8&transition=accept&comment=Intentional+here+%E2%80%94+see+PR+3268.";

        Assert.That(request.Body, Is.EqualTo(expected));
        Assert.That(request.BodyBytes, Is.EqualTo(Encoding.UTF8.GetBytes(expected)));
    }

    [Test]
    public async Task DoIssueTransitionOmitsAnAbsentComment()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-do_transition.json"));
        using var client = TestClient.Create(stub);

        _ = await client.DoIssueTransitionAsync(
            "AZ_xePOumT_q4T_1FWf8",
            "accept",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        Assert.That(
            stub.Requests.Single().Body,
            Is.EqualTo("issue=AZ_xePOumT_q4T_1FWf8&transition=accept"));
    }

    [Test]
    public async Task AssignIssueSendsAnEmptyAssigneeToUnassign()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-assign.json"));
        using var client = TestClient.Create(stub);

        _ = await client.AssignIssueAsync("AZ_xePOumT_q4T_1FWf8", cancellationToken: TestContext.CurrentContext.CancellationToken);

        // "assignee=" with nothing after it is what unassigns; omitting the parameter would leave
        // the current assignee in place, which is the opposite of what a null argument means here.
        Assert.That(stub.Requests, Has.Exactly(1).Items);
        Assert.That(stub.Requests.Single().Body, Is.EqualTo("issue=AZ_xePOumT_q4T_1FWf8&assignee="));
    }

    [Test]
    public async Task AssignIssueSendsTheLoginVerbatim()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-assign.json"));
        using var client = TestClient.Create(stub);

        var response = await client.AssignIssueAsync(
            "AZ_xePOumT_q4T_1FWf8",
            "lahma@github",
            TestContext.CurrentContext.CancellationToken);

        // "@" is not in RFC 3986's unreserved set, so it is percent-encoded — a login is not a
        // pre-escaped value.
        Assert.That(stub.Requests, Has.Exactly(1).Items);
        Assert.That(stub.Requests.Single().Body, Is.EqualTo("issue=AZ_xePOumT_q4T_1FWf8&assignee=lahma%40github"));
        Assert.That(response.Issue?.Assignee, Is.EqualTo("lahma@github"));
    }

    [Test]
    public async Task AddIssueCommentPostsTheTextAsAFormParameter()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("issues-add_comment.json"));
        using var client = TestClient.Create(stub);

        var response = await client.AddIssueCommentAsync(
            "AZ_xePOumT_q4T_1FWf8",
            "Fixed by the naming pass.",
            TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/issues/add_comment"));
        Assert.That(request.Body, Is.EqualTo("issue=AZ_xePOumT_q4T_1FWf8&text=Fixed+by+the+naming+pass."));

        Assert.That(response.Issue?.Comments?.Count, Is.EqualTo(2));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/hotspots/change_status"));
        Assert.That(
            request.Body,
            Is.EqualTo("hotspot=AZvu8ZyfNsnCVHe5poFs&status=REVIEWED&resolution=SAFE&comment=Config+sample%2C+not+a+credential."));
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        // Two requests, byte-identical bodies. This is what ByteArrayContent buys and what a
        // stream-backed body would have lost.
        Assert.That(stub.Requests.Count, Is.EqualTo(2));
        Assert.That(stub.Requests[0].Body, Is.EqualTo("issue=AZ_xePOumT_q4T_1FWf8&text=retry+me"));
        Assert.That(stub.Requests[1].BodyBytes, Is.EqualTo(stub.Requests[0].BodyBytes));
        Assert.That(stub.Requests[1].Headers["Content-Type"], Is.EqualTo("application/x-www-form-urlencoded"));
    }

    // ------------------------------------------------------------------ headers

    [Test]
    public async Task EveryRequestCarriesTheBearerTokenAndTheProductUserAgent()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.ListBranchesAsync(Project, TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        var request = stub.Requests.Single();

        Assert.That(request.Headers["Authorization"], Is.EqualTo("Bearer test-token"));
        Assert.That(request.Headers["Accept"], Is.EqualTo("application/json"));
        Assert.That(request.Headers["User-Agent"], Contains.Substring("sonarqube-mcp/").Using(StringComparison.Ordinal));
        Assert.That(request.Headers["User-Agent"], Contains.Substring("github.com/lahma/sonarqube-mcp").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task ARequestWithNoTokenConfiguredGoesOutAnonymously()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("components-search.json"));
        using var client = TestClient.CreateAnonymous(stub);

        // Not an error: public projects are readable without a credential, and every golden fixture
        // in this suite was captured exactly this way.
        var response = await client.SearchComponentsAsync(
            "quartznet",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        Assert.That(stub.Requests.Single().Headers.ContainsKey("Authorization"), Is.False);
        Assert.That(response.Components!, Has.Exactly(1).Items);
        Assert.That(response.Components!.Single().Key, Is.EqualTo("quartznet_quartznet"));
    }

    // ------------------------------------------------------------------ failures

    [Test]
    public async Task AnEmptyBodied401WithATokenProducesAUsableMessageAndNoNullDereference()
    {
        using var stub = new StubHttpMessageHandler();

        // A bad token's 401 has Content-Length: 0 and no Content-Type. Verified live.
        stub.EnqueueNoBody(HttpStatusCode.Unauthorized);

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(exception.Error, Is.Null);
        Assert.That(exception.RawBody, Is.EqualTo(string.Empty));
        Assert.That(SonarApiException.Detail(exception), Is.Null);

        // The message is still a complete sentence naming the status, with nothing appended from
        // the envelope that is not there.
        Assert.That(exception.Message, Is.EqualTo("SonarQube Cloud API returned HTTP 401 (Unauthorized)."));
    }

    [Test]
    public async Task A401WithNoTokenConfiguredBecomesTheSetYourTokenInstruction()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("error-401-authentication-required.json"), HttpStatusCode.Unauthorized);

        using var client = TestClient.CreateAnonymous(stub);

        // The same status means two different things. With no token it is not "your credential was
        // rejected", it is "you never configured one", and only one of those is actionable.
        var exception = await Assert.ThrowsAsync<AuthenticationRequiredException>(() => client.ListBranchesAsync(
            Project,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("SONARQUBE_TOKEN").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("https://sonarcloud.io/account/security").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("User Token").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("SONARQUBE_ORG").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("sonarqube-mcp status").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task A401WithATokenStaysAnApiExceptionSoTheFunnelCanTellThemApart()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("error-401-authentication-required.json"), HttpStatusCode.Unauthorized);

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(SonarApiException.Detail(exception), Is.EqualTo("Authentication is required"));
    }

    [Test]
    public async Task A400QuotesEveryMessageSonarQubeSent()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("error-400-page-size.json"), HttpStatusCode.BadRequest);

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Quoted verbatim, because SonarQube's own 400 text names the parameter, the value and the rule.
        Assert.That(SonarApiException.Detail(exception), Is.EqualTo("'ps' value (501) must be less than 500"));
        Assert.That(exception.Message, Contains.Substring("'ps' value (501)").Using(StringComparison.Ordinal));
    }

    [Test]
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
        Assert.That(SonarApiException.Detail(exception), Is.EqualTo("first thing; second thing"));
    }

    [Test]
    public async Task A404KeepsSonarQubesOwnComponentNotFoundText()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(SonarFixtures.Read("error-404-component-not-found.json"), HttpStatusCode.NotFound);

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.GetComponentMeasuresAsync(
            "quartznet_quartznet:no/such/File.cs",
            Ncloc,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(SonarApiException.Detail(exception)!, Contains.Substring("not found").Using(StringComparison.Ordinal));
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Error, Is.Null);
        Assert.That(exception.RawBody, Contains.Substring("502 Bad Gateway").Using(StringComparison.Ordinal));
        Assert.That(exception.RetryAttempts, Is.EqualTo(3));
        Assert.That(exception.Message, Contains.Substring("after 3 retries").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task ARedirectBecomesAnErrorNamingTheLocation()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.Found, "https://example.invalid/api/x"));

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.Found));
        Assert.That(exception.Message, Contains.Substring("https://example.invalid/api/x").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("SONARQUBE_URL").Using(StringComparison.Ordinal));

        // Exactly one request: the redirect is refused, never followed, so the credential cannot be
        // stripped and re-sent to whatever host the response named.
        Assert.That(stub.Requests, Has.Exactly(1).Items);
    }

    [Test]
    public async Task ASuccessStatusWithAnUnparsableBodyIsReportedAsSuch()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("{ this is not json");

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(exception.Message, Contains.Substring("could not parse").Using(StringComparison.Ordinal));
        Assert.That(exception.InnerException, Is.TypeOf<JsonException>());
    }

    [Test]
    public async Task ASuccessStatusWithAnEmptyBodyIsReportedAsSuch()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("null");

        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("empty body").Using(StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ retries

    [Test]
    public async Task RetriesFollowAnExponentialScheduleAndSpendNoRealTime()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        stub.Enqueue(HttpStatusCode.ServiceUnavailable);
        stub.Enqueue(HttpStatusCode.ServiceUnavailable);
        stub.Enqueue(HttpStatusCode.ServiceUnavailable);
        stub.EnqueueJson(EmptyJson);

        using var client = TestClient.Create(stub, time);

        _ = await client.ListBranchesAsync(Project, TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests.Count, Is.EqualTo(4));
        Assert.That(time.Delays.Count, Is.EqualTo(3));

        // 500 ms, 1 s, 2 s, each with ±25 % jitter so a burst of parallel calls does not come back
        // in lockstep.
        AssertDelayNear(TimeSpan.FromMilliseconds(500), time.Delays[0]);
        AssertDelayNear(TimeSpan.FromMilliseconds(1000), time.Delays[1]);
        AssertDelayNear(TimeSpan.FromMilliseconds(2000), time.Delays[2]);
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken));

        Assert.That(stub.Requests.Count, Is.EqualTo(RetryHandler.MaxAttempts));
        Assert.That(exception!.RetryAttempts, Is.EqualTo(RetryHandler.MaxAttempts - 1));
    }

    [Test]
    public async Task A404IsNotRetried()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        stub.EnqueueJson(SonarFixtures.Read("error-404-component-not-found.json"), HttpStatusCode.NotFound);

        using var client = TestClient.Create(stub, time);

        _ = await Assert.ThrowsAsync<SonarApiException>(() => client.ListBranchesAsync(
            Project,
            TestContext.CurrentContext.CancellationToken));

        // A 404 is a deterministic answer; reissuing it only spends budget against a rate limit
        // nobody can see.
        Assert.That(stub.Requests, Has.Exactly(1).Items);
        Assert.That(time.Delays, Is.Empty);
    }

    [Test]
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

        _ = await client.ListBranchesAsync(Project, TestContext.CurrentContext.CancellationToken);

        Assert.That(time.Delays, Has.Exactly(1).Items);
        Assert.That(time.Delays.Single(), Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    [Test]
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

        _ = await client.ListBranchesAsync(Project, TestContext.CurrentContext.CancellationToken);

        Assert.That(time.Delays, Has.Exactly(1).Items);
        Assert.That(time.Delays.Single(), Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken));

        // Nothing sits on a two-minute wait inside a tool call; the caller is told the number instead.
        Assert.That(stub.Requests, Has.Exactly(1).Items);
        Assert.That(time.Delays, Is.Empty);
        Assert.That(exception!.RetryAfterSeconds, Is.EqualTo(120));
    }

    [Test]
    public async Task ATransportFailureIsRetried()
    {
        using var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        stub.Enqueue(_ => throw new HttpRequestException("connection reset"));
        stub.EnqueueJson(EmptyJson);

        using var client = TestClient.Create(stub, time);

        _ = await client.ListBranchesAsync(Project, TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests.Count, Is.EqualTo(2));
        Assert.That(time.Delays, Has.Exactly(1).Items);
    }

    // ------------------------------------------------------------------ client-side guards

    [TestCaseSource(nameof(RejectedPageSizes))]
    public async Task PageSizesOutsideSonarQubesRangeAreRefusedBeforeSending(int pageSize)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchIssuesAsync(
            pageSize: pageSize,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.ParamName, Is.EqualTo("pageSize"));
        Assert.That(stub.Requests, Is.Empty);
    }

    [Test]
    public async Task APageBelowOneIsRefused()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchIssuesAsync(
            page: 0,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.ParamName, Is.EqualTo("page"));
        Assert.That(stub.Requests, Is.Empty);
    }

    [TestCase(101, 100)]
    [TestCase(21, 500)]
    [TestCase(10_001, 2)]
    public async Task PagingPastTheTenThousandResultCapIsRefusedWithoutARequest(int page, int pageSize)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchIssuesAsync(
            page: page,
            pageSize: pageSize,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        // Turning the API's terse "10100th result asked" into an instruction is the whole point of
        // enforcing this client-side as well.
        Assert.That(exception!.Message, Contains.Substring("narrow the search").Using(StringComparison.Ordinal));
        Assert.That(stub.Requests, Is.Empty);
    }

    [TestCase(100, 100)]
    [TestCase(1, 500)]
    [TestCase(20, 500)]
    public async Task PagingUpToTheCapIsAllowed(int page, int pageSize)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyJson);
        using var client = TestClient.Create(stub);

        _ = await client.SearchIssuesAsync(
            page: page,
            pageSize: pageSize,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
    }

    [Test]
    public async Task ComponentTreeMeasuresRefusesMoreThanFifteenMetricKeys()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var metrics = Enumerable.Range(0, 16).Select(i => $"metric_{i}").ToArray();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetComponentTreeMeasuresAsync(
            Project,
            metrics,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        // 15 is the API's own maxValuesAllowed on this action; exceeding it is a 400 whose text
        // reads like a value error rather than a count one.
        Assert.That(exception!.Message, Contains.Substring("15").Using(StringComparison.Ordinal));
        Assert.That(stub.Requests, Is.Empty);
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
    }

    [Test]
    public async Task AnEmptyMetricKeyListIsRefused()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetComponentMeasuresAsync(
            Project,
            [],
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("listMetrics").Using(StringComparison.Ordinal));
        Assert.That(stub.Requests, Is.Empty);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task ABlankIssueKeyIsRefusedBeforeAWriteIsSent(string issueKey)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = TestClient.Create(stub);

        _ = await Assert.CatchAsync<ArgumentException>(() => client.AddIssueCommentAsync(
            issueKey,
            "text",
            TestContext.CurrentContext.CancellationToken));

        Assert.That(stub.Requests, Is.Empty);
    }

    // ------------------------------------------------------------------ fixtures

    [TestCaseSource(nameof(FixtureNames))]
    public void EveryEmbeddedFixtureDeserialisesThroughTheSourceGeneratedContext(string fixture)
    {
        var json = SonarFixtures.Read(fixture);

        // Reflection-based serialization is off in the csproj, so anything reached here that is
        // missing from SonarWireJsonContext throws rather than quietly falling back.
        object? value = fixture switch
        {
            "issues-search-page.json" or "issues-search-empty.json" or "issues-search-single.json"
                or "issues-search-facets.json" or "issues-search-facets-pullrequest.json"
                or "issues-search-by-key-pullrequest.json" or "issues-search-rule-context.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.IssuesSearchResponseDto),
            "issues-changelog.json" or "issues-changelog-anonymous.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.IssueChangelogResponseDto),
            "issues-bulk-change.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.BulkChangeResponseDto),
            "ce-component.json" or "ce-component-failed.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.CeComponentResponseDto),
            "issues-do_transition.json" or "issues-assign.json" or "issues-add_comment.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.IssueOperationResponseDto),
            "hotspots-search-page.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.HotspotsSearchResponseDto),
            "hotspots-show.json" or "hotspots-show-with-comment.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.HotspotShowResponseDto),
            "measures-component.json" or "measures-component-periods.json"
                or "measures-component-quality-gate-pr.json" or "measures-component-quality-gate-none.json"
                or "measures-component-quality-gate-error.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.MeasuresComponentResponseDto),
            "measures-component-tree.json" or "measures-component-tree-coverage-sorted.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.MeasuresComponentTreeResponseDto),
            "measures-search-history.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.MeasuresHistoryResponseDto),
            "metrics-search.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.MetricsSearchResponseDto),
            "components-search.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.ComponentsSearchResponseDto),
            "components-tree-leaves.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.ComponentsTreeResponseDto),
            "project-branches-list.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.BranchesListResponseDto),
            "project-pull-requests-list.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.PullRequestsListResponseDto),
            "rules-search-rule-key.json" or "rules-search-with-sections.json"
                or "rules-search-with-contexts.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.RulesSearchResponseDto),
            "sources-lines.json" or "sources-lines-with-coverage.json" or "sources-lines-fully-covered.json" =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.SourcesLinesResponseDto),
            var name when name.StartsWith("error-", StringComparison.Ordinal) =>
                JsonSerializer.Deserialize(json, SonarWireJsonContext.Default.ErrorEnvelopeDto),

            // Not a fallthrough: a fixture added without a mapping fails here rather than being
            // silently excluded from the only test that proves it parses.
            _ => throw new InvalidOperationException(
                $"Fixture '{fixture}' has no deserialisation mapping. Add one, and record the capture in MANIFEST.md."),
        };

        Assert.That(value, Is.Not.Null);
    }

    [Test]
    public void EveryFixtureListedInTheDesignIsPresent()
    {
        var names = SonarFixtures.AllJson().Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        string[] required =
        [
            "issues-search-page.json", "issues-search-empty.json", "issues-search-single.json",
            "hotspots-search-page.json", "hotspots-show.json", "hotspots-show-with-comment.json",
            "measures-component.json", "measures-component-periods.json", "measures-component-tree.json",
            "measures-search-history.json", "metrics-search.json",
            "measures-component-quality-gate-pr.json", "measures-component-quality-gate-none.json",
            "measures-component-quality-gate-error.json", "issues-search-rule-context.json",
            "components-search.json", "components-tree-leaves.json",
            "project-branches-list.json", "project-pull-requests-list.json",
            "sources-lines.json", "rules-search-rule-key.json", "rules-search-with-sections.json",
            "rules-search-with-contexts.json",
            "sources-lines-with-coverage.json", "sources-lines-fully-covered.json",
            "measures-component-tree-coverage-sorted.json",
            "issues-do_transition.json", "issues-assign.json", "issues-add_comment.json",
            "error-400-page-size.json", "error-400-result-cap.json",
            "error-401-authentication-required.json", "error-404-component-not-found.json",
        ];

        foreach (var name in required)
        {
            Assert.That(names, Does.Contain(name));
        }
    }

    [Test]
    public void TheEmptyBodied401IsDocumentedEvenThoughItHasNoJsonToStore()
    {
        // The one response in the golden set with nothing to serialise. The note file is how the
        // difference between the two kinds of 401 stays written down.
        var note = SonarFixtures.Read("error-401-empty-body.txt");

        Assert.That(note, Contains.Substring("Content-Length: 0").Using(StringComparison.Ordinal));
        Assert.That(note, Contains.Substring("error-401-authentication-required.json").Using(StringComparison.Ordinal));
    }

    [Test]
    public void TimestampsWithSonarQubesBasicFormatOffsetSurviveDeserialisation()
    {
        var response = JsonSerializer.Deserialize(
            SonarFixtures.Read("issues-search-page.json"),
            SonarWireJsonContext.Default.IssuesSearchResponseDto);

        // "+0000" — no colon — is what System.Text.Json's built-in reader rejects outright.
        var creation = response!.Issues![0].CreationDate;

        Assert.That(creation, Is.EqualTo(new DateTimeOffset(2026, 8, 11, 15, 35, 10, TimeSpan.Zero)));
    }

    [Test]
    public void TheHotspotShowCommentsArrayIsReadFromItsSingularWireName()
    {
        var response = JsonSerializer.Deserialize(
            SonarFixtures.Read("hotspots-show.json"),
            SonarWireJsonContext.Default.HotspotShowResponseDto);

        // Spelled "comment" on this endpoint alone. The obvious plural silently deserialises to null.
        Assert.That(response!.Comment, Is.Not.Null);
        Assert.That(response.Comment, Is.Empty);
        Assert.That(response.Changelog, Is.Not.Null);
    }

    private static void AssertDelayNear(TimeSpan expected, TimeSpan actual)
    {
        // ±25 % jitter, asserted as a band rather than a value: the jitter is the point, so pinning
        // an exact delay would either need a seeded Random or be flaky.
        Assert.That(actual, Is.InRange(expected * 0.75, expected * 1.25));
    }
}
