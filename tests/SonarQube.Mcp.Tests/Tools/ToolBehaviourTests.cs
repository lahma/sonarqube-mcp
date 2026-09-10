using System.Net;
using System.Text.Json;

using ModelContextProtocol;

using SonarQube.Mcp.Tests.Http;
using SonarQube.Mcp.Tools;
using SonarQube.Mcp.Tools.Models;

using Xunit;

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

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/components/search", Path(handler));
        Assert.Equal("quartznet", Query(handler, "organization"));
        Assert.Equal("TRK", Query(handler, "qualifiers"));
        Assert.Equal("quartz", Query(handler, "q"));
        Assert.Equal("2", Query(handler, "p"));
        Assert.Equal("25", Query(handler, "ps"));

        var project = Assert.Single(result.Projects);

        Assert.Equal(Project, project.Key);
        Assert.Equal("https://sonarcloud.io/project/overview?id=quartznet_quartznet", project.Url);
    }

    [Fact]
    public async Task ListProjectsWithoutAnOrganizationNamesTheVariableAndSendsNothing()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListProjectsAsync(
                client,
                ToolTestHost.CreateOptions(organization: null),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("SONARQUBE_ORG", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Clamping is silent: the binding constraint is the model's context rather than the API's
    /// ceiling, so a caller asking for 500 rows is answered with fewer rather than refused.
    /// </summary>
    [Theory]
    [InlineData(null, "50")]
    [InlineData(0, "1")]
    [InlineData(-7, "1")]
    [InlineData(25, "25")]
    [InlineData(100, "100")]
    [InlineData(500, "100")]
    [InlineData(10_000, "100")]
    public async Task PageSizeIsClampedIntoTheConfiguredRange(int? requested, string expected)
    {
        using var handler = Stub("components-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await ProjectReadTools.ListProjectsAsync(
            client,
            ToolTestHost.CreateOptions(),
            pageSize: requested,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, Query(handler, "ps"));
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData(0, "1")]
    [InlineData(-3, "1")]
    [InlineData(4, "4")]
    public async Task PageIsAlwaysAtLeastOne(int? requested, string expected)
    {
        using var handler = Stub("components-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await ProjectReadTools.ListProjectsAsync(
            client,
            ToolTestHost.CreateOptions(),
            page: requested,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, Query(handler, "p"));
    }

    /// <summary>
    /// The 10,000-result window is not clamped, because there is no smaller answer to give: the
    /// requested page does not exist and never will. It is refused before a request is made.
    /// </summary>
    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("10000", exception.Message, StringComparison.Ordinal);
        Assert.Contains("narrow the search", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // listComponents
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ListComponentsDefaultsToTheFilesInTheWholeProject()
    {
        using var handler = Stub("components-tree-leaves.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.ListComponentsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/components/tree", Path(handler));
        Assert.Equal(Project, Query(handler, "component"));
        Assert.Equal("leaves", Query(handler, "strategy"));
        Assert.Equal("FIL,UTS", Query(handler, "qualifiers"));

        Assert.Equal(3, result.Components.Count);
        Assert.Equal(".github/ISSUE_TEMPLATE/01_bug_report.yml", result.Components[0].Path);
        Assert.True(result.HasMore);
        Assert.Equal(1048, result.TotalCount);
    }

    [Theory]
    [InlineData("files", "leaves", "FIL,UTS")]
    [InlineData("directories", "children", "DIR")]
    [InlineData("all", "all", null)]
    public async Task ScopeChoosesTheStrategyAndQualifierPair(string scope, string strategy, string? qualifiers)
    {
        using var handler = Stub("components-tree-leaves.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await ProjectReadTools.ListComponentsAsync(
            client,
            ToolTestHost.CreateOptions(),
            scope: scope,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(strategy, Query(handler, "strategy"));
        Assert.Equal(qualifiers, Query(handler, "qualifiers"));
    }

    [Fact]
    public async Task AnUnknownScopeIsRefusedWithTheThreeThatWork()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListComponentsAsync(
                client,
                ToolTestHost.CreateOptions(),
                scope: "everything",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("files, directories or all", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The pair is mutually exclusive on every endpoint that takes it, so it is validated once — and
    /// before the request, because SonarQube answers the combination with an opaque 400.
    /// </summary>
    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("mutually exclusive", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AProjectKeyArgumentBeatsTheEnvironmentDefault()
    {
        using var handler = Stub("components-tree-leaves.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await ProjectReadTools.ListComponentsAsync(
            client,
            ToolTestHost.CreateOptions(),
            projectKey: "other_project",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("other_project", Query(handler, "component"));
    }

    [Fact]
    public async Task WithNoProjectAnywhereTheErrorNamesTheVariableAndListProjects()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListComponentsAsync(
                client,
                ToolTestHost.CreateOptions(defaultProject: null),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("SONARQUBE_MCP_DEFAULT_PROJECT", exception.Message, StringComparison.Ordinal);
        Assert.Contains("listProjects", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // listBranches, listPullRequests, getQualityGateStatus
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ListBranchesAsksTheUnpaginatedEndpointAndMarksTheMainBranch()
    {
        using var handler = Stub("project-branches-list.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.ListBranchesAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/project_branches/list", Path(handler));
        Assert.Equal(["project"], Names(handler));

        var branch = Assert.Single(result.Branches);

        Assert.Equal("main", branch.Name);
        Assert.True(branch.IsMain);
        Assert.Equal(140, branch.Bugs);
        Assert.Equal("a8dc2ec41b298390fb1f48ca3f2ef4d4d85128eb", branch.CommitSha);
        Assert.Equal(
            "https://sonarcloud.io/project/overview?id=quartznet_quartznet&branch=main",
            branch.Url);
    }

    [Fact]
    public async Task ListPullRequestsReportsTheKeyToPassBackAndTheGateStatus()
    {
        using var handler = Stub("project-pull-requests-list.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/project_pull_requests/list", Path(handler));
        Assert.Equal(2, result.PullRequests.Count);

        var failing = result.PullRequests[1];

        Assert.Equal("3267", failing.Key);
        Assert.Equal("main", failing.Base);
        Assert.Equal("ERROR", failing.QualityGateStatus);
        Assert.Equal("https://github.com/quartznet/quartznet/pull/3267", failing.ScmUrl);
        Assert.Equal(
            "https://sonarcloud.io/project/overview?id=quartznet_quartznet&pullRequest=3267",
            failing.Url);
    }

    [Fact]
    public async Task AQualityGateWithNoVerdictYetIsExplainedRatherThanReportedAsAFailure()
    {
        using var handler = Stub("qualitygates-project-status-none.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.GetQualityGateStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("NONE", result.Status);
        Assert.Empty(result.Conditions);
        Assert.NotNull(result.Note);
        Assert.Contains("not an error", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingGateArrivesWithItsFailuresAlreadyFilteredAndItsRatingsAsLetters()
    {
        using var handler = Stub("qualitygates-project-status-error.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.GetQualityGateStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            pullRequest: "3267",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("3267", Query(handler, "pullRequest"));
        Assert.Equal("ERROR", result.Status);
        Assert.Equal(5, result.Conditions.Count);
        Assert.Equal(2, result.FailingConditions.Count);

        var rating = result.FailingConditions[0];

        Assert.Equal("new_reliability_rating", rating.Metric);
        Assert.Equal("A", rating.Threshold);
        Assert.Equal("B", rating.ActualValue);
        Assert.True(rating.OnNewCode);

        // A non-rating metric keeps the number SonarQube sent.
        Assert.Equal("4.8", result.FailingConditions[1].ActualValue);
        Assert.Null(result.Note);
    }

    // ---------------------------------------------------------------------------------------
    // searchIssues
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SearchIssuesDefaultsToTheWorkThatIsStillOutstanding()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/issues/search", Path(handler));
        Assert.Equal("OPEN,CONFIRMED", Query(handler, "issueStatuses"));
        Assert.Equal(Project, Query(handler, "componentKeys"));

        // Always sent: the sidecar the file-path join needs, and never _all, which drags a
        // fifty-element languages array onto every response.
        Assert.Equal("rules", Query(handler, "additionalFields"));

        Assert.Equal("CREATION_DATE", Query(handler, "s"));
        Assert.Equal("false", Query(handler, "asc"));
    }

    [Fact]
    public async Task AnExplicitStatusListReplacesTheDefaultRatherThanAddingToIt()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            issueStatuses: ["accepted", "FIXED", "ACCEPTED"],
            cancellationToken: TestContext.Current.CancellationToken);

        // Upper-cased and deduplicated, in the order the caller gave them.
        Assert.Equal("ACCEPTED,FIXED", Query(handler, "issueStatuses"));
    }

    /// <summary>
    /// <c>asc</c> is sent on every call, including the default. It is a plain <c>bool</c> rather
    /// than <c>bool?</c> precisely so the sort direction is never left to the endpoint's own
    /// default, which is ascending — the opposite of what a triage list wants.
    /// </summary>
    [Fact]
    public async Task TheSortDirectionIsAlwaysStatedExplicitly()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            sortBy: "severity",
            ascending: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("SEVERITY", Query(handler, "s"));
        Assert.Equal("true", Query(handler, "asc"));
    }

    [Theory]
    [InlineData("src/Widget.cs", "quartznet_quartznet:src/Widget.cs")]
    [InlineData("quartznet_quartznet:src/Widget.cs", "quartznet_quartznet:src/Widget.cs")]
    [InlineData("src\\Widget.cs", "quartznet_quartznet:src/Widget.cs")]
    [InlineData("./src/Widget.cs", "quartznet_quartznet:src/Widget.cs")]
    [InlineData("/src/Widget.cs", "quartznet_quartznet:src/Widget.cs")]
    [InlineData("quartznet_quartznet", "quartznet_quartznet")]
    public async Task AComponentIsAcceptedAsAPathOrAsAKey(string component, string expected)
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            component: component,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, Query(handler, "componentKeys"));
    }

    [Fact]
    public async Task AComponentThatNormalisesToNothingIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                component: "./",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("listComponents", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheNewCodeFilterIsSentUnderTheNameTheApiUses()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            inNewCodePeriod: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("true", Query(handler, "sinceLeakPeriod"));
        Assert.DoesNotContain("inNewCodePeriod", Names(handler));
    }

    /// <summary>
    /// The legacy vocabularies are rejected <em>with the translation</em>. A model trained before
    /// 2024 reaches for MAJOR and CODE_SMELL, and SonarQube's own 400 lists the accepted values
    /// without saying which of them means what was asked for.
    /// </summary>
    [Fact]
    public async Task ALegacySeverityIsRejectedWithItsModernEquivalent()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                impactSeverities: ["MAJOR"],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("MAJOR is now MEDIUM", exception.Message, StringComparison.Ordinal);
        Assert.Contains("INFO, LOW, MEDIUM, HIGH, BLOCKER", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ALegacyIssueTypeIsRejectedWithItsCleanCodeEquivalent()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                impactSoftwareQualities: ["CODE_SMELL"],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("CODE_SMELL is now MAINTAINABILITY", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ALegacyStatusIsRejectedWithTheThreeStatusesItSplitInto()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                issueStatuses: ["RESOLVED"],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("RESOLVED split into FALSE_POSITIVE, ACCEPTED and FIXED", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("mutually exclusive", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("7 days")]
    [InlineData("d")]
    [InlineData("7x")]
    public async Task AMalformedRelativeWindowNamesTheFormsThatWork(string createdInLast)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                createdInLast: createdInLast,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("7d, 2w, 1m, 1y", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnUnsortableFieldIsRefusedWithTheSevenThatAreSortable()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                sortBy: "RANDOM",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("CREATION_DATE", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The single biggest context saving in the tool layer: the model sees
    /// <c>src/…/QuartzScheduler.cs</c> rather than <c>quartznet_quartznet:src/…/QuartzScheduler.cs</c>
    /// repeated on every row, with the key still available when it needs one back.
    /// </summary>
    [Fact]
    public async Task IssuesComeBackWithTheirFilePathJoinedFromTheComponentsSidecar()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(result.Issues, issue => Assert.False(string.IsNullOrEmpty(issue.File)));
        Assert.All(result.Issues, issue => Assert.DoesNotContain(':', issue.File!));

        var second = result.Issues[1];

        Assert.Equal("src/Quartz/Core/QuartzScheduler.cs", second.File);
        Assert.Equal(FileKey, second.Component);
        Assert.Equal(943, second.Line);
        Assert.Equal("MAINTAINABILITY", Assert.Single(second.Impacts).SoftwareQuality);
        Assert.Equal(1606, result.TotalCount);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task AResultSetLargerThanTheWindowCarriesTheWarningBeforeTheCallerPagesIntoIt()
    {
        using var handler = Stub("issues-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SearchIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        // 1606 results is under the cap, so there is nothing to warn about.
        Assert.Null(result.Note);
    }

    // ---------------------------------------------------------------------------------------
    // getIssue
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetIssueAsksTheSearchEndpointForOneKeyWithItsTransitionsAndComments()
    {
        using var handler = Stub("issues-search-single.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            IssueKey,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/issues/search", Path(handler));
        Assert.Equal(IssueKey, Query(handler, "issues"));
        Assert.Equal("transitions,comments,rules,users", Query(handler, "additionalFields"));
        Assert.Equal("1", Query(handler, "ps"));

        Assert.Equal(IssueKey, result.Key);
        Assert.Equal("src/Quartz/Core/QuartzScheduler.cs", result.File);
        Assert.Equal(
            "Null-forgiving operators should not be used when nullable warnings are disabled",
            result.RuleName);
        Assert.Empty(result.AvailableTransitions);
        Assert.Equal(
            "https://sonarcloud.io/project/issues?id=quartznet_quartznet&issues=AZ_xePOumT_q4T_1FWf8&open=AZ_xePOumT_q4T_1FWf8",
            result.Url);
    }

    [Fact]
    public async Task AnIssueKeyThatMatchesNothingSaysWhereToLookInstead()
    {
        using var handler = Stub("issues-search-empty.json");
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetIssueAsync(
                client,
                ToolTestHost.CreateOptions(),
                "AZ_nosuchissue",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("AZ_nosuchissue", exception.Message, StringComparison.Ordinal);
        Assert.Contains("searchIssues", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlankIssueKeyIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetIssueAsync(
                client,
                ToolTestHost.CreateOptions(),
                "   ",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("issueKey is required", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
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
    [Theory]
    [InlineData(null, null)]
    [InlineData("release/4.0", null)]
    [InlineData(null, "3735")]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ToolTestHost.Project, Query(handler, "componentKeys"));
        Assert.Equal(IssueKey, Query(handler, "issues"));
        Assert.Equal(branch, Query(handler, "branch"));
        Assert.Equal(pullRequest, Query(handler, "pullRequest"));
    }

    /// <summary>An explicit <c>projectKey</c> wins over the configured default, as everywhere else.</summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("otherorg_otherrepo", Query(handler, "componentKeys"));
    }

    /// <summary>
    /// With no project to name, a scoped lookup cannot be honoured — so it is refused here rather
    /// than sent and reported back as "no such issue", which is what the API would make it look like.
    /// A main-branch read with no project stays legal, because that one genuinely works.
    /// </summary>
    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("projectKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ToolDefaults.DefaultProjectVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains("3735", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Unscoped and with nothing configured, the key alone still resolves on the main branch.</summary>
    [Fact]
    public async Task GetIssueWithNoProjectAnywhereStillReadsAMainBranchIssue()
    {
        using var handler = Stub("issues-search-single.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetIssueAsync(
            client,
            ToolTestHost.CreateOptions(defaultProject: null),
            IssueKey,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(Query(handler, "componentKeys"));
        Assert.Equal(IssueKey, result.Key);
    }

    /// <summary>
    /// A hotspot key has the same shape as an issue key and resolves to nothing here, so the message
    /// has to name the tool that does read it. Without that the model retries getIssue.
    /// </summary>
    [Fact]
    public async Task AnIssueKeyThatMatchesNothingPointsAtGetHotspotAsWellAsSearchIssues()
    {
        using var handler = Stub("issues-search-empty.json");
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetIssueAsync(
                client,
                ToolTestHost.CreateOptions(),
                "AZ_nosuchissue",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("getHotspot", exception.Message, StringComparison.Ordinal);
        Assert.Contains("projectKey", exception.Message, StringComparison.Ordinal);
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
    [Fact]
    public async Task GetRuleReturnsTheRequestedSectionsInTheOrderTheyAreRead()
    {
        using var handler = Stub("rules-search-with-sections.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "csharpsquid:S2259",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/rules/search", Path(handler));
        Assert.Equal("csharpsquid:S2259", Query(handler, "rule_key"));
        Assert.Equal("quartznet", Query(handler, "organization"));
        Assert.Equal("1", Query(handler, "ps"));
        Assert.Contains("descriptionSections", Query(handler, "f")!, StringComparison.Ordinal);

        // The capture lists how_to_fix first; the requested order wins, and the introduction the
        // defaults ask for is not one this rule has.
        Assert.Equal(["root_cause", "how_to_fix"], result.Sections.Select(section => section.Key));

        // The HTML is gone and the code sample is fenced.
        Assert.DoesNotContain("<pre", result.Sections[0].Content!, StringComparison.Ordinal);
        Assert.Contains("```", result.Sections[0].Content!, StringComparison.Ordinal);
        Assert.All(result.Sections, section => Assert.Null(section.Context));
        Assert.False(result.Truncated);
        Assert.Null(result.Note);
    }

    /// <summary>
    /// A rule with per-framework fix guidance answers <c>how_to_fix</c> once per context, and both
    /// copies are returned: the context is the only thing that distinguishes them, so dropping it
    /// would leave two identical-looking sections with different advice.
    /// </summary>
    [Fact]
    public async Task GetRuleKeepsEveryContextOfAPerFrameworkSection()
    {
        using var handler = Stub("rules-search-with-contexts.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "javasecurity:S2076",
            sections: ["how_to_fix"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["how_to_fix", "how_to_fix"], result.Sections.Select(section => section.Key));

        // The display name, not the key: it is what the section header would read in the UI.
        Assert.Equal(
            ["Java Lang Package", "Apache Commons"],
            result.Sections.Select(section => section.Context));
    }

    [Fact]
    public async Task GetRuleReturnsOnlyTheSectionsThatWereAskedFor()
    {
        using var handler = Stub("rules-search-with-sections.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "csharpsquid:S2259",
            sections: ["resources"],
            cancellationToken: TestContext.Current.CancellationToken);

        var section = Assert.Single(result.Sections);

        Assert.Equal("resources", section.Key);
        Assert.Contains("- CWE-476", section.Content!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The degraded case, captured live and anonymously: name, impacts and clean-code attribute
    /// arrive, descriptions do not. Half a rule is still worth having, so it is a note rather than
    /// an exception.
    /// </summary>
    [Fact]
    public async Task ARuleWithNoDescriptionSectionsDegradesWithANoteRatherThanThrowing()
    {
        using var handler = Stub("rules-search-rule-key.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "csharpsquid:S2259",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Sections);
        Assert.Equal("Null pointers should not be dereferenced", result.Name);
        Assert.Equal("RELIABILITY", Assert.Single(result.Impacts).SoftwareQuality);
        Assert.NotNull(result.Note);
        Assert.Contains("SONARQUBE_TOKEN", result.Note, StringComparison.Ordinal);
    }

    /// <summary>A rule old enough to predate sections carries one HTML blob; it becomes root_cause.</summary>
    [Fact]
    public async Task ALegacyHtmlDescriptionIsReturnedAsASyntheticRootCauseSection()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.RuleWithHtmlDescOnly);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetRuleAsync(
            client,
            ToolTestHost.CreateOptions(),
            "csharpsquid:S1118",
            cancellationToken: TestContext.Current.CancellationToken);

        var section = Assert.Single(result.Sections);

        Assert.Equal("root_cause", section.Key);
        Assert.Contains("- Add a private constructor.", section.Content!, StringComparison.Ordinal);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task ARuleKeyWithoutARepositoryIsRefusedWithTheShapeThatWorks()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetRuleAsync(
                client,
                ToolTestHost.CreateOptions(),
                "S2259",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("repository:rule", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("csharpsquid:S9999", exception.Message, StringComparison.Ordinal);
        Assert.Contains("quartznet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("assess_the_problem", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // searchHotspots and getHotspot
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// C12. <c>files</c> takes the project-relative path; a component key matches nothing at all,
    /// and does it silently — an empty result rather than an error.
    /// </summary>
    [Theory]
    [InlineData("src/Quartz.Examples.AspNetCore/appsettings.json")]
    [InlineData("quartznet_quartznet:src/Quartz.Examples.AspNetCore/appsettings.json")]
    [InlineData("src\\Quartz.Examples.AspNetCore\\appsettings.json")]
    public async Task SearchHotspotsFiltersByPathWhateverSpellingTheCallerUsed(string component)
    {
        using var handler = Stub("hotspots-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchHotspotsAsync(
            client,
            ToolTestHost.CreateOptions(),
            component: component,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/hotspots/search", Path(handler));
        Assert.Equal("src/Quartz.Examples.AspNetCore/appsettings.json", Query(handler, "files"));
        Assert.Equal(Project, Query(handler, "projectKey"));
    }

    [Fact]
    public async Task WithNoComponentSearchHotspotsSendsNoFileFilterAtAll()
    {
        using var handler = Stub("hotspots-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchHotspotsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("files", Names(handler));
    }

    /// <summary>The project key is not a file filter, so it must not be sent as one.</summary>
    [Fact]
    public async Task TheProjectItselfIsNeverSentAsAFileFilter()
    {
        using var handler = Stub("hotspots-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SearchHotspotsAsync(
            client,
            ToolTestHost.CreateOptions(),
            component: Project,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("files", Names(handler));
    }

    [Fact]
    public async Task HotspotSearchReportsTheAssigneeAsTheUuidItActuallyIs()
    {
        using var handler = Stub("hotspots-search-page.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SearchHotspotsAsync(
            client,
            ToolTestHost.CreateOptions(),
            status: "TO_REVIEW",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("TO_REVIEW", Query(handler, "status"));

        var hotspot = result.Hotspots[0];

        Assert.Equal(HotspotKey, hotspot.Key);
        Assert.Equal("src/Quartz.Examples.AspNetCore/appsettings.json", hotspot.File);
        Assert.Equal("AYgE5F7pEoXHSow6lKjD", hotspot.AssigneeId);
        Assert.Equal("HIGH", hotspot.VulnerabilityProbability);
        Assert.Equal(28, result.TotalCount);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task TheHotspotSearchFilterRefusesAResolutionSonarQubeCloudDoesNotHave()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchHotspotsAsync(
                client,
                ToolTestHost.CreateOptions(),
                resolution: "ACKNOWLEDGED",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("FIXED or SAFE", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetHotspotUsesTheParameterNameTheEndpointActuallyTakes()
    {
        using var handler = Stub("hotspots-show.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetHotspotAsync(
            client,
            ToolTestHost.CreateOptions(),
            HotspotKey,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/hotspots/show", Path(handler));
        Assert.Equal(["hotspot"], Names(handler));

        // A login here, unlike the UUID the search endpoint reports under the same name (C7).
        Assert.Equal("lahma@github", result.Assignee);
        Assert.Equal("src/Quartz.Examples.AspNetCore/appsettings.json", result.File);
        Assert.False(result.CanChangeStatus);

        Assert.NotNull(result.Rule);
        Assert.Contains("Hard-coding credentials", result.Rule.RiskDescription!, StringComparison.Ordinal);
        Assert.Contains("```", result.Rule.FixRecommendations!, StringComparison.Ordinal);
        Assert.Null(result.Rule.VulnerabilityDescription);
    }

    [Fact]
    public async Task ABlankHotspotKeyIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.GetHotspotAsync(
                client,
                ToolTestHost.CreateOptions(),
                string.Empty,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("hotspotKey is required", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // getComponentMeasures and listComponentMeasures
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetComponentMeasuresAsksForPeriodsAndDiffsWhatCameBack()
    {
        using var handler = Stub("measures-component.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            NclocAndRating,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/measures/component", Path(handler));
        Assert.Equal("ncloc,sqale_rating,new_coverage", Query(handler, "metricKeys"));

        // periods, plural — the singular spelling is a 400.
        Assert.Equal("periods", Query(handler, "additionalFields"));

        Assert.Equal(Project, result.Component);
        Assert.Equal("119857", result.Measures[0].Value);

        // "1.0" means A, which is the single most confusing value in the whole API.
        Assert.Equal("A", result.Measures[1].Value);

        // Absent is not zero: the metric was requested and came back with nothing.
        Assert.Equal(["new_coverage"], result.MissingMetrics);
    }

    [Fact]
    public async Task ANewCodeValueIsReportedAsOneAndNeverAsTheOverallValue()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.MeasuresWithRatingAndPeriods);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            ["sqale_rating", "new_coverage", "new_reliability_rating"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("C", result.Measures[0].Value);
        Assert.Null(result.Measures[0].NewCodeValue);

        Assert.Null(result.Measures[1].Value);
        Assert.Equal("82.5", result.Measures[1].NewCodeValue);

        // A rating in the new-code period is still a letter.
        Assert.Equal("E", result.Measures[2].NewCodeValue);
        Assert.Empty(result.MissingMetrics);
    }

    [Fact]
    public async Task GetComponentMeasuresAcceptsTwentyFiveMetricsAndRefusesTwentySix()
    {
        using var handler = Stub("measures-component.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await MeasureReadTools.GetComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            MetricKeys(25),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetComponentMeasuresAsync(
                client,
                ToolTestHost.CreateOptions(),
                MetricKeys(26),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("26 entries", exception.Message, StringComparison.Ordinal);
        Assert.Contains("at most 25", exception.Message, StringComparison.Ordinal);
        Assert.Contains("metricKeys", exception.Message, StringComparison.Ordinal);

        // The refusal cost nothing: still the one successful request.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NoMetricKeysAtAllPointsAtListMetrics()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetComponentMeasuresAsync(
                client,
                ToolTestHost.CreateOptions(),
                [],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("metricKeys is required", exception.Message, StringComparison.Ordinal);
        Assert.Contains("listMetrics", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Fifteen is the API's own <c>maxValuesAllowed</c> on this action, not a house rule.</summary>
    [Fact]
    public async Task ListComponentMeasuresAcceptsFifteenMetricsAndRefusesSixteen()
    {
        using var handler = Stub("measures-component-tree.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            MetricKeys(15),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.ListComponentMeasuresAsync(
                client,
                ToolTestHost.CreateOptions(),
                MetricKeys(16),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("16 entries", exception.Message, StringComparison.Ordinal);
        Assert.Contains("at most 15", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// Sorting by a metric is three parameters that only mean anything together — the third is what
    /// keeps components with no value for the metric from sorting to the top of a "worst files" list.
    /// </summary>
    [Fact]
    public async Task SortingByAMetricSendsAllThreeParametersThatMakeItWork()
    {
        using var handler = Stub("measures-component-tree.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            Ncloc,
            sortByMetric: "coverage",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/measures/component_tree", Path(handler));
        Assert.Equal("metric", Query(handler, "s"));
        Assert.Equal("coverage", Query(handler, "metricSort"));
        Assert.Equal("withMeasuresOnly", Query(handler, "metricSortFilter"));
        Assert.Equal("false", Query(handler, "asc"));

        Assert.Equal(2, result.Components.Count);
        Assert.Equal("10968", result.Components[0].Measures[0].Value);
        Assert.Equal(796, result.TotalCount);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task WithoutASortMetricNoneOfTheSortParametersAreSent()
    {
        using var handler = Stub("measures-component-tree.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            Ncloc,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("s", Names(handler));
        Assert.DoesNotContain("metricSort", Names(handler));
        Assert.DoesNotContain("metricSortFilter", Names(handler));
    }

    [Fact]
    public async Task TreeMeasuresReportTheMetricsNoComponentOnThePageHad()
    {
        using var handler = Stub("measures-component-tree.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            ["ncloc", "coverage"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["coverage"], result.MissingMetrics);
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
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/measures/component_tree", Path(handler));
        Assert.Equal("metric", Query(handler, "s"));
        Assert.Equal("coverage", Query(handler, "metricSort"));
        Assert.Equal("withMeasuresOnly", Query(handler, "metricSortFilter"));
        Assert.Equal("true", Query(handler, "asc"));

        Assert.Equal(168, result.TotalCount);
        Assert.Equal(
            [
                "apache-rat-plugin/src/main/java/org/apache/rat/mp/All.java",
                "apache-rat-tasks/src/main/java/org/apache/rat/anttasks/All.java",
                "apache-rat-plugin/src/main/java/org/apache/rat/mp/Any.java",
                "apache-rat-tasks/src/main/java/org/apache/rat/anttasks/Any.java",
                "apache-rat-tools/src/main/java/org/apache/rat/tools/ArgumentTypes.java",
            ],
            result.Components.Select(component => component.Path));

        // The filter guarantees every ranked row has the sort metric, and between them the five rows
        // carry the other three — including uncovered_conditions, which only one of them has.
        Assert.Empty(result.MissingMetrics);
        Assert.Null(result.Note);
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
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Components);
        Assert.Equal(0, result.TotalCount);
        Assert.False(result.HasMore);

        Assert.Equal(["coverage"], result.MissingMetrics);

        Assert.NotNull(result.Note);
        Assert.Contains("has a value for coverage", result.Note, StringComparison.Ordinal);
        Assert.Contains("is not measured as zero", result.Note, StringComparison.Ordinal);
        Assert.Contains("getComponentMeasures", result.Note, StringComparison.Ordinal);
        Assert.Contains("listMetrics", result.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same empty page without a ranking keeps the ordinary diff, so the narrowing above cannot
    /// spread to a page that is empty for some other reason.
    /// </summary>
    [Fact]
    public async Task AnEmptyPageWithNoRankingStillReportsEveryRequestedMetricAsMissing()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.ComponentTreeRankedToNothing);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListComponentMeasuresAsync(
            client,
            ToolTestHost.CreateOptions(),
            ["coverage", "ncloc"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["coverage", "ncloc"], result.MissingMetrics);
        Assert.Null(result.Note);
    }

    /// <summary>
    /// A blank <c>sortByMetric</c> is no ranking at all — the client refuses to send the three sort
    /// parameters for it, so the mapper must not read the empty page as a filtered one either.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("metricSortFilter", Names(handler));
        Assert.Equal(["coverage", "ncloc"], result.MissingMetrics);
        Assert.Null(result.Note);
    }

    // ---------------------------------------------------------------------------------------
    // getMeasuresHistory
    // ---------------------------------------------------------------------------------------

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/measures/search_history", Path(handler));
        Assert.Equal("ncloc,coverage", Query(handler, "metrics"));
        Assert.DoesNotContain("metricKeys", Names(handler));
        Assert.Equal("2024-08-01", Query(handler, "from"));
        Assert.Equal("2024-11-01", Query(handler, "to"));

        Assert.Equal(2, result.Metrics.Count);
        Assert.Equal("67668", result.Metrics[0].History[0].Value);

        // A point with no value is a gap in the series, not a zero.
        Assert.Null(result.Metrics[1].History[0].Value);

        Assert.Equal(29, result.TotalCount);
        Assert.NotNull(result.Note);
        Assert.Contains("number of analyses", result.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message has to name the parameter the caller actually has. On this one tool it is
    /// <c>metrics</c>; naming <c>metricKeys</c> would send them looking for an argument that does
    /// not exist.
    /// </summary>
    [Fact]
    public async Task MeasureHistoryComplainsAboutMetricsRatherThanMetricKeys()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetMeasuresHistoryAsync(
                client,
                ToolTestHost.CreateOptions(),
                [],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("metrics is required", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("metricKeys", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // listMetrics
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// C6: <c>f</c> is never sent. <c>type</c> is not one of its accepted values, and asking for it
    /// is a 400 — while omitting <c>f</c> returns every field including <c>type</c> and
    /// <c>direction</c>, which is what this tool reads.
    /// </summary>
    [Fact]
    public async Task ListMetricsFetchesTheWholeCatalogueInOneCallAndSendsNoFieldSelector()
    {
        using var handler = Stub("metrics-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await MeasureReadTools.ListMetricsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/metrics/search", Path(handler));
        Assert.Equal(["ps"], Names(handler));
        Assert.Equal("500", Query(handler, "ps"));
    }

    [Fact]
    public async Task DataMetricsAreLeftOutByDefaultAndTheNoteSaysHowMany()
    {
        using var handler = Stub("metrics-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListMetricsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(142, result.Metrics.Count);
        Assert.Equal(142, result.TotalCount);
        Assert.DoesNotContain(result.Metrics, metric => metric.Key == "ncloc_data");

        Assert.NotNull(result.Note);
        Assert.Contains("13 metric(s)", result.Note, StringComparison.Ordinal);
        Assert.Contains("includeDataMetrics=true", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncludeDataMetricsKeepsTheBlobsAndDropsTheNote()
    {
        using var handler = Stub("metrics-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListMetricsAsync(
            client,
            ToolTestHost.CreateOptions(),
            includeDataMetrics: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(155, result.Metrics.Count);
        Assert.Contains(result.Metrics, metric => metric.Key == "ncloc_data");
        Assert.Null(result.Note);
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
    [Fact]
    public async Task TheMetricQueryMatchesDescriptionsAsWellAsKeys()
    {
        using var handler = Stub("metrics-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        var options = ToolTestHost.CreateOptions();

        var described = await MeasureReadTools.ListMetricsAsync(
            client,
            options,
            query: "balanced by statements",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(described.Metrics);
        Assert.Contains(described.Metrics, metric => metric.Key == "duplicated_lines_density");
        Assert.All(described.Metrics, metric => Assert.NotEqual("ncloc", metric.Key));

        using var second = Stub("metrics-search.json");
        using var secondClient = ToolTestHost.CreateClient(second);

        var byKey = await MeasureReadTools.ListMetricsAsync(
            secondClient,
            options,
            query: "duplicated",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(7, byKey.Metrics.Count);
        Assert.All(byKey.Metrics, metric => Assert.Equal("Duplications", metric.Domain));
    }

    [Fact]
    public async Task TheDomainFilterIsExactAndCaseInsensitive()
    {
        using var handler = Stub("metrics-search.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.ListMetricsAsync(
            client,
            ToolTestHost.CreateOptions(),
            domain: "coverage",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Metrics);
        Assert.All(result.Metrics, metric => Assert.Equal("Coverage", metric.Domain));
        Assert.Contains(result.Metrics, metric => metric.Key == "coverage" && metric.HigherIsBetter == true);
    }

    // ---------------------------------------------------------------------------------------
    // getFileCoverage
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task FileCoverageAlwaysNamesBothEndsOfTheRangeItReads()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.SourceLinesWithCoverage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetFileCoverageAsync(
            client,
            ToolTestHost.CreateOptions(),
            "src/Quartz/Core/QuartzScheduler.cs",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/sources/lines", Path(handler));
        Assert.Equal(FileKey, Query(handler, "key"));

        // Both ends resolved even though the caller named neither: the alternative is asking for
        // every line of a file whose length is unknown.
        Assert.Equal("1", Query(handler, "from"));
        Assert.Equal("2000", Query(handler, "to"));

        Assert.Equal(1, result.From);
        Assert.Equal(2000, result.To);
        Assert.Equal([3], result.UncoveredLines);
        Assert.Equal([2], result.PartiallyCoveredLines);
        Assert.Equal([1], result.NewLines);
        Assert.Equal([3], result.DuplicatedLines);
    }

    [Fact]
    public async Task FileCoverageReturnsOnlyTheLinesWorthWritingATestFor()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.SourceLinesWithCoverage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetFileCoverageAsync(
            client,
            ToolTestHost.CreateOptions(),
            FileKey,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([2, 3], result.Lines.Select(line => line.Line));
        Assert.NotNull(result.Note);
        Assert.Contains("onlyUncovered=false", result.Note, StringComparison.Ordinal);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3, 4, 5], result.Lines.Select(line => line.Line));
        Assert.Contains("every line in the range", result.Note!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The source text is syntax-highlighted HTML and the agent already has the file on disk, so it
    /// is dropped — asserted on the serialised result, because "no code" is a claim about the JSON
    /// the client receives rather than about the record.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result, SonarToolJsonContext.Default.FileCoverageResult);

        // "code" as a property name, not as a substring: the deep link legitimately points at
        // /code?id=… , which is the browser page for the file.
        Assert.DoesNotContain("\"code\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("<span", json, StringComparison.Ordinal);
        Assert.DoesNotContain("scmRevision", json, StringComparison.Ordinal);
        Assert.Equal(6, result.Lines.Count);
    }

    /// <summary>
    /// An empty <c>uncoveredLines</c> means "fully covered" only when coverage was measured at all,
    /// so the unmeasured case says so rather than letting the absence read as success.
    /// </summary>
    [Fact]
    public async Task AFileWithNoCoverageDataSaysSoInsteadOfLookingFullyCovered()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolPayloads.SourceLinesWithoutCoverage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await MeasureReadTools.GetFileCoverageAsync(
            client,
            ToolTestHost.CreateOptions(),
            FileKey,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.UncoveredLines);
        Assert.Contains("coverage was never measured", result.Note!, StringComparison.Ordinal);
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
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([50, 57], result.UncoveredLines);
        Assert.Equal([43, 49], result.PartiallyCoveredLines);

        // Non-executable lines in a measured file: no lineHits, so neither state applies.
        int[] noData = [45, 46, 47, 48, 51, 53, 54, 55, 56];

        Assert.All(noData, line => Assert.DoesNotContain(line, result.UncoveredLines));
        Assert.All(noData, line => Assert.DoesNotContain(line, result.PartiallyCoveredLines));

        // Fully covered lines are absent from both for a different reason, and that is fine.
        int[] covered = [44, 52];

        Assert.All(covered, line => Assert.DoesNotContain(line, result.UncoveredLines));
        Assert.All(covered, line => Assert.DoesNotContain(line, result.PartiallyCoveredLines));

        // onlyUncovered defaults to true, so lines is exactly the union of the two arrays, in order.
        Assert.Equal([43, 49, 50, 57], result.Lines.Select(line => line.Line));
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
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.UncoveredLines);
        Assert.Empty(result.PartiallyCoveredLines);
        Assert.Empty(result.Lines);

        Assert.NotNull(result.Note);
        Assert.DoesNotContain("coverage was never measured", result.Note, StringComparison.Ordinal);
        Assert.Contains("Coverage is measured for this file", result.Note, StringComparison.Ordinal);
        Assert.Contains(
            "no line in this range is uncovered or partially covered",
            result.Note,
            StringComparison.Ordinal);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("2001 lines", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SONARQUBE_MCP_MAX_SOURCE_LINES", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("10", Query(handler, "to"));

        _ = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetFileCoverageAsync(
                client,
                options,
                FileKey,
                from: 1,
                to: 11,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(0, null, "from must be 1 or greater")]
    [InlineData(-4, null, "from must be 1 or greater")]
    [InlineData(50, 40, "to must be greater than or equal to from")]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // transitionIssue
    // ---------------------------------------------------------------------------------------

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/issues/do_transition", RequestUrl.Path(request.Uri));
        Assert.Equal("application/x-www-form-urlencoded", request.Headers["Content-Type"]);
        Assert.Equal($"issue={WriteIssueKey}&transition=accept&comment=Intentional+here.", request.Body);

        // Live-captured: accept lands as the modern ACCEPTED plus the legacy RESOLVED/WONTFIX pair,
        // and reopen is the only transition left.
        Assert.Equal("ACCEPTED", result.IssueStatus);
        Assert.Equal("RESOLVED", result.Status);
        Assert.Equal("WONTFIX", result.Resolution);
        Assert.Equal(["reopen"], result.AvailableTransitions);
        Assert.Equal(WriteIssueFile, result.File);
    }

    /// <summary>
    /// When the operation response does not carry the new transitions, one extra read is worth it:
    /// without them the caller can only discover what it may do next by trying and failing.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/issues/search", RequestUrl.Path(handler.Requests[1].Uri));
        Assert.Equal("transitions", RequestUrl.QueryValue(handler.Requests[1].Uri, "additionalFields"));
        Assert.Equal(["reopen"], result.AvailableTransitions);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("accept supersedes wontfix", exception.Message, StringComparison.Ordinal);
        Assert.Contains("availableTransitions", exception.Message, StringComparison.Ordinal);
        Assert.Contains("setHotspotStatus", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheLegacySpellingOfAcceptIsStillAccepted()
    {
        using var handler = Stub("issues-do_transition.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueWriteTools.TransitionIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            WriteIssueKey,
            "WontFix",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal($"issue={WriteIssueKey}&transition=wontfix", Assert.Single(handler.Requests).Body);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // assignIssue and addIssueComment
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AssignIssueSendsTheLoginItWasGiven()
    {
        using var handler = Stub("issues-assign.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.AssignIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            WriteIssueKey,
            "lahma@github",
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);

        Assert.Equal("/api/issues/assign", RequestUrl.Path(request.Uri));
        Assert.Equal($"issue={WriteIssueKey}&assignee=lahma%40github", request.Body);
        Assert.Equal("lahma@github", result.Assignee);
        Assert.Equal(WriteIssueFile, result.File);
    }

    /// <summary>
    /// Omitting <c>assignee</c> means "unassign", which is <c>assignee=</c> on the wire — omitting
    /// the parameter entirely would leave the current assignee in place.
    /// </summary>
    [Fact]
    public async Task OmittingTheAssigneeUnassignsRatherThanLeavingItAlone()
    {
        using var handler = Stub("issues-assign.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueWriteTools.AssignIssueAsync(
            client,
            ToolTestHost.CreateOptions(),
            WriteIssueKey,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal($"issue={WriteIssueKey}&assignee=", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task AddIssueCommentPostsTheTextAndReportsTheCommentThatWasJustCreated()
    {
        using var handler = Stub("issues-add_comment.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.AddIssueCommentAsync(
            client,
            ToolTestHost.CreateOptions(),
            WriteIssueKey,
            WriteCommentText,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);

        Assert.Equal("/api/issues/add_comment", RequestUrl.Path(request.Uri));
        Assert.Equal(
            $"issue={WriteIssueKey}&text=sonarqube-mcp+wire+capture+2+-+removing+shortly",
            request.Body);

        // The response lists every comment on the issue, so the one just posted has to be picked out
        // of two. Which one that is comes from the timestamps, not from the position — see
        // ResultMapperTests for the reversed-order proof.
        Assert.Equal(WriteCommentKey, result.CommentKey);
        Assert.Equal(WriteCommentText, result.Text);
        Assert.NotNull(result.CreatedAt);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("text is required", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // setHotspotStatus
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The endpoint answers 204 with no body, so the resulting state has to be read back — which is
    /// exactly two requests, and the second is what the caller is actually told about.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/hotspots/change_status", RequestUrl.Path(handler.Requests[0].Uri));
        Assert.Equal(
            $"hotspot={HotspotKey}&status=REVIEWED&resolution=SAFE&comment=Placeholder+value.",
            handler.Requests[0].Body);

        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("/api/hotspots/show", RequestUrl.Path(handler.Requests[1].Uri));

        // What SonarQube stored, not what was asked for.
        Assert.Equal("REVIEWED", result.Status);
        Assert.Equal("SAFE", result.Resolution);
        Assert.Equal("src/Quartz.Examples.AspNetCore/appsettings.json", result.File);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("status=REVIEWED needs a resolution", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FIXED when the risky code was changed", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("cannot carry a resolution", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>C2: documented for SonarQube Server, refused by Cloud — so it is refused here, with the reason.</summary>
    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("ACKNOWLEDGED is documented for SonarQube Server", exception.Message, StringComparison.Ordinal);
        Assert.Contains("use SAFE", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("TO_REVIEW or REVIEWED", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>A stub primed with one golden fixture.</summary>
    // ---------------------------------------------------------------------------------------
    // getAnalysisStatus
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAnalysisStatusReportsAFinishedAnalysisAndTheScopeItWasFor()
    {
        using var handler = Stub("ce-component.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.GetAnalysisStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/ce/component", Path(handler));
        Assert.Equal(Project, Query(handler, "component"));

        // The endpoint takes no scope of its own; each task reports the one it analysed.
        Assert.Null(Query(handler, "branch"));
        Assert.Null(Query(handler, "pullRequest"));

        Assert.False(result.AnalysisInProgress);
        Assert.Empty(result.Pending);
        Assert.NotNull(result.Latest);
        Assert.Equal("SUCCESS", result.Latest.Status);
        Assert.Equal("3756", result.Latest.PullRequest);

        // Cloud spells the finish time executedAt; the published example calls it finishedAt.
        Assert.NotNull(result.Latest.FinishedAt);
        Assert.Contains("pull request 3756", result.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed analysis is the case worth getting right: every measure and gate for that scope is
    /// stale rather than merely bad, and this is the only place SonarQube's reason is visible.
    /// </summary>
    [Fact]
    public async Task GetAnalysisStatusSurfacesAFailedAnalysisAndItsErrorMessage()
    {
        using var handler = Stub("ce-component-failed.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.GetAnalysisStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("FAILED", result.Latest?.Status);
        Assert.Equal("the error message", result.Latest?.ErrorMessage);
        Assert.Contains("FAILED", result.Note, StringComparison.Ordinal);
        Assert.Contains("the error message", result.Note, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // summarizeIssues
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The whole point of the tool, against a live capture: one request, no issues fetched, and the
    /// groupings returned in the order they were asked for rather than the order SonarQube listed.
    /// </summary>
    [Fact]
    public async Task SummarizeIssuesAsksForOneIssueAndReturnsTheGroupingsInTheOrderRequested()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["rules", "files", "impactSeverities"],
            impactSeverities: ["BLOCKER"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/issues/search", Path(handler));
        Assert.Equal("1", Query(handler, "ps"));
        Assert.Equal("rules,fileUuids,impactSeverities", Query(handler, "facets"));
        Assert.Equal("rules", Query(handler, "additionalFields"));
        Assert.Null(Query(handler, "facetMode"));

        Assert.Equal(["rules", "files", "impactSeverities"], result.Facets.Select(facet => facet.GroupBy));
        Assert.Equal("issues", result.CountedIn);
        Assert.Equal(80, result.MatchingIssues);
    }

    /// <summary>
    /// A file grouping must never hand back the UUID SonarQube reports. The join comes from the
    /// response's own component sidecar, so it costs no extra request.
    /// </summary>
    [Fact]
    public async Task SummarizeIssuesReportsFilesAsPathsRatherThanComponentUuids()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["files"],
            cancellationToken: TestContext.Current.CancellationToken);

        var files = Assert.Single(result.Facets);

        Assert.NotEmpty(files.Buckets);

        foreach (var bucket in files.Buckets)
        {
            Assert.NotNull(bucket.Value);
            Assert.DoesNotContain("AYlo", bucket.Value, StringComparison.Ordinal);
            Assert.Contains("/", bucket.Value, StringComparison.Ordinal);
        }

        // Largest first, so the worst file is the first thing read.
        Assert.True(files.Buckets[0].Count >= files.Buckets[^1].Count);
    }

    /// <summary>A rule key on its own is unreadable; the sidecar is what makes the ranking mean something.</summary>
    [Fact]
    public async Task SummarizeIssuesLabelsRuleKeysWithTheirTitles()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["rules"],
            cancellationToken: TestContext.Current.CancellationToken);

        var rules = Assert.Single(result.Facets);

        Assert.Contains(rules.Buckets, bucket => !string.IsNullOrEmpty(bucket.Label));
        Assert.All(rules.Buckets, bucket => Assert.Contains(":", bucket.Value!, StringComparison.Ordinal));
    }

    /// <summary>
    /// The note has to say this or the numbers mislead: SonarQube computes each facet with its own
    /// filter removed, so a severity-filtered search still reports every severity's project-wide
    /// count. Verified live 2026-09-09 — filtering to BLOCKER returned totalCount 80 alongside an
    /// impactSeverities facet whose MEDIUM bucket was in the thousands.
    /// </summary>
    [Fact]
    public async Task SummarizeIssuesWarnsThatAGroupingIgnoresItsOwnFilter()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["impactSeverities"],
            impactSeverities: ["BLOCKER"],
            cancellationToken: TestContext.Current.CancellationToken);

        var severities = Assert.Single(result.Facets);
        var buckets = severities.Buckets.ToDictionary(bucket => bucket.Value!, bucket => bucket.Count);

        Assert.Equal(80, result.MatchingIssues);
        Assert.True(
            buckets["MEDIUM"] > result.MatchingIssues,
            "The fixture must still show a facet bucket larger than the filtered total.");

        Assert.Contains("own filter removed", result.Note, StringComparison.Ordinal);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("3735", Query(handler, "pullRequest"));
        Assert.Equal("effort", Query(handler, "facetMode"));
        Assert.Equal("3735", result.PullRequest);
        Assert.Equal("remediationMinutes", result.CountedIn);
        Assert.Contains("remediation minutes", result.Note, StringComparison.Ordinal);
    }

    /// <summary>Defaults are the three questions a triage always asks first.</summary>
    [Fact]
    public async Task SummarizeIssuesDefaultsToSeverityRuleAndFileGroupings()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        _ = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("impactSeverities,rules,fileUuids", Query(handler, "facets"));
    }

    /// <summary>
    /// <c>files</c> is the tool's word and <c>fileUuids</c> is the API's; the API rejects the first
    /// and this server rejects the second, so one word means one thing at the tool boundary.
    /// </summary>
    [Theory]
    [InlineData("fileUuids", "files")]
    [InlineData("severities", "impactSeverities")]
    [InlineData("types", "impactSoftwareQualities")]
    [InlineData("statuses", "issueStatuses")]
    public async Task SummarizeIssuesRejectsALegacyGroupingAndNamesTheModernOne(string legacy, string modern)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SummarizeIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                groupBy: [legacy],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(modern, exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Whatever case a grouping is asked for in, the result echoes one canonical spelling.</summary>
    [Fact]
    public async Task SummarizeIssuesEchoesOneCanonicalGroupingSpellingWhateverCaseWasAsked()
    {
        using var handler = Stub("issues-search-facets.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.SummarizeIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            groupBy: ["FILES", "Rules", "files"],
            cancellationToken: TestContext.Current.CancellationToken);

        // Duplicates collapse, and the wire spelling is the API's rather than the tool's.
        Assert.Equal(["files", "rules"], result.Facets.Select(facet => facet.GroupBy));
        Assert.Equal("fileUuids,rules", Query(handler, "facets"));
    }

    [Fact]
    public async Task SummarizeIssuesRefusesAnUnknownGroupingWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SummarizeIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                groupBy: ["nonsense"],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("nonsense", exception.Message, StringComparison.Ordinal);
        Assert.Contains("directories", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SummarizeIssuesRefusesAnUnknownCountModeWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SummarizeIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                countBy: "minutes",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("issues", exception.Message, StringComparison.Ordinal);
        Assert.Contains("effort", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // getIssueChangelog
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetIssueChangelogReadsTheKeyAloneAndReportsEachFieldThatMoved()
    {
        using var handler = Stub("issues-changelog.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetIssueChangelogAsync(
            client,
            ToolTestHost.CreateOptions(),
            IssueKey,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/issues/changelog", Path(handler));
        Assert.Equal(IssueKey, Query(handler, "issue"));

        // Not scope-sensitive, unlike issues/search: the key alone resolves.
        Assert.Equal(["issue"], Names(handler));

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("lahma@github", result.Entries[0].Author);
        Assert.Equal(
            ["resolution", "status"],
            result.Entries[0].Changes.Select(change => change.Field));
        Assert.Equal("OPEN", result.Entries[0].Changes[1].From);
        Assert.Equal("RESOLVED", result.Entries[0].Changes[1].To);
        Assert.Null(result.Note);
    }

    /// <summary>
    /// An empty changelog from a tokenless server is not evidence that nothing happened: SonarQube
    /// answers an anonymous request with an empty list rather than a 401 (the same restriction that
    /// empties a rule's description sections).
    /// </summary>
    [Fact]
    public async Task AnEmptyChangelogWithoutATokenSaysItCouldNotBeReadRatherThanThatNothingHappened()
    {
        using var handler = Stub("issues-changelog-anonymous.json");
        using var client = ToolTestHost.CreateAnonymousClient(handler);

        var result = await IssueReadTools.GetIssueChangelogAsync(
            client,
            ToolTestHost.CreateOptions(token: null),
            IssueKey,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Entries);
        Assert.Contains("SONARQUBE_TOKEN", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyChangelogWithATokenSaysNobodyHasChangedTheIssue()
    {
        using var handler = Stub("issues-changelog-anonymous.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueReadTools.GetIssueChangelogAsync(
            client,
            ToolTestHost.CreateOptions(),
            IssueKey,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Entries);
        Assert.DoesNotContain("SONARQUBE_TOKEN", result.Note, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // bulkUpdateIssues
    // ---------------------------------------------------------------------------------------

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/api/issues/bulk_change", Path(handler));
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);

        var body = handler.Requests[0].Body!;

        Assert.Contains("issues=" + IssueKey + "%2C" + WriteIssueKey, body, StringComparison.Ordinal);
        Assert.Contains("do_transition=accept", body, StringComparison.Ordinal);
        Assert.DoesNotContain("assign=", body, StringComparison.Ordinal);

        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Ignored);
        Assert.Equal(["transition accept", "comment"], result.Applied);
        Assert.Contains("availableTransitions", result.Note, StringComparison.Ordinal);
    }

    /// <summary>A call with only keys would report counts for a change nobody asked for.</summary>
    [Fact]
    public async Task BulkUpdateIssuesWithNoActionIsRefusedWithoutCallingTheApi()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.BulkUpdateIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                [IssueKey],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("at least one of transition, assignee or comment", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("issueKeys is required", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>The endpoint's own cap, enforced before the request rather than as a 400.</summary>
    [Fact]
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("batches", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Empty means unassign here too, the same way it does on <c>assignIssue</c>.</summary>
    [Fact]
    public async Task BulkUpdateIssuesSendsAnEmptyAssigneeToUnassign()
    {
        using var handler = Stub("issues-bulk-change.json");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await IssueWriteTools.BulkUpdateIssuesAsync(
            client,
            ToolTestHost.CreateOptions(),
            [IssueKey],
            assignee: string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("assign=", handler.Requests[0].Body!, StringComparison.Ordinal);
        Assert.Equal(["unassign"], result.Applied);
    }

    // ---------------------------------------------------------------------------------------
    // Anonymity is diagnosed where it bites (issue #1)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// An empty project list from a tokenless server looks exactly like confirmation that a project
    /// key was wrong. It is not — it is the same anonymity that made the key look wrong.
    /// </summary>
    [Fact]
    public async Task AnEmptyProjectListFromATokenlessServerSaysItCouldOnlySeePublicProjects()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"paging":{"pageIndex":1,"pageSize":50,"total":0},"components":[]}""");
        using var client = ToolTestHost.CreateAnonymousClient(handler);

        var result = await ProjectReadTools.ListProjectsAsync(
            client,
            ToolTestHost.CreateOptions(token: null),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Projects);
        Assert.Contains("SONARQUBE_TOKEN", result.Note, StringComparison.Ordinal);
        Assert.Contains("not evidence", result.Note, StringComparison.Ordinal);
    }

    /// <summary>With a token, an empty list means what it says and carries no such excuse.</summary>
    [Fact]
    public async Task AnEmptyProjectListFromAnAuthenticatedServerCarriesNoAnonymityNote()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"paging":{"pageIndex":1,"pageSize":50,"total":0},"components":[]}""");
        using var client = ToolTestHost.CreateClient(handler);

        var result = await ProjectReadTools.ListProjectsAsync(
            client,
            ToolTestHost.CreateOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Projects);
        Assert.Null(result.Note);
    }

    /// <summary>
    /// SonarQube reports a private project to an anonymous caller as "Project doesn't exist", which
    /// reads as a wrong key. The 404 has to name the missing credential or the reader goes hunting
    /// for a key that was right all along.
    /// </summary>
    [Fact]
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
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("no SONARQUBE_TOKEN is configured", exception.Message, StringComparison.Ordinal);
            Assert.Contains("indistinguishable", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            ToolErrors.UseOptions(ToolTestHost.CreateOptions());
        }
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
