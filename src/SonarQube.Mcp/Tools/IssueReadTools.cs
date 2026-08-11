using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;
using SonarQube.Mcp.Tools.Models;

namespace SonarQube.Mcp.Tools;

/// <summary>
/// The findings half of the surface: issues, the rules behind them, and security hotspots.
/// </summary>
/// <remarks>
/// <para>
/// Every method is static, with the client and the options bound from DI and excluded from the
/// generated schema. The class is sealed rather than <c>static</c> so it can be a type argument to
/// <c>WithTools&lt;T&gt;</c>; the private constructor keeps it uninstantiable.
/// </para>
/// <para>
/// Two things this class does that the API does not. Issues come back with their file <em>path</em>
/// rather than the component key SonarQube reports, joined through the response's own component
/// sidecar. And <c>getIssue</c> asks for the transitions the issue can actually take, which is what
/// makes <c>transitionIssue</c> usable without guessing.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class IssueReadTools
{
    /// <summary>
    /// The sidecar arrays <c>searchIssues</c> asks for. Never <c>_all</c>: it drags a fifty-element
    /// <c>languages</c> array onto every response for a field nothing reads.
    /// </summary>
    private static readonly string[] SearchAdditionalFields = ["rules"];

    /// <summary>What a single-issue read asks for: the transitions and comments a list cannot carry.</summary>
    private static readonly string[] DetailAdditionalFields = ["transitions", "comments", "rules", "users"];

    private IssueReadTools()
    {
    }

    [McpServerTool(
        Name = "searchIssues",
        Title = "Search issues",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Searches a project's issues, newest first, and returns each one with its file path, line, message, " +
        "rule and clean-code impacts. This is the main triage tool. It defaults to issueStatuses " +
        "OPEN,CONFIRMED — the work still outstanding — so pass issueStatuses explicitly to see accepted, " +
        "false-positive or fixed issues. Narrow with component (a file or directory), impactSeverities, " +
        "rules or tags rather than paging through everything: only the first 10000 results are reachable. " +
        "Pass pullRequest to see only what a pull request introduced. Results are paginated.")]
    public static async Task<IssueSearchResult> SearchIssuesAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The Sonar project key (for example myorg_myrepo) - the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it.")]
        string? projectKey = null,
        [Description("Narrow to one file or directory, as either a project-relative path (src/Widget.cs) or a full component key (myorg_myrepo:src/Widget.cs). Omit for the whole project.")]
        string? component = null,
        [Description("Analysed branch to search, spelled as listBranches reports it. Mutually exclusive with pullRequest; omit both for the project's main branch.")]
        string? branch = null,
        [Description("Pull request to search, as the SCM number in a string (\"3266\") - the `key` listPullRequests returns. Mutually exclusive with branch; omit both for the main branch.")]
        string? pullRequest = null,
        [Description("Which statuses to include: OPEN, CONFIRMED, FALSE_POSITIVE, ACCEPTED, FIXED. Defaults to OPEN,CONFIRMED, which is the outstanding work.")]
        string[]? issueStatuses = null,
        [Description("Clean-code severities to include: INFO, LOW, MEDIUM, HIGH, BLOCKER. The old MINOR/MAJOR/CRITICAL names are not accepted - they map to LOW/MEDIUM/HIGH.")]
        string[]? impactSeverities = null,
        [Description("Software qualities to include: MAINTAINABILITY, RELIABILITY, SECURITY. This replaces the old issue types - CODE_SMELL is MAINTAINABILITY, BUG is RELIABILITY, VULNERABILITY is SECURITY.")]
        string[]? impactSoftwareQualities = null,
        [Description("Rule keys to include, spelled repository:rule (csharpsquid:S2259). Use it to find every occurrence of a rule getRule explained.")]
        string[]? rules = null,
        [Description("Issue tags to include, for example \"cwe\" or \"performance\".")]
        string[]? tags = null,
        [Description("Language keys to include, for example cs, java, js, py.")]
        string[]? languages = null,
        [Description("Assignee logins to include. __me__ means the account the configured token belongs to.")]
        string[]? assignees = null,
        [Description("Only issues created on or after this date: YYYY-MM-DD or a full ISO datetime. Mutually exclusive with createdInLast.")]
        string? createdAfter = null,
        [Description("Only issues created within this window ending now: a number followed by d, w, m or y (7d, 2w, 1m, 1y). Mutually exclusive with createdAfter.")]
        string? createdInLast = null,
        [Description("Only issues on code inside the project's new-code period. This is the same code a quality gate judges.")]
        bool? inNewCodePeriod = null,
        [Description("Sort field: CREATION_DATE, UPDATE_DATE, CLOSE_DATE, SEVERITY, STATUS, ASSIGNEE, FILE_LINE. Defaults to CREATION_DATE.")]
        string? sortBy = null,
        [Description("Sort ascending. Defaults to false, which puts the newest (or worst) issues first.")]
        bool ascending = false,
        [Description("1-based page number. Defaults to 1.")]
        int? page = null,
        [Description("Issues per page. Clamped to 1-100 by default; SONARQUBE_MCP_MAX_PAGE_SIZE raises the ceiling. Defaults to 50.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var project = ToolDefaults.ResolveProject(projectKey, options);
        var componentKey = ToolDefaults.ResolveComponent(component, project);
        var scope = ToolDefaults.ResolveScope(branch, pullRequest);
        var statuses = ToolDefaults.ResolveIssueStatuses(issueStatuses);
        var severities = ToolDefaults.ResolveImpactSeverities(impactSeverities);
        var qualities = ToolDefaults.ResolveImpactSoftwareQualities(impactSoftwareQualities);
        var created = ToolDefaults.ResolveCreatedFilter(createdAfter, createdInLast);
        var sort = ToolDefaults.ResolveIssueSort(sortBy);
        var paging = ToolDefaults.ResolvePaging(page, pageSize, options);

        var context = new ToolCallContext("searchIssues", project, componentKey);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client.SearchIssuesAsync(
                    [componentKey],
                    issues: null,
                    statuses,
                    severities,
                    qualities,
                    ToolDefaults.CleanList(rules),
                    ToolDefaults.CleanList(tags),
                    ToolDefaults.CleanList(languages),
                    ToolDefaults.CleanList(assignees),
                    created.CreatedAfter,
                    created.CreatedInLast,
                    inNewCodePeriod,
                    sort,
                    ascending,
                    SearchAdditionalFields,
                    scope.Branch,
                    scope.PullRequest,
                    paging.Page,
                    paging.PageSize,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Issues(response, scope, paging.Page, paging.PageSize, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getIssue",
        Title = "Get issue",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Reads one issue in full: everything searchIssues returns plus its comments, its secondary locations " +
        "(the flow that explains how a data-flow rule reached its conclusion), and availableTransitions - the " +
        "transitions that are legal from this issue's current state. Call this before transitionIssue: " +
        "transitionIssue rejects a transition the issue cannot take, and this is the only way to know which " +
        "ones it can.")]
    public static async Task<IssueDetail> GetIssueAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The issue key, as searchIssues reports it (for example AZ_xePOumT_q4T_1FWf8).")]
        string issueKey,
        [Description("The branch the issue is on, when it is not on the main branch. Mutually exclusive with pullRequest.")]
        string? branch = null,
        [Description("The pull request the issue is on, as the SCM number in a string. Mutually exclusive with branch.")]
        string? pullRequest = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var key = ToolDefaults.RequireIssueKey(issueKey);
        var scope = ToolDefaults.ResolveScope(branch, pullRequest);

        var context = new ToolCallContext("getIssue", options.DefaultProject, Component: null, EntityKey: "issue " + key);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            // issues/search with an explicit key rather than a dedicated show endpoint: SonarQube
            // has none, and this is the only action that can be asked for transitions and comments.
            var response = await client.SearchIssuesAsync(
                    componentKeys: null,
                    issues: [key],
                    issueStatuses: null,
                    impactSeverities: null,
                    impactSoftwareQualities: null,
                    rules: null,
                    tags: null,
                    languages: null,
                    assignees: null,
                    createdAfter: null,
                    createdInLast: null,
                    inNewCodePeriod: null,
                    sortBy: null,
                    ascending: null,
                    DetailAdditionalFields,
                    scope.Branch,
                    scope.PullRequest,
                    page: null,
                    pageSize: 1,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.Issues is not { Count: > 0 } issues || issues[0] is null)
            {
                throw new McpException(
                    $"No issue with key '{key}' is visible to this server. An issue key belongs to one " +
                    "analysis scope, so an issue raised on a branch or pull request is not found without " +
                    "that branch or pullRequest argument; a closed issue is eventually purged. Find the " +
                    "current key with searchIssues, scoped the same way.");
            }

            return ResultMapper.Detail(
                issues[0],
                ComponentKeys.BuildPathMap(response.Components),
                ResultMapper.RuleNames(response.Rules),
                scope,
                options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getRule",
        Title = "Get rule",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Explains one rule: what it flags, why it matters, and how to fix it, with the HTML converted to text " +
        "and code fenced. Call it once per distinct rule when triaging - the explanation is the same for every " +
        "issue that rule raised, so fetching it per issue wastes context. Sections are capped and truncation is " +
        "marked. If the response comes back with no sections at all, the account is not entitled to read rule " +
        "descriptions; the note says so and the rule's name and impacts are still returned.")]
    public static async Task<RuleDetail> GetRuleAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The rule key, spelled repository:rule (csharpsquid:S2259) - exactly as searchIssues reports it on an issue.")]
        string ruleKey,
        [Description("Which description sections to return: introduction, root_cause, assess_the_problem, how_to_fix, resources. Defaults to introduction,root_cause,how_to_fix.")]
        string[]? sections = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var key = ToolDefaults.RequireRuleKey(ruleKey);
        var organization = ToolDefaults.RequireOrganization(options);
        var requestedSections = ToolDefaults.ResolveRuleSections(sections);

        var context = new ToolCallContext("getRule", Project: null, Component: null, EntityKey: "rule " + key);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            // rules/search, not rules/show: on SonarQube Cloud only this action has an `f` parameter,
            // and `f=descriptionSections` is the whole reason for the call.
            var response = await client.SearchRulesAsync(organization, key, cancellationToken).ConfigureAwait(false);

            if (response.Rules is not { Count: > 0 } rules || rules[0] is null)
            {
                throw new McpException(
                    $"No rule with key '{key}' exists in organization '{organization}'. Rule keys are spelled " +
                    "repository:rule (csharpsquid:S2259, java:S1118) and are case-sensitive; searchIssues " +
                    "reports the exact key on every issue it returns.");
            }

            return ResultMapper.Rule(rules[0], requestedSections, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "searchHotspots",
        Title = "Search security hotspots",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Searches a project's security hotspots - code that is security-sensitive and needs a human decision, " +
        "which is a different list from the issues searchIssues returns and is never included there. Defaults " +
        "to every hotspot; pass status=TO_REVIEW for the ones nobody has looked at yet. Each result carries " +
        "vulnerabilityProbability (LOW/MEDIUM/HIGH), which is how likely SonarQube thinks it is to be real. " +
        "Note that assigneeId is an internal UUID on this endpoint, not a login. Results are paginated.")]
    public static async Task<HotspotSearchResult> SearchHotspotsAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The Sonar project key (for example myorg_myrepo) - the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it.")]
        string? projectKey = null,
        [Description("Narrow to one file, as either a project-relative path (src/Widget.cs) or a full component key (myorg_myrepo:src/Widget.cs). Omit for the whole project.")]
        string? component = null,
        [Description("Review status to include: TO_REVIEW or REVIEWED. Omit for both.")]
        string? status = null,
        [Description("Resolution to include, for reviewed hotspots: FIXED or SAFE. ACKNOWLEDGED is not accepted on SonarQube Cloud.")]
        string? resolution = null,
        [Description("Only hotspots assigned to the account the configured token belongs to.")]
        bool? onlyMine = null,
        [Description("Only hotspots on code inside the project's new-code period.")]
        bool? inNewCodePeriod = null,
        [Description("Analysed branch to search, spelled as listBranches reports it. Mutually exclusive with pullRequest; omit both for the project's main branch.")]
        string? branch = null,
        [Description("Pull request to search, as the SCM number in a string (\"3266\") - the `key` listPullRequests returns. Mutually exclusive with branch; omit both for the main branch.")]
        string? pullRequest = null,
        [Description("1-based page number. Defaults to 1.")]
        int? page = null,
        [Description("Hotspots per page. Clamped to 1-100 by default; SONARQUBE_MCP_MAX_PAGE_SIZE raises the ceiling. Defaults to 50.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var project = ToolDefaults.ResolveProject(projectKey, options);
        var scope = ToolDefaults.ResolveScope(branch, pullRequest);
        var filter = ToolDefaults.ResolveHotspotSearchStatus(status, resolution);
        var paging = ToolDefaults.ResolvePaging(page, pageSize, options);

        // `files` takes the project-relative PATH here, not the component key every other endpoint
        // in this server takes (verified live: a key matches nothing, silently). The project itself
        // is already the projectKey argument and has no path, so it is not a file filter at all.
        var path = ToolDefaults.ResolveComponentPath(component, project);
        var files = path is null ? null : new[] { path };

        var context = new ToolCallContext("searchHotspots", project, path);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client.SearchHotspotsAsync(
                    project,
                    files,
                    filter.Status,
                    filter.Resolution,
                    onlyMine,
                    inNewCodePeriod,
                    scope.Branch,
                    scope.PullRequest,
                    paging.Page,
                    paging.PageSize,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Hotspots(
                response, project, scope, paging.Page, paging.PageSize, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getHotspot",
        Title = "Get security hotspot",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Reads one security hotspot in full, with the three explanations that make it reviewable: what the risk " +
        "is, what an attacker could do, and how to make it safe. Also returns the review history and " +
        "canChangeStatus - read that before calling setHotspotStatus, because false means that call would be " +
        "refused. Unlike searchHotspots, the assignee here is a login rather than a UUID.")]
    public static async Task<HotspotDetail> GetHotspotAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The hotspot key, as searchHotspots reports it. Hotspots and issues have separate keys.")]
        string hotspotKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var key = ToolDefaults.RequireHotspotKey(hotspotKey);

        var context = new ToolCallContext(
            "getHotspot", options.DefaultProject, Component: null, EntityKey: "hotspot " + key);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client.ShowHotspotAsync(key, cancellationToken).ConfigureAwait(false);

            return ResultMapper.Hotspot(response, options.BaseUrlText);
        }).ConfigureAwait(false);
    }
}
