using System.ComponentModel;

using ModelContextProtocol.Server;

using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;
using SonarQube.Mcp.Tools.Models;

namespace SonarQube.Mcp.Tools;

/// <summary>
/// Finding your way around a SonarQube organization: which projects exist, what is in them, which
/// branches and pull requests have been analysed, and whether the quality gate passes.
/// </summary>
/// <remarks>
/// <para>
/// Every method is static: <see cref="SonarApiClient"/> and <see cref="SonarQubeMcpOptions"/> arrive
/// as plain parameters that the SDK binds from DI — it excludes anything
/// <c>IServiceProviderIsService</c> recognises, along with the <see cref="CancellationToken"/>, from
/// the generated schema. That avoids activating an instance per call and leaves each method directly
/// unit-testable with a stub client.
/// </para>
/// <para>
/// The class is sealed rather than <c>static</c> for one reason: C# forbids a static class as a type
/// argument (CS0718), and the registration is <c>WithTools&lt;ProjectReadTools&gt;(jsonOptions)</c> —
/// never <c>WithToolsFromAssembly()</c>, which is not AOT-safe (IL2026). The private constructor
/// keeps the type uninstantiable anyway.
/// </para>
/// <para>
/// Nothing here throws anything but <see cref="ModelContextProtocol.McpException"/>: every body runs
/// inside <see cref="ToolErrors.ExecuteAsync"/>.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class ProjectReadTools
{
    private ProjectReadTools()
    {
    }

    [McpServerTool(
        Name = "listProjects",
        Title = "List projects",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists the projects in the configured SonarQube organization, with the project key every other tool " +
        "takes as projectKey. Start here when the key is unknown: it is not the repository name, and guessing " +
        "it produces a 404. Pass query to match part of a key or name. Results are paginated — ask for page+1 " +
        "while hasMore is true.")]
    public static async Task<ProjectListResult> ListProjectsAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("Substring matched against project key and name. Omit to list every project in the organization.")]
        string? query = null,
        [Description("1-based page number. Defaults to 1.")]
        int? page = null,
        [Description("Projects per page. Clamped to 1-100 by default; SONARQUBE_MCP_MAX_PAGE_SIZE raises the ceiling. Defaults to 50.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var organization = ToolDefaults.RequireOrganization(options);
        var paging = ToolDefaults.ResolvePaging(page, pageSize, options);

        var context = new ToolCallContext("listProjects");

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client
                .SearchComponentsAsync(organization, query, paging.Page, paging.PageSize, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Projects(response, paging.Page, paging.PageSize, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "listComponents",
        Title = "List components",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists the files and directories SonarQube analysed in a project, each with its component key and its " +
        "project-relative path. Use it to turn a file you have on disk into the component key the measures and " +
        "coverage tools take, or to confirm that a file was analysed at all — a path SonarQube never saw is " +
        "the usual reason a measures call comes back with nothing. Results are paginated.")]
    public static async Task<ComponentListResult> ListComponentsAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The Sonar project key (for example myorg_myrepo) - the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it.")]
        string? projectKey = null,
        [Description("Substring matched against component name and path, for example a file name.")]
        string? query = null,
        [Description("Which components to return: \"files\" (every file and test file in the project, the default), \"directories\" (the directories directly below the project), or \"all\" (every component).")]
        string? scope = null,
        [Description("Analysed branch to read, spelled as listBranches reports it. Mutually exclusive with pullRequest; omit both for the project's main branch.")]
        string? branch = null,
        [Description("Pull request to read, as the SCM number in a string (\"3266\") - the `key` listPullRequests returns. Mutually exclusive with branch; omit both for the main branch.")]
        string? pullRequest = null,
        [Description("1-based page number. Defaults to 1.")]
        int? page = null,
        [Description("Components per page. Clamped to 1-100 by default; SONARQUBE_MCP_MAX_PAGE_SIZE raises the ceiling. Defaults to 50.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var project = ToolDefaults.ResolveProject(projectKey, options);
        var selection = ToolDefaults.ResolveComponentScope(scope);
        var analysisScope = ToolDefaults.ResolveScope(branch, pullRequest);
        var paging = ToolDefaults.ResolvePaging(page, pageSize, options);

        var context = new ToolCallContext("listComponents", project);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client.GetComponentsTreeAsync(
                    project,
                    selection.Strategy,
                    selection.Qualifiers,
                    query,
                    analysisScope.Branch,
                    analysisScope.PullRequest,
                    paging.Page,
                    paging.PageSize,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Components(
                response, project, analysisScope, paging.Page, paging.PageSize, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "listBranches",
        Title = "List branches",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists the branches SonarQube has analysed for a project, with each branch's last analysis date and " +
        "issue counts, and which one is the main branch. Call it before passing branch to any other tool: a " +
        "branch that exists in git but has never been analysed is a 404 here, not an empty result. Not " +
        "paginated - SonarQube returns every analysed branch.")]
    public static async Task<BranchListResult> ListBranchesAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The Sonar project key (for example myorg_myrepo) - the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it.")]
        string? projectKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var project = ToolDefaults.ResolveProject(projectKey, options);
        var context = new ToolCallContext("listBranches", project);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client.ListBranchesAsync(project, cancellationToken).ConfigureAwait(false);

            return ResultMapper.Branches(response, project, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "listPullRequests",
        Title = "List pull requests",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists the pull requests SonarQube has analysed for a project, with each one's quality gate status and " +
        "new-code issue counts. Each entry's `key` is the value to pass back as pullRequest on the other " +
        "tools. This is the tool to call when reviewing a pull request: its issues and measures are about new " +
        "code only, which is what a gate judges. Not paginated - SonarQube returns every analysed pull request.")]
    public static async Task<PullRequestListResult> ListPullRequestsAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The Sonar project key (for example myorg_myrepo) - the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it.")]
        string? projectKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var project = ToolDefaults.ResolveProject(projectKey, options);
        var context = new ToolCallContext("listPullRequests", project);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client.ListPullRequestsAsync(project, cancellationToken).ConfigureAwait(false);

            return ResultMapper.PullRequests(response, project, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getQualityGateStatus",
        Title = "Get quality gate status",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Reads the quality gate verdict for a project, branch or pull request: OK, ERROR, or NONE when no gate " +
        "has been computed yet. failingConditions is the pre-filtered list of what is actually wrong, each with " +
        "the metric, the threshold and the measured value - that is the shortest path from \"the gate is red\" " +
        "to \"here is what to fix\". Most conditions apply to new code only (onNewCode), so a red gate on a " +
        "pull request is about the change, not about the project's history.")]
    public static async Task<QualityGateResult> GetQualityGateStatusAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The Sonar project key (for example myorg_myrepo) - the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it.")]
        string? projectKey = null,
        [Description("Analysed branch to read, spelled as listBranches reports it. Mutually exclusive with pullRequest; omit both for the project's main branch.")]
        string? branch = null,
        [Description("Pull request to read, as the SCM number in a string (\"3266\") - the `key` listPullRequests returns. Mutually exclusive with branch; omit both for the main branch.")]
        string? pullRequest = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var project = ToolDefaults.ResolveProject(projectKey, options);
        var scope = ToolDefaults.ResolveScope(branch, pullRequest);

        var context = new ToolCallContext("getQualityGateStatus", project);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client
                .GetQualityGateProjectStatusAsync(project, scope.Branch, scope.PullRequest, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.QualityGate(response, project, scope, options.BaseUrlText);
        }).ConfigureAwait(false);
    }
}
