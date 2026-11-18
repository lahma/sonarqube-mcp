using System.Net;
using System.Text.Json;

using ModelContextProtocol;

using NUnit.Framework;

using SonarQube.Mcp.Tests.Http;
using SonarQube.Mcp.Tools;
using SonarQube.Mcp.Tools.Models;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// What the tools actually do with their arguments: the requests they compose, the argument
/// validation they refuse to send, and the shape of what comes back.
/// </summary>
/// <remarks>
/// <para>
/// Everything runs through the real <c>SonarApiClient</c> over a stub transport, so these tests see
/// the URLs, form bodies and JSON an MCP client's call would produce — and the responses are the
/// golden fixtures wherever a live capture exists, so "the mapper handles it" is a claim about
/// SonarQube's actual output rather than about a payload written to suit the mapper.
/// </para>
/// <para>
/// The recurring shape of a validation test is <c>Assert.Empty(handler.Requests)</c>: an argument
/// this server can reject itself must cost the caller nothing, and a message about <c>pageSize</c>
/// that arrives after a round trip has already spent the thing it was protecting.
/// </para>
/// </remarks>
public class ToolBehaviourTests
{
    private const string Project = ToolTestHost.Project;
    private const string FileKey = ToolTestHost.FileComponent;
    private const string IssueKey = "AZ_xePOumT_q4T_1FWf8";
    private const string HotspotKey = "AZvu8ZyfNsnCVHe5poFs";

    /// <summary>
    /// The issue the three write fixtures were captured from. A different issue from
    /// <see cref="IssueKey"/> on purpose: the read fixtures were captured anonymously months earlier,
    /// and the writes needed an issue that was still OPEN and trivial enough to transition and put
    /// back. See <c>Fixtures/MANIFEST.md</c>.
    /// </summary>
    private const string WriteIssueKey = "AaAmMrFelhOWn70x31R7";

    /// <summary>The file <see cref="WriteIssueKey"/> is in, as the write fixtures' components report it.</summary>
    private const string WriteIssueFile = "src/Quartz.HttpClient/QuartzHttpClientServiceCollectionExtensions.cs";

    /// <summary>The newest comment in <c>issues-add_comment.json</c> — the one that capture posted.</summary>
    private const string WriteCommentKey = "AaAoPByOjn1jm7-NQYn-";

    /// <summary>The text of <see cref="WriteCommentKey"/>, so request and response describe one call.</summary>
    private const string WriteCommentText = "sonarqube-mcp wire capture 2 - removing shortly";

    /// <summary>
    /// The project the three coverage fixtures came from: public, anonymously readable, and outside
    /// this organization on purpose — <c>quartznet</c> publishes no coverage report, so none of the
    /// coverage-bearing shapes can be captured from it. See <c>Fixtures/MANIFEST.md</c>.
    /// </summary>
    private const string CoverageProject = "apache_creadur-rat";

    /// <summary>The file whose lines 43–57 carry all four per-line coverage states at once.</summary>
    private const string CoverageFile =
        "apache-rat-core/src/main/java/org/apache/rat/configuration/builders/RegexBuilder.java";

    /// <summary>A file whose lines 40–54 are measured and have nothing wrong with them.</summary>
    private const string CleanCoverageFile =
        "apache-rat-core/src/main/java/org/apache/rat/annotation/ApacheV2LicenseAppender.java";

    private static readonly string[] Ncloc = ["ncloc"];
    private static readonly string[] NclocAndRating = ["ncloc", "sqale_rating", "new_coverage"];

    // ---------------------------------------------------------------------------------------
    // listProjects
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task ListProjectsSearchesComponentsInTheConfiguredOrganization()
    {
        using var handler = Stub("components-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.ListProjectsAsync(
            client,
            ToolTestHost.CreateOptions(),
            query: "quartz",
            page: 2,
            pageSize: 25,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/components/search"));
        Assert.That(Query(handler, "organization"), Is.EqualTo("quartznet"));
        Assert.That(Query(handler, "qualifiers"), Is.EqualTo("TRK"));
        Assert.That(Query(handler, "q"), Is.EqualTo("quartz"));
        Assert.That(Query(handler, "p"), Is.EqualTo("2"));
        Assert.That(Query(handler, "ps"), Is.EqualTo("25"));

        Assert.That(result.Projects, Has.Exactly(1).Items);
        var project = result.Projects.Single();

        Assert.That(project.Key, Is.EqualTo(Project));
        Assert.That(project.Url, Is.EqualTo("https://sonarcloud.io/project/overview?id=quartznet_quartznet"));
    }

    [Test]
    public async Task ListProjectsWithoutAnOrganizationNamesTheVariableAndSendsNothing()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListProjectsAsync(
                client,
                ToolTestHost.CreateOptions(organization: null),
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("SONARQUBE_ORG").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// Clamping is silent: the binding constraint is the model's context rather than the API's
    /// ceiling, so a caller asking for 500 rows is answered with fewer rather than refused.
    /// </summary>
    [TestCase(null, "50")]
    [TestCase(0, "1")]
    [TestCase(-7, "1")]
    [TestCase(25, "25")]
    [TestCase(100, "100")]
    [TestCase(500, "100")]
    [TestCase(10_000, "100")]
    public async Task PageSizeIsClampedIntoTheConfiguredRange(int? requested, string expected)
    {
        using var handler = Stub("components-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await ProjectReadTools.ListProjectsAsync(
            client,
            ToolTestHost.CreateOptions(),
            pageSize: requested,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "ps"), Is.EqualTo(expected));
    }

    [TestCase(null, "1")]
    [TestCase(0, "1")]
    [TestCase(-3, "1")]
    [TestCase(4, "4")]
    public async Task PageIsAlwaysAtLeastOne(int? requested, string expected)
    {
        using var handler = Stub("components-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await ProjectReadTools.ListProjectsAsync(
            client,
            ToolTestHost.CreateOptions(),
            page: requested,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "p"), Is.EqualTo(expected));
    }

    /// <summary>
    /// The 10,000-result window is not clamped, because there is no smaller answer to give: the
    /// requested page does not exist and never will. It is refused before a request is made.
    /// </summary>
    [Test]
    public async Task APageBeyondTheResultWindowIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListProjectsAsync(
                client,
                ToolTestHost.CreateOptions(),
                page: 200,
                pageSize: 100,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("10000").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("narrow the search").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // listComponents
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task ListComponentsDefaultsToTheFilesInTheWholeProject()
    {
        using var handler = Stub("components-tree-leaves.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.ListComponentsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/components/tree"));
        Assert.That(Query(handler, "component"), Is.EqualTo(Project));
        Assert.That(Query(handler, "strategy"), Is.EqualTo("leaves"));
        Assert.That(Query(handler, "qualifiers"), Is.EqualTo("FIL,UTS"));

        Assert.That(result.Components.Count, Is.EqualTo(3));
        Assert.That(result.Components[0].Path, Is.EqualTo(".github/ISSUE_TEMPLATE/01_bug_report.yml"));
        Assert.That(result.HasMore, Is.True);
        Assert.That(result.TotalCount, Is.EqualTo(1048));
    }

