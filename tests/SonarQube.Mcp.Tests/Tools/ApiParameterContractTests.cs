using System.Reflection;
using System.Text.Json;

using SonarQube.Mcp.Tests.Http;
using SonarQube.Mcp.Tools;

using Xunit;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// One (action, parameter) pair this server sends, and the values it may carry.
/// </summary>
/// <param name="Action">The web service and action, as <c>issues/search</c>.</param>
/// <param name="Name">The parameter name exactly as it goes on the wire.</param>
/// <param name="ValueSet">
/// The name of the value set in <see cref="SonarApiParameters.ValueSets"/> this parameter is
/// restricted to, or <see langword="null"/> for a free-form value.
/// </param>
internal sealed record SonarApiParameter(string Action, string Name, string? ValueSet = null);

/// <summary>
/// The complete inventory of what <c>SonarApiClient</c> puts on the wire: every action, every
/// parameter, and every closed value set.
/// </summary>
/// <remarks>
/// <para>
/// This is the structural replacement for the template's <c>FieldSets</c> table. There the risk was
/// an inclusive field list that silently dropped the pagination link; here it is a parameter
/// SonarSource renames or retires, which answers <c>400</c> at best and is silently ignored at
/// worst. The table is asserted against the requests the client actually composes (offline, always)
/// and against SonarQube Cloud's own catalogue (online, opt-in).
/// </para>
/// <para>
/// The value sets are duplicated here on purpose. <c>ToolDefaults</c> decides what a caller may ask
/// for and the client decides what is sent; if those two disagree, a validator either rejects a
/// value the API accepts or accepts one it does not. Writing the values twice and asserting the
/// copies match is what makes that disagreement a test failure rather than a support question.
/// </para>
/// </remarks>
internal static class SonarApiParameters
{
    /// <summary>Every parameter this server is capable of sending, per action.</summary>
    internal static IReadOnlyList<SonarApiParameter> Table { get; } =
    [
        // ------------------------------------------------------------------ projects, components
        new("components/search", "organization"),
        new("components/search", "qualifiers"),
        new("components/search", "q"),
        new("components/search", "p"),
        new("components/search", "ps"),

        new("components/tree", "component"),
        new("components/tree", "strategy"),
        new("components/tree", "qualifiers"),
        new("components/tree", "q"),
        new("components/tree", "branch"),
        new("components/tree", "pullRequest"),
        new("components/tree", "p"),
        new("components/tree", "ps"),

        new("project_branches/list", "project"),
        new("project_pull_requests/list", "project"),

        new("qualitygates/project_status", "projectKey"),
        new("qualitygates/project_status", "branch"),
        new("qualitygates/project_status", "pullRequest"),

        // ------------------------------------------------------------------ issues
        new("issues/search", "componentKeys"),
        new("issues/search", "issues"),
        new("issues/search", "issueStatuses", "IssueStatuses"),
        new("issues/search", "impactSeverities", "ImpactSeverities"),
        new("issues/search", "impactSoftwareQualities", "SoftwareQualities"),
        new("issues/search", "rules"),
        new("issues/search", "tags"),
        new("issues/search", "languages"),
        new("issues/search", "assignees"),
        new("issues/search", "createdAfter"),
        new("issues/search", "createdInLast"),
        new("issues/search", "sinceLeakPeriod"),
        new("issues/search", "s", "IssueSorts"),
        new("issues/search", "asc"),
        new("issues/search", "additionalFields"),
        new("issues/search", "branch"),
        new("issues/search", "pullRequest"),
        new("issues/search", "p"),
        new("issues/search", "ps"),

        new("issues/do_transition", "issue"),
        new("issues/do_transition", "transition", "Transitions"),
        new("issues/do_transition", "comment"),

        new("issues/assign", "issue"),
        new("issues/assign", "assignee"),

        new("issues/add_comment", "issue"),
        new("issues/add_comment", "text"),

        // ------------------------------------------------------------------ security hotspots
        new("hotspots/search", "projectKey"),
        new("hotspots/search", "files"),
        new("hotspots/search", "status", "HotspotStatuses"),
        new("hotspots/search", "resolution", "HotspotResolutions"),
        new("hotspots/search", "onlyMine"),
        new("hotspots/search", "sinceLeakPeriod"),
        new("hotspots/search", "branch"),
        new("hotspots/search", "pullRequest"),
        new("hotspots/search", "p"),
        new("hotspots/search", "ps"),

        new("hotspots/show", "hotspot"),

        new("hotspots/change_status", "hotspot"),
        new("hotspots/change_status", "status", "HotspotStatuses"),
        new("hotspots/change_status", "resolution", "HotspotResolutions"),
        new("hotspots/change_status", "comment"),

        // ------------------------------------------------------------------ measures and metrics
        new("measures/component", "component"),
        new("measures/component", "metricKeys"),
        new("measures/component", "additionalFields"),
        new("measures/component", "branch"),
        new("measures/component", "pullRequest"),

        new("measures/component_tree", "component"),
        new("measures/component_tree", "metricKeys"),
        new("measures/component_tree", "strategy"),
        new("measures/component_tree", "qualifiers"),
        new("measures/component_tree", "q"),
        new("measures/component_tree", "s"),
        new("measures/component_tree", "metricSort"),
        new("measures/component_tree", "metricSortFilter"),
        new("measures/component_tree", "asc"),
        new("measures/component_tree", "additionalFields"),
        new("measures/component_tree", "branch"),
        new("measures/component_tree", "pullRequest"),
        new("measures/component_tree", "p"),
        new("measures/component_tree", "ps"),

        new("measures/search_history", "component"),
        new("measures/search_history", "metrics"),
        new("measures/search_history", "from"),
        new("measures/search_history", "to"),
        new("measures/search_history", "branch"),
        new("measures/search_history", "pullRequest"),
        new("measures/search_history", "p"),
        new("measures/search_history", "ps"),

        new("metrics/search", "p"),
        new("metrics/search", "ps"),

        // ------------------------------------------------------------------ rules and sources
        new("rules/search", "organization"),
        new("rules/search", "rule_key"),
        new("rules/search", "f"),
        new("rules/search", "ps"),

        new("sources/lines", "key"),
        new("sources/lines", "from"),
        new("sources/lines", "to"),
        new("sources/lines", "branch"),
        new("sources/lines", "pullRequest"),
    ];

