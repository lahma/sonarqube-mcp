using System.ComponentModel;

using ModelContextProtocol.Server;

using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;
using SonarQube.Mcp.Tools.Models;

namespace SonarQube.Mcp.Tools;

/// <summary>
/// The numbers: measures for a component, measures across a subtree, their history, the metric
/// catalogue, and per-line coverage.
/// </summary>
/// <remarks>
/// <para>
/// Every method is static, with the client and the options bound from DI and excluded from the
/// generated schema. The class is sealed rather than <c>static</c> so it can be a type argument to
/// <c>WithTools&lt;T&gt;</c>; the private constructor keeps it uninstantiable.
/// </para>
/// <para>
/// Three traps are handled here rather than left to the caller: a metric with no data is omitted
/// from the response rather than returned as zero, new-code values never appear under <c>value</c>,
/// and a <c>*_rating</c> arrives as <c>"1.0"</c> when it means <c>A</c>. All three are corrected in
/// the mapper, and the tool descriptions say so.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class MeasureReadTools
{
    /// <summary>
    /// <c>periods</c> is what carries new-code values. The plural matters: <c>period</c> is a 400.
    /// </summary>
    private static readonly string[] MeasureAdditionalFields = ["periods"];

    private MeasureReadTools()
    {
    }

    [McpServerTool(
        Name = "getComponentMeasures",
        Title = "Get measures",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Reads named metrics for one project, directory or file — coverage, duplication, lines of code, " +
        "ratings, issue counts. Two things to read carefully: missingMetrics lists what came back with no data " +
        "at all, and absent is NOT zero (a missing coverage means coverage is not measured, not that it is 0%); " +
        "and newCodeValue is the value within the new-code period, which is where every new_* metric's number " +
        "lives. Rating metrics are returned as their letter A-E, not as the 1.0-5.0 the API sends. Call " +
        "listMetrics to find metric keys.")]
    public static async Task<MeasuresResult> GetComponentMeasuresAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("Metric keys to read, for example [\"coverage\",\"duplicated_lines_density\",\"sqale_rating\"]. At most 25. Call listMetrics to find them.")]
        string[] metricKeys,
        [Description("The Sonar project key (for example myorg_myrepo) - the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it.")]
        string? projectKey = null,
        [Description("The file or directory to measure, as either a project-relative path (src/Widget.cs) or a full component key (myorg_myrepo:src/Widget.cs). Omit to measure the whole project.")]
        string? component = null,
        [Description("Analysed branch to read, spelled as listBranches reports it. Mutually exclusive with pullRequest; omit both for the project's main branch.")]
        string? branch = null,
        [Description("Pull request to read, as the SCM number in a string (\"3266\") - the `key` listPullRequests returns. Mutually exclusive with branch; omit both for the main branch.")]
        string? pullRequest = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var project = ToolDefaults.ResolveProject(projectKey, options);
        var componentKey = ToolDefaults.ResolveComponent(component, project);
        var metrics = ToolDefaults.RequireMetricKeys(metricKeys, ToolDefaults.MaxComponentMetricKeys);
        var scope = ToolDefaults.ResolveScope(branch, pullRequest);

        var context = new ToolCallContext("getComponentMeasures", project, componentKey);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client.GetComponentMeasuresAsync(
                    componentKey,
                    metrics,
                    MeasureAdditionalFields,
                    scope.Branch,
                    scope.PullRequest,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Measures(response, metrics, project, scope, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "listComponentMeasures",
        Title = "List measures by component",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Reads metrics for every component under a project or directory, one row per file — this is how to " +
        "answer \"which files are worst?\". Pass sortByMetric with the metric to rank by. ascending=false " +
        "(the default) puts the LARGEST values first, which is the worst end for uncovered_lines, complexity " +
        "or duplicated_lines_density and the BEST end for coverage — rank by coverage with ascending=true. " +
        "Components with no value for the sort metric are excluded rather than ranked first, so an empty page " +
        "means nothing under this scope measures it. At most 15 metricKeys here, which is the API's own limit. " +
        "Results are paginated.")]
    public static async Task<ComponentMeasuresResult> ListComponentMeasuresAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("Metric keys to read for each component, for example [\"ncloc\",\"coverage\"]. At most 15 - the API rejects more. Call listMetrics to find them.")]
        string[] metricKeys,
        [Description("The Sonar project key (for example myorg_myrepo) - the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it.")]
        string? projectKey = null,
        [Description("The subtree to walk, as either a project-relative directory path (src/Core) or a full component key. Omit to walk the whole project.")]
        string? component = null,
        [Description("Which components to return: \"files\" (every file and test file, the default), \"directories\" (the directories directly below the component), or \"all\".")]
        string? scope = null,
        [Description("A metric key to rank by, for example \"coverage\" or \"complexity\". Components with no value for it are excluded rather than ranked first.")]
        string? sortByMetric = null,
        [Description("Sort ascending. Defaults to false, which puts the largest values first: the worst end for uncovered_lines, complexity, ncloc and duplicated_lines_density, and the best end for coverage and every other higher-is-better metric. Pass true when ranking by coverage.")]
        bool ascending = false,
        [Description("Substring matched against component name and path.")]
        string? query = null,
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
        var componentKey = ToolDefaults.ResolveComponent(component, project);
        var metrics = ToolDefaults.RequireMetricKeys(metricKeys, ToolDefaults.MaxTreeMetricKeys);
        var selection = ToolDefaults.ResolveComponentScope(scope);
        var analysisScope = ToolDefaults.ResolveScope(branch, pullRequest);
        var paging = ToolDefaults.ResolvePaging(page, pageSize, options);

        // Normalised once and handed to both halves of the call, so the two cannot disagree: the
        // client sends the three sort parameters only when this is non-blank, and the mapper has to
        // know whether metricSortFilter=withMeasuresOnly was in play before it can read an empty
        // page as a statement about one metric rather than about all of them.
        var rankBy = string.IsNullOrWhiteSpace(sortByMetric) ? null : sortByMetric.Trim();

        var context = new ToolCallContext("listComponentMeasures", project, componentKey);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client.GetComponentTreeMeasuresAsync(
                    componentKey,
                    metrics,
                    selection.Strategy,
                    selection.Qualifiers,
                    query,
                    rankBy,
                    ascending,
                    MeasureAdditionalFields,
                    analysisScope.Branch,
                    analysisScope.PullRequest,
                    paging.Page,
                    paging.PageSize,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.TreeMeasures(
                response,
                metrics,
                rankBy,
                project,
                analysisScope,
                paging.Page,
                paging.PageSize,
                options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getMeasuresHistory",
        Title = "Get measures history",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Reads how metrics changed over a project's analysis history — one time series per metric, oldest " +
        "first. Use it to answer whether something is getting better or worse rather than what it is now. A " +
        "point with no value is an analysis where the metric had no data, which is a gap and not a zero. " +
        "totalCount counts analyses, not measures, because the paging is over the analysis history that every " +
        "metric shares.")]
    public static async Task<MeasuresHistoryResult> GetMeasuresHistoryAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("Metric keys to chart, for example [\"coverage\",\"ncloc\"]. Call listMetrics to find them.")]
        string[] metrics,
        [Description("The Sonar project key (for example myorg_myrepo) - the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it.")]
        string? projectKey = null,
        [Description("The file or directory to chart, as either a project-relative path (src/Widget.cs) or a full component key. Omit to chart the whole project.")]
        string? component = null,
        [Description("Only analyses on or after this date: YYYY-MM-DD or a full ISO datetime.")]
        string? from = null,
        [Description("Only analyses on or before this date: YYYY-MM-DD or a full ISO datetime.")]
        string? to = null,
        [Description("Analysed branch to read, spelled as listBranches reports it. Mutually exclusive with pullRequest; omit both for the project's main branch.")]
        string? branch = null,
        [Description("Pull request to read, as the SCM number in a string (\"3266\") - the `key` listPullRequests returns. Mutually exclusive with branch; omit both for the main branch.")]
        string? pullRequest = null,
        [Description("1-based page number over the analysis history. Defaults to 1.")]
        int? page = null,
        [Description("Analyses per page. Clamped to 1-100 by default; SONARQUBE_MCP_MAX_PAGE_SIZE raises the ceiling. Defaults to 50.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var project = ToolDefaults.ResolveProject(projectKey, options);
        var componentKey = ToolDefaults.ResolveComponent(component, project);

        // The API parameter really is `metrics` here, not `metricKeys`, so the tool's parameter is
        // spelled the same way and the error message has to name that spelling.
        var metricKeys = ToolDefaults.RequireMetricKeys(
            metrics, ToolDefaults.MaxComponentMetricKeys, parameterName: "metrics");

        var scope = ToolDefaults.ResolveScope(branch, pullRequest);
        var paging = ToolDefaults.ResolvePaging(page, pageSize, options);

        var context = new ToolCallContext("getMeasuresHistory", project, componentKey);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client.SearchMeasuresHistoryAsync(
                    componentKey,
                    metricKeys,
                    from,
                    to,
                    scope.Branch,
                    scope.PullRequest,
                    paging.Page,
                    paging.PageSize,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.History(response, componentKey, paging.Page, paging.PageSize);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "listMetrics",
        Title = "List metrics",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists the metric keys this SonarQube instance knows, with what each one measures and whether higher is " +
        "better. Call it before the measures tools rather than guessing a key: a key that does not exist is not " +
        "an error, it is a silently missing measure. Filter with query (matched against key, name and " +
        "description) or domain. Metrics whose values are multi-kilobyte per-line blobs are left out unless " +
        "includeDataMetrics is set, and the note says how many. Not paginated — one call returns the catalogue.")]
    public static async Task<MetricListResult> ListMetricsAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("Substring matched against metric key, name and description, for example \"coverage\" or \"duplication\".")]
        string? query = null,
        [Description("Only metrics in this domain, for example \"Coverage\", \"Issues\", \"Duplications\", \"Size\", \"Maintainability\".")]
        string? domain = null,
        [Description("Include DATA and DISTRIB metrics, whose values are large per-line blobs. Defaults to false.")]
        bool includeDataMetrics = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var context = new ToolCallContext("listMetrics");

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            // One call for the whole catalogue: it is about 150 definitions, and fetching it whole is
            // what lets the filter be a substring match rather than the API's non-existent `q`.
            var response = await client
                .SearchMetricsAsync(page: null, pageSize: SonarApiClient.MaxPageSize, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Metrics(response, query, domain, includeDataMetrics);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getFileCoverage",
        Title = "Get file coverage",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Reads one file's per-line coverage: which lines tests never executed, which ran with only some of " +
        "their branches taken, which are inside the new-code period and which are duplicated. This is the one " +
        "thing SonarQube knows that a checkout does not, and it turns \"coverage is too low\" into a list of " +
        "lines to write tests for. The source text is never returned — read the file from disk. Defaults to " +
        "the uncovered and partially covered lines only; an empty result with no coverage data at all is " +
        "reported in the note rather than read as full coverage.")]
    public static async Task<FileCoverageResult> GetFileCoverageAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The file to read, as either a project-relative path (src/Widget.cs) or a full component key (myorg_myrepo:src/Widget.cs). listComponents turns a path you have on disk into a key SonarQube analysed.")]
        string component,
        [Description("The Sonar project key (for example myorg_myrepo) - the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it.")]
        string? projectKey = null,
        [Description("First line to read, 1-based. Defaults to 1.")]
        int? from = null,
        [Description("Last line to read, inclusive. Defaults to from plus the server's line cap (SONARQUBE_MCP_MAX_SOURCE_LINES, 2000 lines).")]
        int? to = null,
        [Description("Return only the lines that are uncovered or partially covered. Defaults to true; pass false for every line in the range.")]
        bool onlyUncovered = true,
        [Description("Analysed branch to read, spelled as listBranches reports it. Mutually exclusive with pullRequest; omit both for the project's main branch.")]
        string? branch = null,
        [Description("Pull request to read, as the SCM number in a string (\"3266\") - the `key` listPullRequests returns. Mutually exclusive with branch; omit both for the main branch.")]
        string? pullRequest = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var project = ToolDefaults.ResolveProject(projectKey, options);
        var componentKey = ToolDefaults.ResolveComponent(component, project);
        var range = ToolDefaults.ResolveLineRange(from, to, options);
        var scope = ToolDefaults.ResolveScope(branch, pullRequest);

        var context = new ToolCallContext("getFileCoverage", project, componentKey);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client.GetSourceLinesAsync(
                    componentKey,
                    range.From,
                    range.To,
                    scope.Branch,
                    scope.PullRequest,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Coverage(
                response,
                componentKey,
                project,
                range.From,
                range.To,
                onlyUncovered,
                scope,
                options.BaseUrlText);
        }).ConfigureAwait(false);
    }
}