    [TestCase("files", "leaves", "FIL,UTS")]
    [TestCase("directories", "children", "DIR")]
    [TestCase("all", "all", null)]
    public async Task ScopeChoosesTheStrategyAndQualifierPair(string scope, string strategy, string? qualifiers)
    {
        using var handler = Stub("components-tree-leaves.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await ProjectReadTools.ListComponentsAsync(
            client,
            ToolTestHost.CreateOptions(),
            scope: scope,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "strategy"), Is.EqualTo(strategy));
        Assert.That(Query(handler, "qualifiers"), Is.EqualTo(qualifiers));
    }

    [Test]
    public async Task AnUnknownScopeIsRefusedWithTheThreeThatWork()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListComponentsAsync(
                client,
                ToolTestHost.CreateOptions(),
                scope: "everything",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("files, directories or all").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// The pair is mutually exclusive on every endpoint that takes it, so it is validated once — and
    /// before the request, because SonarQube answers the combination with an opaque 400.
    /// </summary>
    [Test]
    public async Task BranchAndPullRequestTogetherAreRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListComponentsAsync(
                client,
                ToolTestHost.CreateOptions(),
                branch: "main",
                pullRequest: "3266",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("mutually exclusive").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task AProjectKeyArgumentBeatsTheEnvironmentDefault()
    {
        using var handler = Stub("components-tree-leaves.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await ProjectReadTools.ListComponentsAsync(
            client,
            ToolTestHost.CreateOptions(),
            projectKey: "other_project",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "component"), Is.EqualTo("other_project"));
    }

    [Test]
    public async Task WithNoProjectAnywhereTheErrorNamesTheVariableAndListProjects()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListComponentsAsync(
                client,
                ToolTestHost.CreateOptions(defaultProject: null),
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("SONARQUBE_MCP_DEFAULT_PROJECT").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("listProjects").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // listBranches, listPullRequests, getQualityGateStatus
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task ListBranchesAsksTheUnpaginatedEndpointAndMarksTheMainBranch()
    {
        using var handler = Stub("project-branches-list.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.ListBranchesAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/project_branches/list"));
        Assert.That(Names(handler), Is.EqualTo<string[]>(["project"]));

        Assert.That(result.Branches, Has.Exactly(1).Items);
        var branch = result.Branches.Single();

        Assert.That(branch.Name, Is.EqualTo("main"));
        Assert.That(branch.IsMain, Is.True);
        Assert.That(branch.Bugs, Is.EqualTo(140));
        Assert.That(branch.CommitSha, Is.EqualTo("a8dc2ec41b298390fb1f48ca3f2ef4d4d85128eb"));
        Assert.That(
            branch.Url,
            Is.EqualTo("https://sonarcloud.io/project/overview?id=quartznet_quartznet&branch=main"));
    }

    [Test]
    public async Task ListPullRequestsReportsTheKeyToPassBackAndTheGateStatus()
    {
        using var handler = Stub("project-pull-requests-list.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/project_pull_requests/list"));
        Assert.That(result.PullRequests.Count, Is.EqualTo(2));

        var failing = result.PullRequests[1];

        Assert.That(failing.Key, Is.EqualTo("3267"));
        Assert.That(failing.Base, Is.EqualTo("main"));
        Assert.That(failing.QualityGateStatus, Is.EqualTo("ERROR"));
        Assert.That(failing.ScmUrl, Is.EqualTo("https://github.com/quartznet/quartznet/pull/3267"));
        Assert.That(
            failing.Url,
            Is.EqualTo("https://sonarcloud.io/project/overview?id=quartznet_quartznet&pullRequest=3267"));
    }

    /// <summary>
    /// C14: the gate is read from the two gate measures, not from the deprecated
    /// qualitygates/project_status, and an ungated scope simply has neither measure.
    /// </summary>
    [Test]
    public async Task AQualityGateWithNoVerdictYetIsExplainedRatherThanReportedAsAFailure()
    {
        using var handler = Stub("measures-component-quality-gate-none.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.GetQualityGateStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/measures/component"));
        Assert.That(Query(handler, "component"), Is.EqualTo(Project));
        Assert.That(Query(handler, "metricKeys"), Is.EqualTo("alert_status,quality_gate_details"));
        Assert.That(result.Status, Is.EqualTo("NONE"));
        Assert.That(result.Conditions, Is.Empty);
        Assert.That(result.Note, Is.Not.Null);
        Assert.That(result.Note, Contains.Substring("not an error").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task AFailingGateArrivesWithItsFailuresAlreadyFilteredAndItsRatingsAsLetters()
    {
        using var handler = Stub("measures-component-quality-gate-error.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.GetQualityGateStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            projectKey: "apache_creadur-rat",
            pullRequest: "749",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "pullRequest"), Is.EqualTo("749"));
        Assert.That(Query(handler, "branch"), Is.Null);
        Assert.That(result.Status, Is.EqualTo("ERROR"));
        Assert.That(result.Conditions.Count, Is.EqualTo(6));
        Assert.That(
            result.FailingConditions.Select(condition => condition.Metric),
            Is.EqualTo<string?[]>(["new_reliability_rating", "new_security_rating", "new_coverage"]));

        var rating = result.FailingConditions[0];

        Assert.That(rating.Threshold, Is.EqualTo("A"));
        Assert.That(rating.ActualValue, Is.EqualTo("D"));
        Assert.That(rating.Comparator, Is.EqualTo("GT"));
        Assert.That(rating.Status, Is.EqualTo("ERROR"));
        Assert.That(rating.OnNewCode, Is.True);
        Assert.That(result.FailingConditions[1].ActualValue, Is.EqualTo("E"));

        // A non-rating metric keeps the number SonarQube sent.
        Assert.That(result.FailingConditions[2].Threshold, Is.EqualTo("80"));
        Assert.That(result.FailingConditions[2].ActualValue, Is.EqualTo("35.3"));
        Assert.That(result.Note, Is.Null);
    }

    /// <summary>
    /// The C14 equivalence, from the pull request it was verified on: the verdict comes from
    /// alert_status, the four conditions from the JSON inside quality_gate_details.
    /// </summary>
    [Test]
    public async Task APassingPullRequestGateCarriesEveryConditionAndNoFailures()
    {
        using var handler = Stub("measures-component-quality-gate-pr.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.GetQualityGateStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            pullRequest: "4358",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Status, Is.EqualTo("OK"));
        Assert.That(result.PullRequest, Is.EqualTo("4358"));
        Assert.That(result.FailingConditions, Is.Empty);
        Assert.That(
            result.Conditions.Select(condition => condition.Metric),
            Is.EqualTo<string?[]>([
                "new_reliability_rating", "new_security_rating", "new_maintainability_rating",
                "new_security_hotspots_reviewed",
            ]));
        Assert.That(result.Conditions[3].ActualValue, Is.EqualTo("100.0"));
        Assert.That(result.Note, Is.Null);
    }

    [Test]
    public async Task GateDetailsThatAreNotJsonAreReportedAsAnUnreadableResponse()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""
            {"component":{"key":"quartznet_quartznet","measures":[
              {"metric":"alert_status","value":"OK"},
              {"metric":"quality_gate_details","value":"not json"}]}}
            """);
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.GetQualityGateStatusAsync(
                client,
                ToolTestHost.CreateOptions(),
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("quality_gate_details").Using(StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // searchIssues
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task SearchIssuesDefaultsToTheWorkThatIsStillOutstanding()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/issues/search"));
        Assert.That(Query(handler, "issueStatuses"), Is.EqualTo("OPEN,CONFIRMED"));
        Assert.That(Query(handler, "componentKeys"), Is.EqualTo(Project));

        // Always sent: the sidecar the file-path join needs, and never _all, which drags a
        // fifty-element languages array onto every response.
        // ruleDescriptionContextKey is opt-in (C15): without it no issue says which variant of its
        // rule's fix guidance applies.
        Assert.That(Query(handler, "additionalFields"), Is.EqualTo("rules,ruleDescriptionContextKey"));
        Assert.That(Names(handler), Does.Not.Contain("author"));
        Assert.That(Names(handler), Does.Not.Contain("fixableByAgent"));

        Assert.That(Query(handler, "s"), Is.EqualTo("CREATION_DATE"));
        Assert.That(Query(handler, "asc"), Is.EqualTo("false"));
    }

    /// <summary>
    /// C16: author is the one list SonarQube wants repeated - comma-joined it is read as a single
    /// literal author and matches nothing.
    /// </summary>
    [Test]
    public async Task AuthorsAreSentOnePerParameterAndFixableByAgentOnlyWhenTrue()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(SonarFixtures.Read("issues-search-empty.json"));
        handler.EnqueueJson(SonarFixtures.Read("issues-search-empty.json"));
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            authors: ["ada@example.com", " bob@example.com "],
            fixableByAgent: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            fixableByAgent: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var authors = RequestUrl.Query(handler.Requests[0].Uri)
            .Where(pair => pair.Key == "author")
            .Select(pair => pair.Value);

        Assert.That(authors, Is.EqualTo<string[]>(["ada@example.com", "bob@example.com"]));
        Assert.That(Query(handler, "fixableByAgent"), Is.EqualTo("true"));

        // false filters nothing on the API side, so it is not sent at all.
        Assert.That(Names(handler, 1), Does.Not.Contain("fixableByAgent"));
    }

    [Test]
    public async Task AnIssueSaysWhichVariantOfItsRulesFixGuidanceApplies()
    {
        using var handler = Stub("issues-search-rule-context.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            projectKey: "jspwiki-builder",
            rules: ["javasecurity:S5131"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Issues[0].Rule, Is.EqualTo("javasecurity:S5131"));
        Assert.That(result.Issues[0].RuleContext, Is.EqualTo("jsp"));
    }

    [Test]
    public async Task AnExplicitStatusListReplacesTheDefaultRatherThanAddingToIt()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            issueStatuses: ["accepted", "FIXED", "ACCEPTED"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        // Upper-cased and deduplicated, in the order the caller gave them.
        Assert.That(Query(handler, "issueStatuses"), Is.EqualTo("ACCEPTED,FIXED"));
    }

    /// <summary>
    /// <c>asc</c> is sent on every call, including the default. It is a plain <c>bool</c> rather
    /// than <c>bool?</c> precisely so the sort direction is never left to the endpoint's own
    /// default, which is ascending — the opposite of what a triage list wants.
    /// </summary>
    [Test]
    public async Task TheSortDirectionIsAlwaysStatedExplicitly()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            sortBy: "severity",
            ascending: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "s"), Is.EqualTo("SEVERITY"));
        Assert.That(Query(handler, "asc"), Is.EqualTo("true"));
    }

    [TestCase("src/Widget.cs", "quartznet_quartznet:src/Widget.cs")]
    [TestCase("quartznet_quartznet:src/Widget.cs", "quartznet_quartznet:src/Widget.cs")]
    [TestCase("src\\Widget.cs", "quartznet_quartznet:src/Widget.cs")]
    [TestCase("./src/Widget.cs", "quartznet_quartznet:src/Widget.cs")]
    [TestCase("/src/Widget.cs", "quartznet_quartznet:src/Widget.cs")]
    [TestCase("quartznet_quartznet", "quartznet_quartznet")]
    public async Task AComponentIsAcceptedAsAPathOrAsAKey(string component, string expected)
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            component: component,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "componentKeys"), Is.EqualTo(expected));
    }

    [Test]
    public async Task AComponentThatNormalisesToNothingIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                component: "./",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("listComponents").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task SearchIssuesRefusesBranchAndPullRequestTogetherBeforeAnyRequest()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        _ = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                branch: "main",
                pullRequest: "3266",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task TheNewCodeFilterIsSentUnderTheNameTheApiUses()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            inNewCodePeriod: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "sinceLeakPeriod"), Is.EqualTo("true"));
        Assert.That(Names(handler), Does.Not.Contain("inNewCodePeriod"));
    }

    /// <summary>
    /// The legacy vocabularies are rejected <em>with the translation</em>. A model trained before
    /// 2024 reaches for MAJOR and CODE_SMELL, and SonarQube's own 400 lists the accepted values
    /// without saying which of them means what was asked for.
    /// </summary>
    [Test]
    public async Task ALegacySeverityIsRejectedWithItsModernEquivalent()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                impactSeverities: ["MAJOR"],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("MAJOR is now MEDIUM").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("INFO, LOW, MEDIUM, HIGH, BLOCKER").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task ALegacyIssueTypeIsRejectedWithItsCleanCodeEquivalent()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                impactSoftwareQualities: ["CODE_SMELL"],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("CODE_SMELL is now MAINTAINABILITY").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task ALegacyStatusIsRejectedWithTheThreeStatusesItSplitInto()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                issueStatuses: ["RESOLVED"],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("RESOLVED split into FALSE_POSITIVE, ACCEPTED and FIXED").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task TheTwoCreationDateFiltersAreRefusedTogether()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                createdAfter: "2026-01-01",
                createdInLast: "7d",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("mutually exclusive").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [TestCase("7 days")]
    [TestCase("d")]
    [TestCase("7x")]
    public async Task AMalformedRelativeWindowNamesTheFormsThatWork(string createdInLast)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                createdInLast: createdInLast,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("7d, 2w, 1m, 1y").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task AnUnsortableFieldIsRefusedWithTheSevenThatAreSortable()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                sortBy: "RANDOM",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("CREATION_DATE").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// The single biggest context saving in the tool layer: the model sees
    /// <c>src/…/QuartzScheduler.cs</c> rather than <c>quartznet_quartznet:src/…/QuartzScheduler.cs</c>
    /// repeated on every row, with the key still available when it needs one back.
    /// </summary>
    [Test]
    public async Task IssuesComeBackWithTheirFilePathJoinedFromTheComponentsSidecar()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        foreach (var issue in result.Issues)
        {
            Assert.That(string.IsNullOrEmpty(issue.File), Is.False);
            Assert.That(issue.File!, Does.Not.Contain(':'));
        }

        var second = result.Issues[1];

        Assert.That(second.File, Is.EqualTo("src/Quartz/Core/QuartzScheduler.cs"));
        Assert.That(second.Component, Is.EqualTo(FileKey));
        Assert.That(second.Line, Is.EqualTo(943));
        Assert.That(second.Impacts, Has.Exactly(1).Items);
        Assert.That(second.Impacts.Single().SoftwareQuality, Is.EqualTo("MAINTAINABILITY"));
        Assert.That(result.TotalCount, Is.EqualTo(1606));
        Assert.That(result.HasMore, Is.True);
    }

    [Test]
    public async Task AResultSetLargerThanTheWindowCarriesTheWarningBeforeTheCallerPagesIntoIt()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        // 1606 results is under the cap, so there is nothing to warn about.
        Assert.That(result.Note, Is.Null);
    }

    // ---------------------------------------------------------------------------------------
    // getIssue
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task GetIssueAsksTheSearchEndpointForOneKeyWithItsTransitionsAndComments()
    {
        using var handler = Stub("issues-search-single.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            IssueKey,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/issues/search"));
        Assert.That(Query(handler, "issues"), Is.EqualTo(IssueKey));
        Assert.That(
            Query(handler, "additionalFields"),
            Is.EqualTo("transitions,comments,rules,users,ruleDescriptionContextKey"));
        Assert.That(Query(handler, "ps"), Is.EqualTo("1"));

        Assert.That(result.Key, Is.EqualTo(IssueKey));
        Assert.That(result.File, Is.EqualTo("src/Quartz/Core/QuartzScheduler.cs"));
        Assert.That(
            result.RuleName,
            Is.EqualTo("Null-forgiving operators should not be used when nullable warnings are disabled"));
        Assert.That(result.AvailableTransitions, Is.Empty);
        Assert.That(
            result.Url,
            Is.EqualTo("https://sonarcloud.io/project/issues?id=quartznet_quartznet&issues=AZ_xePOumT_q4T_1FWf8&open=AZ_xePOumT_q4T_1FWf8"));
    }

    [Test]
    public async Task AnIssueKeyThatMatchesNothingSaysWhereToLookInstead()
    {
        using var handler = Stub("issues-search-empty.json");
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetIssueAsync(
                client,
                ToolTestHost.CreateOptions(),
                "AZ_nosuchissue",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("AZ_nosuchissue").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("searchIssues").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task ABlankIssueKeyIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetIssueAsync(
                client,
                ToolTestHost.CreateOptions(),
                "   ",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("issueKey is required").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// The regression for the defect this release exists to fix. SonarQube Cloud only applies a
    /// <c>branch</c> or <c>pullRequest</c> filter to an <c>issues=</c> key lookup when the project is
    /// named alongside it. Verified live on 2026-09-09 against <c>quartznet_quartznet</c> pull
    /// request 3735: the key filter plus <c>pullRequest</c> answered <c>200</c> with
    /// <c>total: 0</c>, and the same query with <c>componentKeys</c> added answered with the issue.
    /// So <c>componentKeys</c> has to be on the wire whenever a project can be resolved — not only
    /// when a scope is given, because a caller who omits the scope on a main-branch issue must keep
    /// working either way.
    /// </summary>
    [TestCase(null, null)]
    [TestCase("release/4.0", null)]
    [TestCase(null, "3735")]
    public async Task GetIssueNamesTheProjectSoTheScopeIsActuallyApplied(string? branch, string? pullRequest)
    {
        using var handler = Stub("issues-search-single.json");
        using var client = ToolTestHost.CreateClient(handler);

        await IssueReadTools.GetIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            IssueKey,
            branch: branch,
            pullRequest: pullRequest,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "componentKeys"), Is.EqualTo(ToolTestHost.Project));
        Assert.That(Query(handler, "issues"), Is.EqualTo(IssueKey));
        Assert.That(Query(handler, "branch"), Is.EqualTo(branch));
        Assert.That(Query(handler, "pullRequest"), Is.EqualTo(pullRequest));
    }

    /// <summary>An explicit <c>projectKey</c> wins over the configured default, as everywhere else.</summary>
    [Test]
    public async Task GetIssuePrefersAnExplicitProjectKeyOverTheConfiguredDefault()
    {
        using var handler = Stub("issues-search-single.json");
        using var client = ToolTestHost.CreateClient(handler);

        await IssueReadTools.GetIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            IssueKey,
            projectKey: "otherorg_otherrepo",
            pullRequest: "3735",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "componentKeys"), Is.EqualTo("otherorg_otherrepo"));
    }

    /// <summary>
    /// With no project to name, a scoped lookup cannot be honoured — so it is refused here rather
    /// than sent and reported back as "no such issue", which is what the API would make it look like.
    /// A main-branch read with no project stays legal, because that one genuinely works.
    /// </summary>
    [Test]
    public async Task GetIssueRefusesAScopedLookupItCannotNameAProjectForWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetIssueAsync(
                client,
                ToolTestHost.CreateOptions(defaultProject: null),
                IssueKey,
                pullRequest: "3735",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("projectKey").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring(ToolDefaults.DefaultProjectVariable).Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("3735").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>Unscoped and with nothing configured, the key alone still resolves on the main branch.</summary>
    [Test]
    public async Task GetIssueWithNoProjectAnywhereStillReadsAMainBranchIssue()
    {
        using var handler = Stub("issues-search-single.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetIssueAsync(
            client,
            ToolTestHost.CreateOptions(defaultProject: null),
            IssueKey,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "componentKeys"), Is.Null);
        Assert.That(result.Key, Is.EqualTo(IssueKey));
    }

    /// <summary>
    /// A hotspot key has the same shape as an issue key and resolves to nothing here, so the message
    /// has to name the tool that does read it. Without that the model retries getIssue.
    /// </summary>
    [Test]
    public async Task AnIssueKeyThatMatchesNothingPointsAtGetHotspotAsWellAsSearchIssues()
    {
        using var handler = Stub("issues-search-empty.json");
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetIssueAsync(
                client,
                ToolTestHost.CreateOptions(),
                "AZ_nosuchissue",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("getHotspot").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("projectKey").Using(StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // getRule
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The default sections are asked for in reading order — what it is, why, how to fix it — and
    /// that is the order they come back in, whatever order the API listed them. This rule has no
    /// <c>introduction</c>, so a section that was asked for and does not exist is simply absent
    /// rather than empty.
    /// </summary>
    [Test]
    public async Task GetRuleReturnsTheRequestedSectionsInTheOrderTheyAreRead()
    {
        using var handler = Stub("rules-search-with-sections.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "csharpsquid:S2259",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/rules/search"));
        Assert.That(Query(handler, "rule_key"), Is.EqualTo("csharpsquid:S2259"));
        Assert.That(Query(handler, "organization"), Is.EqualTo("quartznet"));
        Assert.That(Query(handler, "ps"), Is.EqualTo("1"));
        Assert.That(Query(handler, "f")!, Contains.Substring("descriptionSections").Using(StringComparison.Ordinal));

        // The capture lists how_to_fix first; the requested order wins, and the introduction the
        // defaults ask for is not one this rule has.
        Assert.That(result.Sections.Select(section => section.Key), Is.EqualTo<string?[]>(["root_cause", "how_to_fix"]));

        // The HTML is gone and the code sample is fenced.
        Assert.That(result.Sections[0].Content!, !Contains.Substring("<pre").Using(StringComparison.Ordinal));
        Assert.That(result.Sections[0].Content!, Contains.Substring("```").Using(StringComparison.Ordinal));

        foreach (var section in result.Sections)
        {
            Assert.That(section.Context, Is.Null);
        }

        Assert.That(result.Truncated, Is.False);
        Assert.That(result.Note, Is.Null);
    }

    /// <summary>
    /// A rule with per-framework fix guidance answers <c>how_to_fix</c> once per context, and both
    /// copies are returned: the context is the only thing that distinguishes them, so dropping it
    /// would leave two identical-looking sections with different advice.
    /// </summary>
    [Test]
    public async Task GetRuleKeepsEveryContextOfAPerFrameworkSection()
    {
        using var handler = Stub("rules-search-with-contexts.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "javasecurity:S2076",
            sections: ["how_to_fix"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Sections.Select(section => section.Key), Is.EqualTo<string?[]>(["how_to_fix", "how_to_fix"]));

        // The display name, not the key: it is what the section header would read in the UI.
        Assert.That(
            result.Sections.Select(section => section.Context),
            Is.EqualTo<string?[]>(["Java Lang Package", "Apache Commons"]));
        Assert.That(
            result.Sections.Select(section => section.ContextKey),
            Is.EqualTo<string?[]>(["java_lang_package", "apache_commons"]));
    }

    [Test]
    public async Task GetRuleWithAnIssuesContextKeepsOnlyThatVariant()
    {
        using var handler = Stub("rules-search-with-contexts.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "javasecurity:S2076",
            sections: ["root_cause", "how_to_fix"],
            context: "apache_commons",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        // Sections without a context are kept; of the per-framework ones only the asked-for one is.
        Assert.That(
            result.Sections.Select(section => section.Key),
            Is.EqualTo<string?[]>(["root_cause", "how_to_fix"]));
        Assert.That(result.Sections[1].ContextKey, Is.EqualTo("apache_commons"));
        Assert.That(result.Note, Is.Null);
    }

    [Test]
    public async Task AContextTheRuleDoesNotDocumentFiltersNothingAndNamesTheOnesItDoes()
    {
        using var handler = Stub("rules-search-with-contexts.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "javasecurity:S2076",
            sections: ["how_to_fix"],
            context: "spring",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Sections.Count, Is.EqualTo(2));
        Assert.That(result.Note, Contains.Substring("no 'spring' variant").Using(StringComparison.Ordinal));
        Assert.That(result.Note, Contains.Substring("java_lang_package, apache_commons").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task AContextOnARuleWithoutVariantsIsSaidToChangeNothing()
    {
        using var handler = Stub("rules-search-with-sections.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "csharpsquid:S2259",
            context: "spring",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Sections, Is.Not.Empty);
        Assert.That(result.Note, Contains.Substring("no framework variants").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task GetRuleReturnsOnlyTheSectionsThatWereAskedFor()
    {
        using var handler = Stub("rules-search-with-sections.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "csharpsquid:S2259",
            sections: ["resources"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Sections, Has.Exactly(1).Items);
        var section = result.Sections.Single();

        Assert.That(section.Key, Is.EqualTo("resources"));
        Assert.That(section.Content!, Contains.Substring("- CWE-476").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// The degraded case, captured live and anonymously: name, impacts and clean-code attribute
    /// arrive, descriptions do not. Half a rule is still worth having, so it is a note rather than
    /// an exception.
    /// </summary>
    [Test]
    public async Task ARuleWithNoDescriptionSectionsDegradesWithANoteRatherThanThrowing()
    {
        using var handler = Stub("rules-search-rule-key.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "csharpsquid:S2259",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Sections, Is.Empty);
        Assert.That(result.Name, Is.EqualTo("Null pointers should not be dereferenced"));
        Assert.That(result.Impacts, Has.Exactly(1).Items);
        Assert.That(result.Impacts.Single().SoftwareQuality, Is.EqualTo("RELIABILITY"));
        Assert.That(result.Note, Is.Not.Null);
        Assert.That(result.Note, Contains.Substring("SONARQUBE_TOKEN").Using(StringComparison.Ordinal));
    }

    /// <summary>A rule old enough to predate sections carries one HTML blob; it becomes root_cause.</summary>
    [Test]
    public async Task ALegacyHtmlDescriptionIsReturnedAsASyntheticRootCauseSection()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.RuleWithHtmlDescOnly);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "csharpsquid:S1118",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Sections, Has.Exactly(1).Items);
        var section = result.Sections.Single();

        Assert.That(section.Key, Is.EqualTo("root_cause"));
        Assert.That(section.Content!, Contains.Substring("- Add a private constructor.").Using(StringComparison.Ordinal));
        Assert.That(result.Note, Is.Null);
    }

    [Test]
    public async Task ARuleKeyWithoutARepositoryIsRefusedWithTheShapeThatWorks()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetRuleAsync(
                client,
                ToolTestHost.CreateOptions(),
                "S2259",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("repository:rule").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task ARuleKeyThatMatchesNothingNamesTheKeyAndTheOrganization()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.NoRules);

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetRuleAsync(
                client,
                ToolTestHost.CreateOptions(),
                "csharpsquid:S9999",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("csharpsquid:S9999").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("quartznet").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task AnUnknownRuleSectionIsRefusedWithTheFiveThatExist()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetRuleAsync(
                client,
                ToolTestHost.CreateOptions(),
                "csharpsquid:S2259",
                sections: ["summary"],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("assess_the_problem").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // searchHotspots and getHotspot
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// C12. <c>files</c> takes the project-relative path; a component key matches nothing at all,
    /// and does it silently — an empty result rather than an error.
    /// </summary>
    [TestCase("src/Quartz.Examples.AspNetCore/appsettings.json")]
    [TestCase("quartznet_quartznet:src/Quartz.Examples.AspNetCore/appsettings.json")]
    [TestCase("src\\Quartz.Examples.AspNetCore\\appsettings.json")]
    public async Task SearchHotspotsFiltersByPathWhateverSpellingTheCallerUsed(string component)
    {
        using var handler = Stub("hotspots-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchHotspotsAsync(
            client,
            ToolTestHost.CreateOptions(),
            component: component,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/hotspots/search"));
        Assert.That(Query(handler, "files"), Is.EqualTo("src/Quartz.Examples.AspNetCore/appsettings.json"));
        Assert.That(Query(handler, "projectKey"), Is.EqualTo(Project));
    }

    [Test]
    public async Task WithNoComponentSearchHotspotsSendsNoFileFilterAtAll()
    {
        using var handler = Stub("hotspots-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchHotspotsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Names(handler), Does.Not.Contain("files"));
    }

    /// <summary>The project key is not a file filter, so it must not be sent as one.</summary>
    [Test]
    public async Task TheProjectItselfIsNeverSentAsAFileFilter()
    {
        using var handler = Stub("hotspots-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchHotspotsAsync(
            client,
            ToolTestHost.CreateOptions(),
            component: Project,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Names(handler), Does.Not.Contain("files"));
    }

    [Test]
    public async Task HotspotSearchReportsTheAssigneeAsTheUuidItActuallyIs()
    {
        using var handler = Stub("hotspots-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SearchHotspotsAsync(
            client,
            ToolTestHost.CreateOptions(),
            status: "TO_REVIEW",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "status"), Is.EqualTo("TO_REVIEW"));

        var hotspot = result.Hotspots[0];

        Assert.That(hotspot.Key, Is.EqualTo(HotspotKey));
        Assert.That(hotspot.File, Is.EqualTo("src/Quartz.Examples.AspNetCore/appsettings.json"));
        Assert.That(hotspot.AssigneeId, Is.EqualTo("AYgE5F7pEoXHSow6lKjD"));
        Assert.That(hotspot.VulnerabilityProbability, Is.EqualTo("HIGH"));
        Assert.That(result.TotalCount, Is.EqualTo(28));
        Assert.That(result.HasMore, Is.True);
    }

    [Test]
    public async Task TheHotspotSearchFilterRefusesAResolutionSonarQubeCloudDoesNotHave()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchHotspotsAsync(
                client,
                ToolTestHost.CreateOptions(),
                resolution: "ACKNOWLEDGED",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("FIXED or SAFE").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task GetHotspotUsesTheParameterNameTheEndpointActuallyTakes()
    {
        using var handler = Stub("hotspots-show.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetHotspotAsync(
            client,
            ToolTestHost.CreateOptions(),
            HotspotKey,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/hotspots/show"));
        Assert.That(Names(handler), Is.EqualTo<string[]>(["hotspot"]));

        // A login here, unlike the UUID the search endpoint reports under the same name (C7).
        Assert.That(result.Assignee, Is.EqualTo("lahma@github"));
        Assert.That(result.File, Is.EqualTo("src/Quartz.Examples.AspNetCore/appsettings.json"));
        Assert.That(result.CanChangeStatus, Is.False);

        Assert.That(result.Rule, Is.Not.Null);
        Assert.That(result.Rule!.RiskDescription!, Contains.Substring("Hard-coding credentials").Using(StringComparison.Ordinal));
        Assert.That(result.Rule.FixRecommendations!, Contains.Substring("```").Using(StringComparison.Ordinal));
        Assert.That(result.Rule.VulnerabilityDescription, Is.Null);
    }

    [Test]
    public async Task ABlankHotspotKeyIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetHotspotAsync(
                client,
                ToolTestHost.CreateOptions(),
                string.Empty,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("hotspotKey is required").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // getComponentMeasures and listComponentMeasures
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task GetComponentMeasuresAsksForPeriodsAndDiffsWhatCameBack()
    {
        using var handler = Stub("measures-component.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            NclocAndRating,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/measures/component"));
        Assert.That(Query(handler, "metricKeys"), Is.EqualTo("ncloc,sqale_rating,new_coverage"));

        // periods, plural — the singular spelling is a 400.
        Assert.That(Query(handler, "additionalFields"), Is.EqualTo("periods"));

        Assert.That(result.Component, Is.EqualTo(Project));
        Assert.That(result.Measures[0].Value, Is.EqualTo("119857"));

        // "1.0" means A, which is the single most confusing value in the whole API.
        Assert.That(result.Measures[1].Value, Is.EqualTo("A"));

        // Absent is not zero: the metric was requested and came back with nothing.
        Assert.That(result.MissingMetrics, Is.EqualTo<string[]>(["new_coverage"]));
    }

    [Test]
    public async Task ANewCodeValueIsReportedAsOneAndNeverAsTheOverallValue()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.MeasuresWithRatingAndPeriods);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            ["sqale_rating", "new_coverage", "new_reliability_rating"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Measures[0].Value, Is.EqualTo("C"));
        Assert.That(result.Measures[0].NewCodeValue, Is.Null);

        Assert.That(result.Measures[1].Value, Is.Null);
        Assert.That(result.Measures[1].NewCodeValue, Is.EqualTo("82.5"));

        // A rating in the new-code period is still a letter.
        Assert.That(result.Measures[2].NewCodeValue, Is.EqualTo("E"));
        Assert.That(result.MissingMetrics, Is.Empty);
    }

    [Test]
    public async Task GetComponentMeasuresAcceptsTwentyFiveMetricsAndRefusesTwentySix()
    {
        using var handler = Stub("measures-component.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await MeasureReadTools.GetComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            MetricKeys(25),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests, Has.Exactly(1).Items);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetComponentMeasuresAsync(
                client,
                ToolTestHost.CreateOptions(),
                MetricKeys(26),
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("26 entries").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("at most 25").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("metricKeys").Using(StringComparison.Ordinal));

        // The refusal cost nothing: still the one successful request.
        Assert.That(handler.Requests, Has.Exactly(1).Items);
    }

    [Test]
    public async Task NoMetricKeysAtAllPointsAtListMetrics()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetComponentMeasuresAsync(
                client,
                ToolTestHost.CreateOptions(),
                [],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("metricKeys is required").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("listMetrics").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>Fifteen is the API's own <c>maxValuesAllowed</c> on this action, not a house rule.</summary>
    [Test]
    public async Task ListComponentMeasuresAcceptsFifteenMetricsAndRefusesSixteen()
    {
        using var handler = Stub("measures-component-tree.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            MetricKeys(15),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests, Has.Exactly(1).Items);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.ListComponentMeasuresAsync(
                client,
                ToolTestHost.CreateOptions(),
                MetricKeys(16),
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("16 entries").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("at most 15").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Has.Exactly(1).Items);
    }

    /// <summary>
    /// Sorting by a metric is three parameters that only mean anything together — the third is what
    /// keeps components with no value for the metric from sorting to the top of a "worst files" list.
    /// </summary>
    [Test]
    public async Task SortingByAMetricSendsAllThreeParametersThatMakeItWork()
    {
        using var handler = Stub("measures-component-tree.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            Ncloc,
            sortByMetric: "coverage",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/measures/component_tree"));
        Assert.That(Query(handler, "s"), Is.EqualTo("metric"));
        Assert.That(Query(handler, "metricSort"), Is.EqualTo("coverage"));
        Assert.That(Query(handler, "metricSortFilter"), Is.EqualTo("withMeasuresOnly"));
        Assert.That(Query(handler, "asc"), Is.EqualTo("false"));

        Assert.That(result.Components.Count, Is.EqualTo(2));
        Assert.That(result.Components[0].Measures[0].Value, Is.EqualTo("10968"));
        Assert.That(result.TotalCount, Is.EqualTo(796));
        Assert.That(result.HasMore, Is.True);
    }

    [Test]
    public async Task WithoutASortMetricNoneOfTheSortParametersAreSent()
    {
        using var handler = Stub("measures-component-tree.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            Ncloc,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Names(handler), Does.Not.Contain("s"));
        Assert.That(Names(handler), Does.Not.Contain("metricSort"));
        Assert.That(Names(handler), Does.Not.Contain("metricSortFilter"));
    }

    [Test]
    public async Task TreeMeasuresReportTheMetricsNoComponentOnThePageHad()
    {
        using var handler = Stub("measures-component-tree.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            ["ncloc", "coverage"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.MissingMetrics, Is.EqualTo<string[]>(["coverage"]));
    }

    /// <summary>
    /// The coverage ranking over a live capture of exactly the request this tool composes.
    /// </summary>
    /// <remarks>
    /// Two things it pins. <c>metricSortFilter=withMeasuresOnly</c> is what turns 340 leaf components
    /// into the 168 this page counts — without it the files with no coverage at all rank first in a
    /// "worst covered" list, which is the reverse of the question. And the order is SonarQube's: the
    /// mapper walks the page and never re-sorts, so a ranking survives the round trip intact.
    /// </remarks>
    [Test]
    public async Task TheCoverageRankingKeepsTheOrderSonarQubeRankedItIn()
    {
        using var handler = Stub("measures-component-tree-coverage-sorted.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            ["coverage", "uncovered_lines", "uncovered_conditions", "ncloc"],
            projectKey: CoverageProject,
            sortByMetric: "coverage",
            ascending: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/measures/component_tree"));
        Assert.That(Query(handler, "s"), Is.EqualTo("metric"));
        Assert.That(Query(handler, "metricSort"), Is.EqualTo("coverage"));
        Assert.That(Query(handler, "metricSortFilter"), Is.EqualTo("withMeasuresOnly"));
        Assert.That(Query(handler, "asc"), Is.EqualTo("true"));

        Assert.That(result.TotalCount, Is.EqualTo(168));
        Assert.That(
            result.Components.Select(component => component.Path),
            Is.EqualTo<string?[]>([
                "apache-rat-plugin/src/main/java/org/apache/rat/mp/All.java",
                "apache-rat-tasks/src/main/java/org/apache/rat/anttasks/All.java",
                "apache-rat-plugin/src/main/java/org/apache/rat/mp/Any.java",
                "apache-rat-tasks/src/main/java/org/apache/rat/anttasks/Any.java",
                "apache-rat-tools/src/main/java/org/apache/rat/tools/ArgumentTypes.java",
            ]));

        // The filter guarantees every ranked row has the sort metric, and between them the five rows
        // carry the other three — including uncovered_conditions, which only one of them has.
        Assert.That(result.MissingMetrics, Is.Empty);
        Assert.That(result.Note, Is.Null);
    }

    /// <summary>
    /// An empty ranking is a statement about the metric that was ranked by, and about nothing else.
    /// </summary>
    /// <remarks>
    /// The live failure this replaces: ranking <c>quartznet_quartznet</c> by <c>coverage</c> — a
    /// project that publishes none — answered zero components and <c>missingMetrics</c>
    /// <c>["coverage","ncloc"]</c>, while that project's <c>ncloc</c> is 120,338.
    /// <c>metricSortFilter=withMeasuresOnly</c> had removed every row before any metric could appear
    /// on one, so the diff was reporting the absence of rows as the absence of measurement.
    /// </remarks>
    [Test]
    public async Task ARankingThatFilteredEverythingOutBlamesOnlyTheMetricItRankedBy()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.ComponentTreeRankedToNothing);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            ["coverage", "ncloc"],
            sortByMetric: "coverage",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Components, Is.Empty);
        Assert.That(result.TotalCount, Is.EqualTo(0));
        Assert.That(result.HasMore, Is.False);

        Assert.That(result.MissingMetrics, Is.EqualTo<string[]>(["coverage"]));

        Assert.That(result.Note, Is.Not.Null);
        Assert.That(result.Note, Contains.Substring("has a value for coverage").Using(StringComparison.Ordinal));
        Assert.That(result.Note, Contains.Substring("is not measured as zero").Using(StringComparison.Ordinal));
        Assert.That(result.Note, Contains.Substring("getComponentMeasures").Using(StringComparison.Ordinal));
        Assert.That(result.Note, Contains.Substring("listMetrics").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// The same empty page without a ranking keeps the ordinary diff, so the narrowing above cannot
    /// spread to a page that is empty for some other reason.
    /// </summary>
    [Test]
    public async Task AnEmptyPageWithNoRankingStillReportsEveryRequestedMetricAsMissing()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.ComponentTreeRankedToNothing);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            ["coverage", "ncloc"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.MissingMetrics, Is.EqualTo<string[]>(["coverage", "ncloc"]));
        Assert.That(result.Note, Is.Null);
    }

    /// <summary>
    /// A blank <c>sortByMetric</c> is no ranking at all — the client refuses to send the three sort
    /// parameters for it, so the mapper must not read the empty page as a filtered one either.
    /// </summary>
    [Test]
    public async Task ABlankSortMetricRanksNothingAndExplainsNothing()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.ComponentTreeRankedToNothing);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            ["coverage", "ncloc"],
            sortByMetric: "   ",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Names(handler), Does.Not.Contain("metricSortFilter"));
        Assert.That(result.MissingMetrics, Is.EqualTo<string[]>(["coverage", "ncloc"]));
        Assert.That(result.Note, Is.Null);
    }

    // ---------------------------------------------------------------------------------------
    // getMeasuresHistory
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task MeasureHistoryUsesTheParameterNameThisEndpointActuallyHas()
    {
        using var handler = Stub("measures-search-history.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetMeasuresHistoryAsync(
            client,
            ToolTestHost.CreateOptions(),
            ["ncloc", "coverage"],
            from: "2024-08-01",
            to: "2024-11-01",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/measures/search_history"));
        Assert.That(Query(handler, "metrics"), Is.EqualTo("ncloc,coverage"));
        Assert.That(Names(handler), Does.Not.Contain("metricKeys"));
        Assert.That(Query(handler, "from"), Is.EqualTo("2024-08-01"));
        Assert.That(Query(handler, "to"), Is.EqualTo("2024-11-01"));

        Assert.That(result.Metrics.Count, Is.EqualTo(2));
        Assert.That(result.Metrics[0].History[0].Value, Is.EqualTo("67668"));

        // A point with no value is a gap in the series, not a zero.
        Assert.That(result.Metrics[1].History[0].Value, Is.Null);

        Assert.That(result.TotalCount, Is.EqualTo(29));
        Assert.That(result.Note, Is.Not.Null);
        Assert.That(result.Note, Contains.Substring("number of analyses").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// The message has to name the parameter the caller actually has. On this one tool it is
    /// <c>metrics</c>; naming <c>metricKeys</c> would send them looking for an argument that does
    /// not exist.
    /// </summary>
    [Test]
    public async Task MeasureHistoryComplainsAboutMetricsRatherThanMetricKeys()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetMeasuresHistoryAsync(
                client,
                ToolTestHost.CreateOptions(),
                [],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("metrics is required").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, !Contains.Substring("metricKeys").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // listMetrics
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// C6: <c>f</c> is never sent. <c>type</c> is not one of its accepted values, and asking for it
    /// is a 400 — while omitting <c>f</c> returns every field including <c>type</c> and
    /// <c>direction</c>, which is what this tool reads.
    /// </summary>
    [Test]
    public async Task ListMetricsFetchesTheWholeCatalogueInOneCallAndSendsNoFieldSelector()
    {
        using var handler = Stub("metrics-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await MeasureReadTools.ListMetricsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/metrics/search"));
        Assert.That(Names(handler), Is.EqualTo<string[]>(["ps"]));
        Assert.That(Query(handler, "ps"), Is.EqualTo("500"));
    }

    [Test]
    public async Task DataMetricsAreLeftOutByDefaultAndTheNoteSaysHowMany()
    {
        using var handler = Stub("metrics-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListMetricsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Metrics.Count, Is.EqualTo(142));
        Assert.That(result.TotalCount, Is.EqualTo(142));
        Assert.That(result.Metrics, Has.None.Matches<MetricSummary>(metric => metric!.Key == "ncloc_data"));

        Assert.That(result.Note, Is.Not.Null);
        Assert.That(result.Note, Contains.Substring("13 metric(s)").Using(StringComparison.Ordinal));
        Assert.That(result.Note, Contains.Substring("includeDataMetrics=true").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task IncludeDataMetricsKeepsTheBlobsAndDropsTheNote()
    {
        using var handler = Stub("metrics-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListMetricsAsync(
            client,
            ToolTestHost.CreateOptions(),
            includeDataMetrics: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Metrics.Count, Is.EqualTo(155));
        Assert.That(result.Metrics, Has.Some.Matches<MetricSummary>(metric => metric!.Key == "ncloc_data"));
        Assert.That(result.Note, Is.Null);
    }

    /// <summary>
    /// The filter is a substring over key, name <em>and</em> description. The phrase below appears
    /// in no key and no name, so matching on it is proof that descriptions are searched — which is
    /// the half of the filter a caller cannot get from the API's own (non-existent) <c>q</c>.
    /// </summary>
    /// <remarks>
    /// Worth knowing: the catalogue spells this family "Duplicated", never "Duplication" (outside
    /// the DATA-typed <c>duplications_data</c>), and the <c>domain</c> is not searched — so the
    /// query "duplication" finds nothing once the DATA metrics are filtered out. The <c>domain</c>
    /// parameter is the way to ask that question, and <c>listMetrics</c> exposes it.
    /// </remarks>
    [Test]
    public async Task TheMetricQueryMatchesDescriptionsAsWellAsKeys()
    {
        using var handler = Stub("metrics-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        var options = ToolTestHost.CreateOptions();

        var described = await MeasureReadTools.ListMetricsAsync(
            client,
            options,
            query: "balanced by statements",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(described.Metrics, Is.Not.Empty);
        Assert.That(described.Metrics, Has.Some.Matches<MetricSummary>(metric => metric!.Key == "duplicated_lines_density"));

        foreach (var metric in described.Metrics)
        {
            Assert.That(metric.Key, Is.Not.EqualTo("ncloc"));
        }

        using var second = Stub("metrics-search.json");
        using var secondClient = ToolTestHost.CreateClient(second);

        var byKey = await MeasureReadTools.ListMetricsAsync(
            secondClient,
            options,
            query: "duplicated",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(byKey.Metrics.Count, Is.EqualTo(7));

        foreach (var metric in byKey.Metrics)
        {
            Assert.That(metric.Domain, Is.EqualTo("Duplications"));
        }
    }

    [Test]
    public async Task TheDomainFilterIsExactAndCaseInsensitive()
    {
        using var handler = Stub("metrics-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListMetricsAsync(
            client,
            ToolTestHost.CreateOptions(),
            domain: "coverage",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Metrics, Is.Not.Empty);

        foreach (var metric in result.Metrics)
        {
            Assert.That(metric.Domain, Is.EqualTo("Coverage"));
        }

        Assert.That(result.Metrics, Has.Some.Matches<MetricSummary>(metric => metric!.Key == "coverage" && metric.HigherIsBetter == true));
    }

    // ---------------------------------------------------------------------------------------
    // getFileCoverage
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task FileCoverageAlwaysNamesBothEndsOfTheRangeItReads()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.SourceLinesWithCoverage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetFileCoverageAsync(
            client,
            ToolTestHost.CreateOptions(),
            "src/Quartz/Core/QuartzScheduler.cs",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/sources/lines"));
        Assert.That(Query(handler, "key"), Is.EqualTo(FileKey));

        // Both ends resolved even though the caller named neither: the alternative is asking for
        // every line of a file whose length is unknown.
        Assert.That(Query(handler, "from"), Is.EqualTo("1"));
        Assert.That(Query(handler, "to"), Is.EqualTo("2000"));

        Assert.That(result.From, Is.EqualTo(1));
        Assert.That(result.To, Is.EqualTo(2000));
        Assert.That(result.UncoveredLines, Is.EqualTo<int[]>([3]));
        Assert.That(result.PartiallyCoveredLines, Is.EqualTo<int[]>([2]));
        Assert.That(result.NewLines, Is.EqualTo<int[]>([1]));
        Assert.That(result.DuplicatedLines, Is.EqualTo<int[]>([3]));
    }

    [Test]
    public async Task FileCoverageReturnsOnlyTheLinesWorthWritingATestFor()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.SourceLinesWithCoverage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetFileCoverageAsync(
            client,
            ToolTestHost.CreateOptions(),
            FileKey,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Lines.Select(line => line.Line), Is.EqualTo<int[]>([2, 3]));
        Assert.That(result.Note, Is.Not.Null);
        Assert.That(result.Note, Contains.Substring("onlyUncovered=false").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task OnlyUncoveredFalseReturnsEveryLineInTheRange()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.SourceLinesWithCoverage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetFileCoverageAsync(
            client,
            ToolTestHost.CreateOptions(),
            FileKey,
            onlyUncovered: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Lines.Select(line => line.Line), Is.EqualTo<int[]>([1, 2, 3, 4, 5]));
        Assert.That(result.Note!, Contains.Substring("every line in the range").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// The source text is syntax-highlighted HTML and the agent already has the file on disk, so it
    /// is dropped — asserted on the serialised result, because "no code" is a claim about the JSON
    /// the client receives rather than about the record.
    /// </summary>
    [Test]
    public async Task FileCoverageNeverReturnsTheSourceText()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(SonarFixtures.Read("sources-lines.json"));

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetFileCoverageAsync(
            client,
            ToolTestHost.CreateOptions(),
            FileKey,
            from: 940,
            to: 945,
            onlyUncovered: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var json = JsonSerializer.Serialize(result, SonarToolJsonContext.Default.FileCoverageResult);

        // "code" as a property name, not as a substring: the deep link legitimately points at
        // /code?id=… , which is the browser page for the file.
        Assert.That(json, !Contains.Substring("\"code\"").Using(StringComparison.Ordinal));
        Assert.That(json, !Contains.Substring("<span").Using(StringComparison.Ordinal));
        Assert.That(json, !Contains.Substring("scmRevision").Using(StringComparison.Ordinal));
        Assert.That(result.Lines.Count, Is.EqualTo(6));
    }

    /// <summary>
    /// An empty <c>uncoveredLines</c> means "fully covered" only when coverage was measured at all,
    /// so the unmeasured case says so rather than letting the absence read as success.
    /// </summary>
    [Test]
    public async Task AFileWithNoCoverageDataSaysSoInsteadOfLookingFullyCovered()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.SourceLinesWithoutCoverage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetFileCoverageAsync(
            client,
            ToolTestHost.CreateOptions(),
            FileKey,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.UncoveredLines, Is.Empty);
        Assert.That(result.Note!, Contains.Substring("coverage was never measured").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// The four per-line coverage states, over one live fifteen-line window that contains all of
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The nine lines with no <c>lineHits</c> property at all are why this fixture exists: closing
    /// braces, blank lines and annotations sit inside a file SonarQube <em>did</em> measure, and they
    /// are neither covered nor uncovered. Absent is not zero here either, and a mapper that read it
    /// as zero would hand back nine lines to write tests for that no test can execute.
    /// </para>
    /// <para>
    /// Line 57 is the other trap: two conditions, none of them covered, but zero hits. It is
    /// uncovered, and it must not also be counted partial — a line nothing ran is not a line whose
    /// branches were half-taken, and listing it twice would double-count the work.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheFourPerLineCoverageStatesComeApartOnALiveWindow()
    {
        using var handler = Stub("sources-lines-with-coverage.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetFileCoverageAsync(
            client,
            ToolTestHost.CreateOptions(),
            CoverageFile,
            projectKey: CoverageProject,
            from: 43,
            to: 57,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.UncoveredLines, Is.EqualTo<int[]>([50, 57]));
        Assert.That(result.PartiallyCoveredLines, Is.EqualTo<int[]>([43, 49]));

        // Non-executable lines in a measured file: no lineHits, so neither state applies.
        int[] noData = [45, 46, 47, 48, 51, 53, 54, 55, 56];

        foreach (var line in noData)
        {
            Assert.That(result.UncoveredLines, Does.Not.Contain(line));
            Assert.That(result.PartiallyCoveredLines, Does.Not.Contain(line));
        }

        // Fully covered lines are absent from both for a different reason, and that is fine.
        int[] covered = [44, 52];

        foreach (var line in covered)
        {
            Assert.That(result.UncoveredLines, Does.Not.Contain(line));
            Assert.That(result.PartiallyCoveredLines, Does.Not.Contain(line));
        }

        // onlyUncovered defaults to true, so lines is exactly the union of the two arrays, in order.
        Assert.That(result.Lines.Select(line => line.Line), Is.EqualTo<int[]>([43, 49, 50, 57]));
    }

    /// <summary>
    /// Measured and clean, which is the one thing two empty arrays cannot say on their own.
    /// </summary>
    /// <remarks>
    /// This range and a file SonarQube never measured produce byte-identical <c>uncoveredLines</c>
    /// and <c>partiallyCoveredLines</c>, and they mean opposite things — so the note is the only
    /// place the difference can live, and it now states the good outcome rather than leaving it to
    /// be inferred from an absence.
    /// </remarks>
    [Test]
    public async Task ARangeThatIsMeasuredAndCleanSaysSoRatherThanLookingUnmeasured()
    {
        using var handler = Stub("sources-lines-fully-covered.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetFileCoverageAsync(
            client,
            ToolTestHost.CreateOptions(),
            CleanCoverageFile,
            projectKey: CoverageProject,
            from: 40,
            to: 54,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.UncoveredLines, Is.Empty);
        Assert.That(result.PartiallyCoveredLines, Is.Empty);
        Assert.That(result.Lines, Is.Empty);

        Assert.That(result.Note, Is.Not.Null);
        Assert.That(result.Note, !Contains.Substring("coverage was never measured").Using(StringComparison.Ordinal));
        Assert.That(result.Note, Contains.Substring("Coverage is measured for this file").Using(StringComparison.Ordinal));
        Assert.That(
            result.Note,
            Contains.Substring("no line in this range is uncovered or partially covered").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task ARangeWiderThanTheServerCapIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetFileCoverageAsync(
                client,
                ToolTestHost.CreateOptions(),
                FileKey,
                from: 1,
                to: 2001,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("2001 lines").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("SONARQUBE_MCP_MAX_SOURCE_LINES").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task TheLineCapFollowsTheConfiguredValue()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.SourceLinesWithCoverage);

        using var client = ToolTestHost.CreateClient(handler);
        var options = ToolTestHost.CreateOptions(maxSourceLines: 10);

        _ = await MeasureReadTools.GetFileCoverageAsync(
            client,
            options,
            FileKey,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "to"), Is.EqualTo("10"));

        _ = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetFileCoverageAsync(
                client,
                options,
                FileKey,
                from: 1,
                to: 11,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(handler.Requests, Has.Exactly(1).Items);
    }

    [TestCase(0, null, "from must be 1 or greater")]
    [TestCase(-4, null, "from must be 1 or greater")]
    [TestCase(50, 40, "to must be greater than or equal to from")]
    public async Task AnImpossibleRangeIsRefusedWithTheReason(int from, int? to, string expected)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetFileCoverageAsync(
                client,
                ToolTestHost.CreateOptions(),
                FileKey,
                from: from,
                to: to,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring(expected).Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // transitionIssue
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task TransitionIssuePostsAFormBodyAndCostsOneRequestWhenTheAnswerCarriesTransitions()
    {
        using var handler = Stub("issues-do_transition.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.TransitionIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            WriteIssueKey,
            "accept",
            comment: "Intentional here.",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests, Has.Exactly(1).Items);
        var request = handler.Requests.Single();

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/issues/do_transition"));
        Assert.That(request.Headers["Content-Type"], Is.EqualTo("application/x-www-form-urlencoded"));
        Assert.That(request.Body, Is.EqualTo($"issue={WriteIssueKey}&transition=accept&comment=Intentional+here."));

        // Live-captured: accept lands as the modern ACCEPTED plus the legacy RESOLVED/WONTFIX pair,
        // and reopen is the only transition left.
        Assert.That(result.IssueStatus, Is.EqualTo("ACCEPTED"));
        Assert.That(result.Status, Is.EqualTo("RESOLVED"));
        Assert.That(result.Resolution, Is.EqualTo("WONTFIX"));
        Assert.That(result.AvailableTransitions, Is.EqualTo<string[]>(["reopen"]));
        Assert.That(result.File, Is.EqualTo(WriteIssueFile));
    }

    /// <summary>
    /// When the operation response does not carry the new transitions, one extra read is worth it:
    /// without them the caller can only discover what it may do next by trying and failing.
    /// </summary>
    [Test]
    public async Task ATransitionResponseWithoutTransitionsCostsOneExtraRead()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.TransitionWithoutTransitions);
        handler.EnqueueJson(ToolPayloads.TransitionReread);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.TransitionIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            IssueKey,
            "resolve",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(2));
        Assert.That(RequestUrl.Path(handler.Requests[1].Uri), Is.EqualTo("/api/issues/search"));
        Assert.That(RequestUrl.QueryValue(handler.Requests[1].Uri, "additionalFields"), Is.EqualTo("transitions"));
        Assert.That(result.AvailableTransitions, Is.EqualTo<string[]>(["reopen"]));
    }

    /// <summary>
    /// The re-read has to name the issue's scope: a key alone falls back to the main branch, where a
    /// pull request's issue does not exist (C13).
    /// </summary>
    [Test]
    public async Task TheExtraReadAfterATransitionIsScopedToTheIssuesPullRequest()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.TransitionWithoutTransitions.Replace(
            "\"project\": \"quartznet_quartznet\",",
            "\"project\": \"quartznet_quartznet\", \"pullRequest\": \"3735\",",
            StringComparison.Ordinal));
        handler.EnqueueJson(ToolPayloads.TransitionReread);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.TransitionIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            IssueKey,
            "resolve",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var reread = handler.Requests[1].Uri;

        Assert.That(RequestUrl.QueryValue(reread, "componentKeys"), Is.EqualTo(Project));
        Assert.That(RequestUrl.QueryValue(reread, "pullRequest"), Is.EqualTo("3735"));
        Assert.That(RequestUrl.QueryValue(reread, "branch"), Is.Null);
        Assert.That(result.AvailableTransitions, Is.EqualTo<string[]>(["reopen"]));
    }

    [Test]
    public async Task AnUnknownTransitionExplainsWhatToCallFirstAndWhatAcceptReplaced()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.TransitionIssueAsync(
                client,
                ToolTestHost.CreateOptions(),
                IssueKey,
                "close",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("accept supersedes wontfix").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("availableTransitions").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("setHotspotStatus").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task TheLegacySpellingOfAcceptIsStillAccepted()
    {
        using var handler = Stub("issues-do_transition.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueWriteTools.TransitionIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            WriteIssueKey,
            "WontFix",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests, Has.Exactly(1).Items);
        Assert.That(handler.Requests.Single().Body, Is.EqualTo($"issue={WriteIssueKey}&transition=wontfix"));
    }

    [Test]
    public async Task ATransitionWithNoIssueKeyIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        _ = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.TransitionIssueAsync(
                client,
                ToolTestHost.CreateOptions(),
                " ",
                "accept",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // assignIssue and addIssueComment
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task AssignIssueSendsTheLoginItWasGiven()
    {
        using var handler = Stub("issues-assign.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.AssignIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            WriteIssueKey,
            "lahma@github",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests, Has.Exactly(1).Items);
        var request = handler.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/issues/assign"));
        Assert.That(request.Body, Is.EqualTo($"issue={WriteIssueKey}&assignee=lahma%40github"));
        Assert.That(result.Assignee, Is.EqualTo("lahma@github"));
        Assert.That(result.File, Is.EqualTo(WriteIssueFile));
    }

    /// <summary>
    /// Omitting <c>assignee</c> means "unassign", which is <c>assignee=</c> on the wire — omitting
    /// the parameter entirely would leave the current assignee in place.
    /// </summary>
    [Test]
    public async Task OmittingTheAssigneeUnassignsRatherThanLeavingItAlone()
    {
        using var handler = Stub("issues-assign.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueWriteTools.AssignIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            WriteIssueKey,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests, Has.Exactly(1).Items);
        Assert.That(handler.Requests.Single().Body, Is.EqualTo($"issue={WriteIssueKey}&assignee="));
    }

    [Test]
    public async Task AddIssueCommentPostsTheTextAndReportsTheCommentThatWasJustCreated()
    {
        using var handler = Stub("issues-add_comment.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.AddIssueCommentAsync(
            client,
            ToolTestHost.CreateOptions(),
            WriteIssueKey,
            WriteCommentText,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests, Has.Exactly(1).Items);
        var request = handler.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/issues/add_comment"));
        Assert.That(
            request.Body,
            Is.EqualTo($"issue={WriteIssueKey}&text=sonarqube-mcp+wire+capture+2+-+removing+shortly"));

        // The response lists every comment on the issue, so the one just posted has to be picked out
        // of two. Which one that is comes from the timestamps, not from the position — see
        // ResultMapperTests for the reversed-order proof.
        Assert.That(result.CommentKey, Is.EqualTo(WriteCommentKey));
        Assert.That(result.Text, Is.EqualTo(WriteCommentText));
        Assert.That(result.CreatedAt, Is.Not.Null);
    }

    [Test]
    public async Task AnEmptyCommentIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.AddIssueCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                IssueKey,
                "   ",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("text is required").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // setHotspotStatus
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The endpoint answers 204 with no body, so the resulting state has to be read back — which is
    /// exactly two requests, and the second is what the caller is actually told about.
    /// </summary>
    [Test]
    public async Task SettingAHotspotStatusIsAPostFollowedByAReadBack()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueNoBody(HttpStatusCode.NoContent);
        handler.EnqueueJson(SonarFixtures.Read("hotspots-show-with-comment.json"));

        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.SetHotspotStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            HotspotKey,
            "reviewed",
            "safe",
            comment: "Placeholder value.",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(2));

        Assert.That(handler.Requests[0].Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(RequestUrl.Path(handler.Requests[0].Uri), Is.EqualTo("/api/hotspots/change_status"));
        Assert.That(
            handler.Requests[0].Body,
            Is.EqualTo($"hotspot={HotspotKey}&status=REVIEWED&resolution=SAFE&comment=Placeholder+value."));

        Assert.That(handler.Requests[1].Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(RequestUrl.Path(handler.Requests[1].Uri), Is.EqualTo("/api/hotspots/show"));

        // What SonarQube stored, not what was asked for.
        Assert.That(result.Status, Is.EqualTo("REVIEWED"));
        Assert.That(result.Resolution, Is.EqualTo("SAFE"));
        Assert.That(result.File, Is.EqualTo("src/Quartz.Examples.AspNetCore/appsettings.json"));
    }

    [Test]
    public async Task ReviewedWithoutAResolutionIsRefusedWithBothOptionsExplained()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.SetHotspotStatusAsync(
                client,
                ToolTestHost.CreateOptions(),
                HotspotKey,
                "REVIEWED",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("status=REVIEWED needs a resolution").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("FIXED when the risky code was changed").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task ToReviewWithAResolutionIsRefusedAsContradictory()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.SetHotspotStatusAsync(
                client,
                ToolTestHost.CreateOptions(),
                HotspotKey,
                "TO_REVIEW",
                "SAFE",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("cannot carry a resolution").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>C2: documented for SonarQube Server, refused by Cloud — so it is refused here, with the reason.</summary>
    [Test]
    public async Task AcknowledgedIsRefusedWithTheCloudSpecificExplanation()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.SetHotspotStatusAsync(
                client,
                ToolTestHost.CreateOptions(),
                HotspotKey,
                "REVIEWED",
                "ACKNOWLEDGED",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("ACKNOWLEDGED is documented for SonarQube Server").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("use SAFE").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task AnUnknownHotspotStatusIsRefusedWithTheTwoThatExist()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.SetHotspotStatusAsync(
                client,
                ToolTestHost.CreateOptions(),
                HotspotKey,
                "CLOSED",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("TO_REVIEW or REVIEWED").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>A stub primed with one golden fixture.</summary>
    // ---------------------------------------------------------------------------------------
    // getAnalysisStatus
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task GetAnalysisStatusReportsAFinishedAnalysisAndTheScopeItWasFor()
    {
        using var handler = Stub("ce-component.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.GetAnalysisStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/ce/component"));
        Assert.That(Query(handler, "component"), Is.EqualTo(Project));

        // The endpoint takes no scope of its own; each task reports the one it analysed.
        Assert.That(Query(handler, "branch"), Is.Null);
        Assert.That(Query(handler, "pullRequest"), Is.Null);

        Assert.That(result.AnalysisInProgress, Is.False);
        Assert.That(result.Pending, Is.Empty);
        Assert.That(result.Latest, Is.Not.Null);
        Assert.That(result.Latest!.Status, Is.EqualTo("SUCCESS"));
        Assert.That(result.Latest.PullRequest, Is.EqualTo("3756"));

        // Cloud spells the finish time executedAt; the published example calls it finishedAt.
        Assert.That(result.Latest.FinishedAt, Is.Not.Null);
        Assert.That(result.Note, Contains.Substring("pull request 3756").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// A failed analysis is the case worth getting right: every measure and gate for that scope is
    /// stale rather than merely bad, and this is the only place SonarQube's reason is visible.
    /// </summary>
    [Test]
    public async Task GetAnalysisStatusSurfacesAFailedAnalysisAndItsErrorMessage()
    {
        using var handler = Stub("ce-component-failed.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.GetAnalysisStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Latest?.Status, Is.EqualTo("FAILED"));
        Assert.That(result.Latest?.ErrorMessage, Is.EqualTo("the error message"));
        Assert.That(result.Note, Contains.Substring("FAILED").Using(StringComparison.Ordinal));
        Assert.That(result.Note, Contains.Substring("the error message").Using(StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // summarizeIssues
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The whole point of the tool, against a live capture: one request, no issues fetched, and the
    /// groupings returned in the order they were asked for rather than the order SonarQube listed.
    /// </summary>
    [Test]
    public async Task SummarizeIssuesAsksForOneIssueAndReturnsTheGroupingsInTheOrderRequested()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["rules", "files", "impactSeverities"],
            impactSeverities: ["BLOCKER"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/issues/search"));
        Assert.That(Query(handler, "ps"), Is.EqualTo("1"));
        Assert.That(Query(handler, "facets"), Is.EqualTo("rules,fileUuids,impactSeverities"));
        Assert.That(Query(handler, "additionalFields"), Is.EqualTo("rules"));
        Assert.That(Query(handler, "facetMode"), Is.Null);

        Assert.That(result.Facets.Select(facet => facet.GroupBy), Is.EqualTo<string?[]>(["rules", "files", "impactSeverities"]));
        Assert.That(result.CountedIn, Is.EqualTo("issues"));
        Assert.That(result.MatchingIssues, Is.EqualTo(80));
    }

    /// <summary>
    /// A file grouping must never hand back the UUID SonarQube reports. The join comes from the
    /// response's own component sidecar, so it costs no extra request.
    /// </summary>
    [Test]
    public async Task SummarizeIssuesReportsFilesAsPathsRatherThanComponentUuids()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["files"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Facets, Has.Exactly(1).Items);
        var files = result.Facets.Single();

        Assert.That(files.Buckets, Is.Not.Empty);

        foreach (var bucket in files.Buckets)
        {
            Assert.That(bucket.Value, Is.Not.Null);
            Assert.That(bucket.Value, !Contains.Substring("AYlo").Using(StringComparison.Ordinal));
            Assert.That(bucket.Value, Contains.Substring("/").Using(StringComparison.Ordinal));
        }

        // Largest first, so the worst file is the first thing read.
        Assert.That(files.Buckets[0].Count >= files.Buckets[^1].Count, Is.True);
    }

    /// <summary>A rule key on its own is unreadable; the sidecar is what makes the ranking mean something.</summary>
    [Test]
    public async Task SummarizeIssuesLabelsRuleKeysWithTheirTitles()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["rules"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Facets, Has.Exactly(1).Items);
        var rules = result.Facets.Single();

        Assert.That(rules.Buckets, Has.Some.Matches<IssueFacetBucket>(bucket => !string.IsNullOrEmpty(bucket!.Label)));

        foreach (var bucket in rules.Buckets)
        {
            Assert.That(bucket.Value!, Contains.Substring(":").Using(StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The note has to say this or the numbers mislead: SonarQube computes each facet with its own
    /// filter removed, so a severity-filtered search still reports every severity's project-wide
    /// count. Verified live 2026-09-09 — filtering to BLOCKER returned totalCount 80 alongside an
    /// impactSeverities facet whose MEDIUM bucket was in the thousands.
    /// </summary>
    [Test]
    public async Task SummarizeIssuesWarnsThatAGroupingIgnoresItsOwnFilter()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["impactSeverities"],
            impactSeverities: ["BLOCKER"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Facets, Has.Exactly(1).Items);
        var severities = result.Facets.Single();
        var buckets = severities.Buckets.ToDictionary(bucket => bucket.Value!, bucket => bucket.Count);

        Assert.That(result.MatchingIssues, Is.EqualTo(80));
        Assert.That(
            buckets["MEDIUM"] > result.MatchingIssues,
            Is.True,
            "The fixture must still show a facet bucket larger than the filtered total.");

        Assert.That(result.Note, Contains.Substring("own filter removed").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task SummarizeIssuesScopesToAPullRequestAndCountsEffortWhenAsked()
    {
        using var handler = Stub("issues-search-facets-pullrequest.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            pullRequest: "3735",
            groupBy: ["rules"],
            countBy: "effort",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "pullRequest"), Is.EqualTo("3735"));
        Assert.That(Query(handler, "facetMode"), Is.EqualTo("effort"));
        Assert.That(result.PullRequest, Is.EqualTo("3735"));
        Assert.That(result.CountedIn, Is.EqualTo("remediationMinutes"));
        Assert.That(result.Note, Contains.Substring("remediation minutes").Using(StringComparison.Ordinal));
    }

    /// <summary>Defaults are the three questions a triage always asks first.</summary>
    [Test]
    public async Task SummarizeIssuesDefaultsToSeverityRuleAndFileGroupings()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "facets"), Is.EqualTo("impactSeverities,rules,fileUuids"));
    }

    /// <summary>
    /// <c>files</c> is the tool's word and <c>fileUuids</c> is the API's; the API rejects the first
    /// and this server rejects the second, so one word means one thing at the tool boundary.
    /// </summary>
    [TestCase("fileUuids", "files")]
    [TestCase("severities", "impactSeverities")]
    [TestCase("types", "impactSoftwareQualities")]
    [TestCase("statuses", "issueStatuses")]
    public async Task SummarizeIssuesRejectsALegacyGroupingAndNamesTheModernOne(string legacy, string modern)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SummarizeIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                groupBy: [legacy],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring(modern).Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>Whatever case a grouping is asked for in, the result echoes one canonical spelling.</summary>
    [Test]
    public async Task SummarizeIssuesEchoesOneCanonicalGroupingSpellingWhateverCaseWasAsked()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["FILES", "Rules", "files"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        // Duplicates collapse, and the wire spelling is the API's rather than the tool's.
        Assert.That(result.Facets.Select(facet => facet.GroupBy), Is.EqualTo<string?[]>(["files", "rules"]));
        Assert.That(Query(handler, "facets"), Is.EqualTo("fileUuids,rules"));
    }

    [Test]
    public async Task SummarizeIssuesRefusesAnUnknownGroupingWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SummarizeIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                groupBy: ["nonsense"],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("nonsense").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("directories").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task SummarizeIssuesRefusesAnUnknownCountModeWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SummarizeIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                countBy: "minutes",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("issues").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("effort").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // getIssueChangelog
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task GetIssueChangelogReadsTheKeyAloneAndReportsEachFieldThatMoved()
    {
        using var handler = Stub("issues-changelog.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetIssueChangelogAsync(
            client,
            ToolTestHost.CreateOptions(),
            IssueKey,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/issues/changelog"));
        Assert.That(Query(handler, "issue"), Is.EqualTo(IssueKey));

        // Not scope-sensitive, unlike issues/search: the key alone resolves.
        Assert.That(Names(handler), Is.EqualTo<string[]>(["issue"]));

        Assert.That(result.Entries.Count, Is.EqualTo(2));
        Assert.That(result.Entries[0].Author, Is.EqualTo("lahma@github"));
        Assert.That(
            result.Entries[0].Changes.Select(change => change.Field),
            Is.EqualTo<string?[]>(["resolution", "status"]));
        Assert.That(result.Entries[0].Changes[1].From, Is.EqualTo("OPEN"));
        Assert.That(result.Entries[0].Changes[1].To, Is.EqualTo("RESOLVED"));
        Assert.That(result.Note, Is.Null);
    }

    /// <summary>
    /// An empty changelog from a tokenless server is not evidence that nothing happened: SonarQube
    /// answers an anonymous request with an empty list rather than a 401 (the same restriction that
    /// empties a rule's description sections).
    /// </summary>
    [Test]
    public async Task AnEmptyChangelogWithoutATokenSaysItCouldNotBeReadRatherThanThatNothingHappened()
    {
        using var handler = Stub("issues-changelog-anonymous.json");
        using var client = ToolTestHost.CreateAnonymousClient(handler);

        var result = await IssueReadTools.GetIssueChangelogAsync(
            client,
            ToolTestHost.CreateOptions(token: null),
            IssueKey,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Entries, Is.Empty);
        Assert.That(result.Note, Contains.Substring("SONARQUBE_TOKEN").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task AnEmptyChangelogWithATokenSaysNobodyHasChangedTheIssue()
    {
        using var handler = Stub("issues-changelog-anonymous.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetIssueChangelogAsync(
            client,
            ToolTestHost.CreateOptions(),
            IssueKey,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Entries, Is.Empty);
        Assert.That(result.Note, !Contains.Substring("SONARQUBE_TOKEN").Using(StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // bulkUpdateIssues
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task BulkUpdateIssuesSendsOneFormBodyAndReportsWhatDidNotMove()
    {
        using var handler = Stub("issues-bulk-change.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.BulkUpdateIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            [IssueKey, WriteIssueKey],
            transition: "accept",
            comment: "handled in bulk",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Path(handler), Is.EqualTo("/api/issues/bulk_change"));
        Assert.That(handler.Requests[0].Method, Is.EqualTo(HttpMethod.Post));

        var body = handler.Requests[0].Body!;

        Assert.That(body, Contains.Substring("issues=" + IssueKey + "%2C" + WriteIssueKey).Using(StringComparison.Ordinal));
        Assert.That(body, Contains.Substring("do_transition=accept").Using(StringComparison.Ordinal));
        Assert.That(body, !Contains.Substring("assign=").Using(StringComparison.Ordinal));

        Assert.That(result.Total, Is.EqualTo(2));
        Assert.That(result.Changed, Is.EqualTo(1));
        Assert.That(result.Ignored, Is.EqualTo(1));
        Assert.That(result.Applied, Is.EqualTo<string[]>(["transition accept", "comment"]));
        Assert.That(result.Note, Contains.Substring("availableTransitions").Using(StringComparison.Ordinal));
    }

    /// <summary>A call with only keys would report counts for a change nobody asked for.</summary>
    [Test]
    public async Task BulkUpdateIssuesWithNoActionIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.BulkUpdateIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                [IssueKey],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("at least one of transition, assignee or comment").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task BulkUpdateIssuesWithNoKeysIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.BulkUpdateIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                [],
                transition: "accept",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("issueKeys is required").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>The endpoint's own cap, enforced before the request rather than as a 400.</summary>
    [Test]
    public async Task BulkUpdateIssuesRefusesMoreKeysThanTheEndpointAcceptsWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var keys = Enumerable.Range(0, ToolDefaults.MaxBulkIssueKeys + 1)
            .Select(index => "AZ_key" + index)
            .ToArray();

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.BulkUpdateIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                keys,
                transition: "accept",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception!.Message, Contains.Substring("batches").Using(StringComparison.Ordinal));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>Empty means unassign here too, the same way it does on <c>assignIssue</c>.</summary>
    [Test]
    public async Task BulkUpdateIssuesSendsAnEmptyAssigneeToUnassign()
    {
        using var handler = Stub("issues-bulk-change.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.BulkUpdateIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            [IssueKey],
            assignee: string.Empty,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests[0].Body!, Contains.Substring("assign=").Using(StringComparison.Ordinal));
        Assert.That(result.Applied, Is.EqualTo<string[]>(["unassign"]));
    }

    // ---------------------------------------------------------------------------------------
    // Anonymity is diagnosed where it bites (issue #1)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// An empty project list from a tokenless server looks exactly like confirmation that a project
    /// key was wrong. It is not — it is the same anonymity that made the key look wrong.
    /// </summary>
    [Test]
    public async Task AnEmptyProjectListFromATokenlessServerSaysItCouldOnlySeePublicProjects()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"paging":{"pageIndex":1,"pageSize":50,"total":0},"components":[]}""");
        using var client = ToolTestHost.CreateAnonymousClient(handler);

        var result = await ProjectReadTools.ListProjectsAsync(
            client,
            ToolTestHost.CreateOptions(token: null),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Projects, Is.Empty);
        Assert.That(result.Note, Contains.Substring("SONARQUBE_TOKEN").Using(StringComparison.Ordinal));
        Assert.That(result.Note, Contains.Substring("not evidence").Using(StringComparison.Ordinal));
    }

    /// <summary>With a token, an empty list means what it says and carries no such excuse.</summary>
    [Test]
    public async Task AnEmptyProjectListFromAnAuthenticatedServerCarriesNoAnonymityNote()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"paging":{"pageIndex":1,"pageSize":50,"total":0},"components":[]}""");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.ListProjectsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Projects, Is.Empty);
        Assert.That(result.Note, Is.Null);
    }

    /// <summary>
    /// SonarQube reports a private project to an anonymous caller as "Project doesn't exist", which
    /// reads as a wrong key. The 404 has to name the missing credential or the reader goes hunting
    /// for a key that was right all along.
    /// </summary>
    [Test]
    public async Task A404FromATokenlessServerNamesTheMissingTokenAsAPossibleCause()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(
            """{"errors":[{"msg":"Component key 'x' not found"}]}""",
            HttpStatusCode.NotFound);

        using var client = ToolTestHost.CreateAnonymousClient(handler);

        ToolErrors.UseOptions(ToolTestHost.CreateOptions(token: null));

        try
        {
            var exception = await Assert.ThrowsAsync<McpException>(() =>
                ProjectReadTools.ListBranchesAsync(
                    client,
                    ToolTestHost.CreateOptions(token: null),
                    cancellationToken: TestContext.CurrentContext.CancellationToken));

            Assert.That(exception!.Message, Contains.Substring("no SONARQUBE_TOKEN is configured").Using(StringComparison.Ordinal));
            Assert.That(exception.Message, Contains.Substring("indistinguishable").Using(StringComparison.Ordinal));
        }
        finally
        {
            ToolErrors.UseOptions(ToolTestHost.CreateOptions());
        }
    }

    /// <summary>
    /// Once SonarQube Cloud has migrated a project's hotspots into former-hotspot issues, this endpoint
    /// answers with nothing, which must not read as "no security findings".
    /// </summary>
    [Test]
    public async Task AnEmptyHotspotSearchPointsAtTheMigratedIssues()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"paging":{"pageIndex":1,"pageSize":50,"total":0},"hotspots":[],"components":[]}""");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SearchHotspotsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Hotspots, Is.Empty);
        Assert.That(result.Note, Contains.Substring("tags=former-hotspot").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task SummarizeIssuesGroupsBySecurityStandardUnderTheirOwnFacetNames()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["owaspTop10-2021", "CWE", "sonarsourceSecurity"],
            authors: ["ada@example.com"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Query(handler, "facets"), Is.EqualTo("owaspTop10-2021,cwe,sonarsourceSecurity"));
        Assert.That(Query(handler, "author"), Is.EqualTo("ada@example.com"));
        Assert.That(
            result.Facets.Select(facet => facet.GroupBy),
            Is.EqualTo<string[]>(["owaspTop10-2021", "cwe", "sonarsourceSecurity"]));
    }

    private static StubHttpMessageHandler Stub(string fixture)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(SonarFixtures.Read(fixture));
        return handler;
    }

    private static string Path(StubHttpMessageHandler handler, int index = 0) =>
        RequestUrl.Path(handler.Requests[index].Uri);

    private static string? Query(StubHttpMessageHandler handler, string name, int index = 0) =>
        RequestUrl.QueryValue(handler.Requests[index].Uri, name);

    private static List<string> Names(StubHttpMessageHandler handler, int index = 0) =>
        RequestUrl.QueryNames(handler.Requests[index].Uri);

    /// <summary>A metric list of the requested length, all distinct so nothing is deduplicated away.</summary>
    private static string[] MetricKeys(int count)
    {
        var keys = new string[count];

        for (var index = 0; index < count; index++)
        {
            keys[index] = "metric_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return keys;
    }
}