    /// <summary>
    /// The closed value sets, keyed by the <c>ToolDefaults</c> field that has to agree with them.
    /// </summary>
    internal static IReadOnlyDictionary<string, string[]> ValueSets { get; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["IssueStatuses"] = ["OPEN", "CONFIRMED", "FALSE_POSITIVE", "ACCEPTED", "FIXED"],
            ["DefaultIssueStatuses"] = ["OPEN", "CONFIRMED"],
            ["ImpactSeverities"] = ["INFO", "LOW", "MEDIUM", "HIGH", "BLOCKER"],
            ["SoftwareQualities"] = ["MAINTAINABILITY", "RELIABILITY", "SECURITY"],
            ["IssueSorts"] =
                ["CREATION_DATE", "UPDATE_DATE", "CLOSE_DATE", "SEVERITY", "STATUS", "ASSIGNEE", "FILE_LINE"],
            ["Transitions"] = ["accept", "confirm", "falsepositive", "reopen", "resolve", "unconfirm", "wontfix"],
            ["HotspotStatuses"] = ["TO_REVIEW", "REVIEWED"],
            ["HotspotResolutions"] = ["FIXED", "SAFE"],
            ["RuleSections"] = ["introduction", "root_cause", "assess_the_problem", "how_to_fix", "resources"],
            ["DefaultRuleSections"] = ["introduction", "root_cause", "how_to_fix"],
        };

    /// <summary>
    /// Parameters SonarQube Cloud accepts but does not document, each verified live on the stated
    /// date. Without this list the online drift test would fail on three parameters SonarSource
    /// simply never wrote down; with it, the test still catches a parameter that is withdrawn.
    /// </summary>
    internal static IReadOnlyList<string> UndocumentedButVerified { get; } =
    [
        // 200 with a real branch, and "Project doesn't exist" with a bogus one — so it is read,
        // not ignored (2026-08-11).
        "hotspots/search:branch",
        "hotspots/search:pullRequest",

        // Without it the project search also returns directories and files (2026-08-11).
        "components/search:qualifiers",
    ];

    /// <summary>
    /// Actions absent from <c>api/webservices/list</c> altogether, with and without
    /// <c>include_internals=true</c>, that nonetheless answer 200 (C11, verified 2026-08-11).
    /// This is a missing <em>action</em> rather than a missing parameter, so the online test must
    /// look the action up tolerantly instead of throwing on it.
    /// </summary>
    internal static IReadOnlyList<string> UndocumentedActions { get; } = ["sources/lines"];
}

/// <summary>
/// The parameter contract between this server and SonarQube Cloud, enforced from both sides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Offline</b> (always, in CI): every client method is driven through the stub with every
/// optional argument supplied, the parameter names it actually sent are collected, and each one is
/// checked against <see cref="SonarApiParameters.Table"/>. The reverse direction is checked too —
/// a table row nothing sends is a row nobody is maintaining — and a deliberately doctored copy of
/// the table proves the check is capable of failing at all.
/// </para>
/// <para>
/// <b>Online</b> (opt-in, <c>SONARQUBE_MCP_LIVE_TESTS=1</c>): the same table is checked against
/// <c>api/webservices/list</c>, as a <em>subset</em> assertion with an explicit exception list. It
/// can never be an equality assertion: the catalogue is incomplete (C5, C11), and the parameters we
/// do not send are none of our business.
/// </para>
/// </remarks>
public class ApiParameterContractTests
{
    private const string Project = ToolTestHost.Project;
    private const string FileKey = ToolTestHost.FileComponent;

    /// <summary>Set to <c>1</c> to run the live legs. Never set in the required CI path.</summary>
    private const string LiveVariable = "SONARQUBE_MCP_LIVE_TESTS";

    private static readonly string[] Metrics = ["ncloc", "coverage"];
    private static readonly string[] Qualifiers = ["FIL", "UTS"];
    private static readonly string[] AdditionalFields = ["periods"];
    private static readonly string[] Files = ["src/Widget.cs"];

    /// <summary>
    /// Every parameter the client is able to send, in one pass. A failure here means the client
    /// grew a parameter nobody recorded — which is exactly the parameter the online leg would have
    /// told us about had it known to look.
    /// </summary>
    [Fact]
    public async Task EveryParameterTheClientSendsIsInTheTable()
    {
        var sent = await DriveEveryEndpointAsync();
        var unknown = Unknown(SonarApiParameters.Table, sent);

        Assert.Empty(unknown);
    }

    /// <summary>
    /// The reverse direction: a row that no client method can produce is either a typo or a
    /// parameter that was removed, and either way the table has stopped describing this server.
    /// </summary>
    [Fact]
    public async Task EveryTableRowIsActuallySentBySomeClientMethod()
    {
        var sent = await DriveEveryEndpointAsync();

        Assert.Empty(Unsent(SonarApiParameters.Table, sent));
    }

    /// <summary>
    /// The rule has to be able to bite, in both directions. A copy of the table with one row
    /// deleted must report that parameter as unknown, and a copy with one invented row must report
    /// that row as never sent — otherwise the two tests above would pass just as happily against a
    /// client that had stopped sending anything at all.
    /// </summary>
    [Fact]
    public async Task TheContractCheckIsDiscriminating()
    {
        var sent = await DriveEveryEndpointAsync();

        Assert.Empty(Unknown(SonarApiParameters.Table, sent));
        Assert.Empty(Unsent(SonarApiParameters.Table, sent));

        // A deliberately incomplete copy: everything except the one parameter that makes
        // searchIssues default to the work that is still outstanding.
        var withoutARealRow = SonarApiParameters.Table
            .Where(entry => entry.Action != "issues/search" || entry.Name != "issueStatuses")
            .ToArray();

        Assert.Equal(["issues/search:issueStatuses"], Unknown(withoutARealRow, sent));

        // And a copy carrying a parameter this server has never sent.
        var withABogusRow = SonarApiParameters.Table
            .Append(new SonarApiParameter("issues/search", "definitelyNotAParameter"))
            .ToArray();

        Assert.Equal(["issues/search:definitelyNotAParameter"], Unsent(withABogusRow, sent));
    }

    /// <summary>
    /// C12, asserted where the two spellings meet: <c>hotspots/search</c> filters by the
    /// project-relative <b>path</b> while <c>issues/search</c> filters by the full component
    /// <b>key</b>. The failure mode of getting this wrong is an empty result, not an error, which
    /// is why it is a test and not a comment.
    /// </summary>
    [Fact]
    public async Task HotspotSearchFiltersByPathWhileIssueSearchFiltersByKey()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Fallback = _ => StubHttpMessageHandler.CreateResponse(System.Net.HttpStatusCode.OK, "{}");

        using var client = ToolTestHost.CreateClient(handler);
        var options = ToolTestHost.CreateOptions();

        _ = await IssueReadTools.SearchHotspotsAsync(
            client,
            options,
            component: "src/Widget.cs",
            cancellationToken: TestContext.Current.CancellationToken);

        _ = await IssueReadTools.SearchIssuesAsync(
            client,
            options,
            component: "src/Widget.cs",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("src/Widget.cs", RequestUrl.QueryValue(handler.Requests[0].Uri, "files"));
        Assert.Equal($"{Project}:src/Widget.cs", RequestUrl.QueryValue(handler.Requests[1].Uri, "componentKeys"));

        // The key form must not leak into `files`: it matches nothing, silently.
        Assert.DoesNotContain(':', RequestUrl.QueryValue(handler.Requests[0].Uri, "files")!);
    }

    /// <summary>
    /// The validators and the table must name the same values. Read by reflection so that a value
    /// set added to <c>ToolDefaults</c> without a row here fails rather than going unchecked.
    /// </summary>
    [Fact]
    public void ToolDefaultsValueSetsAreExactlyTheDocumentedOnes()
    {
        var actual = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var field in typeof(ToolDefaults).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(string[]))
            {
                actual[field.Name] = (string[]) field.GetValue(null)!;
            }
        }

        Assert.Equal(
            SonarApiParameters.ValueSets.Keys.Order(StringComparer.Ordinal),
            actual.Keys.Order(StringComparer.Ordinal));

        foreach (var (name, expected) in SonarApiParameters.ValueSets)
        {
            Assert.Equal(expected, actual[name]);
        }
    }

    /// <summary>
    /// Every enum-valued parameter in the table points at a value set that exists, and every value
    /// in it is one the matching validator accepts — so the agreement is behavioural and not just
    /// two arrays that happen to be spelled the same.
    /// </summary>
    [Fact]
    public void EveryDocumentedValueIsAcceptedByTheValidatorThatGuardsIt()
    {
        foreach (var entry in SonarApiParameters.Table.Where(entry => entry.ValueSet is not null))
        {
            Assert.True(
                SonarApiParameters.ValueSets.ContainsKey(entry.ValueSet!),
                $"{entry.Action}:{entry.Name} names the value set '{entry.ValueSet}', which does not exist.");
        }

        Assert.Equal(
            SonarApiParameters.ValueSets["IssueStatuses"],
            ToolDefaults.ResolveIssueStatuses(SonarApiParameters.ValueSets["IssueStatuses"]));

        Assert.Equal(
            SonarApiParameters.ValueSets["ImpactSeverities"],
            ToolDefaults.ResolveImpactSeverities(SonarApiParameters.ValueSets["ImpactSeverities"]));

        Assert.Equal(
            SonarApiParameters.ValueSets["SoftwareQualities"],
            ToolDefaults.ResolveImpactSoftwareQualities(SonarApiParameters.ValueSets["SoftwareQualities"]));

        Assert.Equal(
            SonarApiParameters.ValueSets["RuleSections"],
            ToolDefaults.ResolveRuleSections(SonarApiParameters.ValueSets["RuleSections"]));

        foreach (var sort in SonarApiParameters.ValueSets["IssueSorts"])
        {
            Assert.Equal(sort, ToolDefaults.ResolveIssueSort(sort));
        }

        foreach (var transition in SonarApiParameters.ValueSets["Transitions"])
        {
            Assert.Equal(transition, ToolDefaults.ResolveTransition(transition));
        }

        foreach (var status in SonarApiParameters.ValueSets["HotspotStatuses"])
        {
            var resolution = string.Equals(status, "REVIEWED", StringComparison.Ordinal) ? "SAFE" : null;

            Assert.Equal((status, resolution), ToolDefaults.ResolveHotspotStatus(status, resolution));
        }

        foreach (var resolution in SonarApiParameters.ValueSets["HotspotResolutions"])
        {
            Assert.Equal(("REVIEWED", resolution), ToolDefaults.ResolveHotspotStatus("REVIEWED", resolution));
        }
    }

    // ---------------------------------------------------------------------------------------
    // The live leg
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Checks the table against SonarQube Cloud's own catalogue. Opt-in, because it needs the
    /// network and because SonarSource can change the catalogue on a day this repository is not
    /// being worked on — which must never turn a CI run red.
    /// </summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task EveryParameterIsDocumentedOrExplicitlyVerified()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(LiveVariable), "1", StringComparison.Ordinal),
            $"Set {LiveVariable}=1 to run the live API-catalogue check.");

        var documented = await FetchCatalogueAsync(TestContext.Current.CancellationToken);
        var undocumented = new List<string>();

        foreach (var entry in SonarApiParameters.Table)
        {
            var identifier = $"{entry.Action}:{entry.Name}";

            if (SonarApiParameters.UndocumentedButVerified.Contains(identifier, StringComparer.Ordinal))
            {
                continue;
            }

            // C11: an action can be missing from the catalogue entirely, so it is looked up
            // tolerantly — a throwing lookup would report "no such key" instead of the finding.
            if (!documented.TryGetValue(entry.Action, out var parameters))
            {
                if (SonarApiParameters.UndocumentedActions.Contains(entry.Action, StringComparer.Ordinal))
                {
                    continue;
                }

                undocumented.Add($"{identifier} (the whole action is absent from the catalogue)");
                continue;
            }

            if (!parameters.Contains(entry.Name, StringComparer.Ordinal))
            {
                undocumented.Add(identifier);
            }
        }

        Assert.Empty(undocumented);
    }

    /// <summary>
    /// The exception lists must stay honest: an entry SonarSource has since documented, or an action
    /// that has appeared in the catalogue, should be deleted rather than carried forever.
    /// </summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task TheUndocumentedListStillDescribesReality()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(LiveVariable), "1", StringComparison.Ordinal),
            $"Set {LiveVariable}=1 to run the live API-catalogue check.");

        var documented = await FetchCatalogueAsync(TestContext.Current.CancellationToken);
        var stale = new List<string>();

        foreach (var identifier in SonarApiParameters.UndocumentedButVerified)
        {
            var separator = identifier.LastIndexOf(':');
            var action = identifier[..separator];
            var name = identifier[(separator + 1)..];

            if (documented.TryGetValue(action, out var parameters)
                && parameters.Contains(name, StringComparer.Ordinal))
            {
                stale.Add(identifier);
            }
        }

        foreach (var action in SonarApiParameters.UndocumentedActions)
        {
            if (documented.ContainsKey(action))
            {
                stale.Add(action);
            }
        }

        Assert.Empty(stale);
    }

    /// <summary>Reads <c>api/webservices/list</c> into an action-to-parameter-names map.</summary>
    private static async Task<Dictionary<string, HashSet<string>>> FetchCatalogueAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://sonarcloud.io/") };

        using var response = await http.GetAsync(
            new Uri("api/webservices/list?include_internals=true", UriKind.Relative),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);

        var catalogue = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var service in document.RootElement.GetProperty("webServices").EnumerateArray())
        {
            var path = service.GetProperty("path").GetString() ?? string.Empty;

            // "api/issues" — the table's actions are spelled without the api/ prefix.
            if (path.StartsWith("api/", StringComparison.Ordinal))
            {
                path = path[4..];
            }

            if (!service.TryGetProperty("actions", out var actions))
            {
                continue;
            }

            foreach (var action in actions.EnumerateArray())
            {
                var key = path + "/" + action.GetProperty("key").GetString();
                var names = new HashSet<string>(StringComparer.Ordinal);

                if (action.TryGetProperty("params", out var parameters))
                {
                    foreach (var parameter in parameters.EnumerateArray())
                    {
                        if (parameter.TryGetProperty("key", out var name) && name.GetString() is { } text)
                        {
                            names.Add(text);
                        }
                    }
                }

                catalogue[key] = names;
            }
        }

        return catalogue;
    }

    // ---------------------------------------------------------------------------------------
    // Driving the client
    // ---------------------------------------------------------------------------------------

    /// <summary>The <c>action:parameter</c> identifiers a client method sent but the table lacks.</summary>
    private static string[] Unknown(IEnumerable<SonarApiParameter> table, IReadOnlySet<string> sent)
    {
        var known = new HashSet<string>(
            table.Select(entry => $"{entry.Action}:{entry.Name}"),
            StringComparer.Ordinal);

        return [.. sent.Where(identifier => !known.Contains(identifier)).Order(StringComparer.Ordinal)];
    }

    /// <summary>The table rows no client method produced.</summary>
    private static string[] Unsent(IEnumerable<SonarApiParameter> table, IReadOnlySet<string> sent) =>
    [
        .. table
            .Select(entry => $"{entry.Action}:{entry.Name}")
            .Where(identifier => !sent.Contains(identifier))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Calls every method on <c>SonarApiClient</c> with every optional argument set, and returns the
    /// <c>action:parameter</c> identifiers that went on the wire — query keys for a GET, form keys
    /// for the four POSTs.
    /// </summary>
    private static async Task<IReadOnlySet<string>> DriveEveryEndpointAsync()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Fallback = _ => StubHttpMessageHandler.CreateResponse(System.Net.HttpStatusCode.OK, "{}");

        using var client = ToolTestHost.CreateClient(handler);
        var cancellationToken = TestContext.Current.CancellationToken;

        _ = await client.SearchComponentsAsync("quartznet", "quartz", 1, 10, cancellationToken);

        _ = await client.GetComponentsTreeAsync(
            Project, "leaves", Qualifiers, "Scheduler", "main", "3266", 1, 10, cancellationToken);

        _ = await client.ListBranchesAsync(Project, cancellationToken);
        _ = await client.ListPullRequestsAsync(Project, cancellationToken);
        _ = await client.GetQualityGateProjectStatusAsync(Project, "main", "3266", cancellationToken);

        _ = await client.SearchIssuesAsync(
            componentKeys: [Project],
            issues: ["AZ_xePOumT_q4T_1FWf8"],
            issueStatuses: ["OPEN"],
            impactSeverities: ["HIGH"],
            impactSoftwareQualities: ["SECURITY"],
            rules: ["csharpsquid:S2259"],
            tags: ["cwe"],
            languages: ["cs"],
            assignees: ["__me__"],
            createdAfter: "2026-01-01",
            createdInLast: "7d",
            inNewCodePeriod: true,
            sortBy: "CREATION_DATE",
            ascending: false,
            additionalFields: ["rules"],
            branch: "main",
            pullRequest: "3266",
            page: 1,
            pageSize: 10,
            cancellationToken: cancellationToken);

        _ = await client.DoIssueTransitionAsync("AZ_xePOumT_q4T_1FWf8", "accept", "because", cancellationToken);
        _ = await client.AssignIssueAsync("AZ_xePOumT_q4T_1FWf8", "ada@github", cancellationToken);
        _ = await client.AddIssueCommentAsync("AZ_xePOumT_q4T_1FWf8", "a comment", cancellationToken);

        _ = await client.SearchHotspotsAsync(
            Project, Files, "REVIEWED", "SAFE", true, true, "main", "3266", 1, 10, cancellationToken);

        _ = await client.ShowHotspotAsync("AZvu8ZyfNsnCVHe5poFs", cancellationToken);

        await client.ChangeHotspotStatusAsync(
            "AZvu8ZyfNsnCVHe5poFs", "REVIEWED", "SAFE", "reviewed", cancellationToken);

        _ = await client.GetComponentMeasuresAsync(
            FileKey, Metrics, AdditionalFields, "main", "3266", cancellationToken);

        _ = await client.GetComponentTreeMeasuresAsync(
            Project, Metrics, "leaves", Qualifiers, "Scheduler", "coverage", true, AdditionalFields,
            "main", "3266", 1, 10, cancellationToken);

        _ = await client.SearchMeasuresHistoryAsync(
            Project, Metrics, "2026-01-01", "2026-08-01", "main", "3266", 1, 10, cancellationToken);

        _ = await client.SearchMetricsAsync(1, 500, cancellationToken);
        _ = await client.SearchRulesAsync("quartznet", "csharpsquid:S2259", cancellationToken);
        _ = await client.GetSourceLinesAsync(FileKey, 1, 50, "main", "3266", cancellationToken);
        _ = await client.ValidateAuthenticationAsync(cancellationToken);

        // Nothing above may be skipped silently: a method that stopped sending a request would make
        // the table look complete while covering less than it claims.
        Assert.Equal(19, handler.Requests.Count);

        var sent = new HashSet<string>(StringComparer.Ordinal);

        foreach (var request in handler.Requests)
        {
            var action = ActionOf(request.Uri);

            foreach (var name in RequestUrl.QueryNames(request.Uri))
            {
                sent.Add($"{action}:{name}");
            }

            foreach (var name in FormKeys(request.Body))
            {
                sent.Add($"{action}:{name}");
            }
        }

        return sent;
    }

    /// <summary>Turns <c>/api/issues/search</c> into <c>issues/search</c>.</summary>
    private static string ActionOf(Uri? uri)
    {
        var path = RequestUrl.Path(uri);

        Assert.StartsWith("/api/", path, StringComparison.Ordinal);

        return path[5..];
    }

    /// <summary>The parameter names of an <c>application/x-www-form-urlencoded</c> body.</summary>
    private static IEnumerable<string> FormKeys(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }

        return body
            .Split('&')
            .Select(pair =>
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                return Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            });
    }
}
