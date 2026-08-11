using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Logging;

using SonarQube.Mcp.Authentication;
using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http.Models;

namespace SonarQube.Mcp.Http;

/// <summary>
/// The only thing in this server that talks to SonarQube Cloud. One instance, one
/// <see cref="HttpClient"/>, one hand-built handler chain (D5 — no <c>IHttpClientFactory</c>, which
/// exists to manage many named clients we do not have).
/// </summary>
/// <remarks>
/// <para>
/// The chain is <see cref="AuthenticationHandler"/> → <see cref="RetryHandler"/> →
/// <see cref="SocketsHttpHandler"/>, built here rather than by DI so that the ordering is visible in
/// one place. Authentication sits outermost so that each retry attaches the header itself rather
/// than replaying whatever the first attempt happened to carry.
/// </para>
/// <para>
/// Every response is deserialised straight from the response stream through
/// <see cref="SonarWireJsonContext"/>. No intermediate JSON strings exist anywhere on the success
/// path; the one place a body becomes a string is a failed request, where the raw text is what makes
/// the error diagnosable.
/// </para>
/// <para>
/// Failures: a non-2xx becomes a <see cref="SonarApiException"/> carrying the status, the parsed
/// error envelope <em>when there was one</em>, the raw body capped at 16 KiB, and how many retries
/// the request already cost. The one exception is a <c>401</c> with no token configured, which
/// becomes an <see cref="AuthenticationRequiredException"/> — the same status means two different
/// things and only one of them is fixed by editing an argument.
/// </para>
/// <para>
/// The argument guards here throw <see cref="ArgumentException"/> and friends rather than
/// <c>McpException</c>. This layer has no opinion about MCP; the tool layer wraps what escapes.
/// </para>
/// </remarks>
internal sealed class SonarApiClient : IDisposable
{
    /// <summary>
    /// SonarQube Cloud's hard ceiling on <c>ps</c>. Exceeding it is a 400 reading
    /// <c>'ps' value (501) must be less than 500</c> (verified live 2026-08-11).
    /// </summary>
    internal const int MaxPageSize = 500;

    /// <summary>
    /// How deep any paginated endpoint can be read. <c>p * ps</c> beyond this is a 400 reading
    /// <c>Can return only the first 10000 results. 10100th result asked.</c> (verified live
    /// 2026-08-11). Enforced before the request leaves the process so the failure can say what to do
    /// instead of paging.
    /// </summary>
    internal const int MaxResultWindow = 10_000;

    /// <summary>The <c>metricKeys</c> cap on <c>measures/component_tree</c>, from the API's own metadata.</summary>
    internal const int MaxTreeMetricKeys = 15;

    /// <summary>The <c>metricKeys</c> cap this server applies to <c>measures/component</c>.</summary>
    internal const int MaxComponentMetricKeys = 25;

    /// <summary>
    /// The <c>f</c> list <c>rules/search</c> is asked for. <c>descriptionSections</c> is the reason
    /// this endpoint is used at all — <c>rules/show</c> has no <c>f</c> parameter on Cloud.
    /// </summary>
    internal const string RuleFields =
        "descriptionSections,name,cleanCodeAttribute,impacts,repo,langName,securityStandards,tags";

    private const string ProjectUrl = "https://github.com/lahma/sonarqube-mcp";

    private readonly HttpClient _httpClient;
    private readonly StaticTokenCredential _credential;
    private readonly SonarQubeMcpOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// The same clock <see cref="RetryHandler"/> uses, kept because an HTTP-date <c>Retry-After</c>
    /// on the response that finally failed is only a number of seconds relative to "now".
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The production constructor: builds its own transport. This is the one the container binds —
    /// the test constructor below cannot be satisfied from the service collection because
    /// <see cref="HttpMessageHandler"/> is not registered.
    /// </summary>
    internal SonarApiClient(
        SonarQubeMcpOptions options,
        StaticTokenCredential credential,
        ILoggerFactory loggerFactory)
        : this(options, credential, loggerFactory, CreateTransport(), baseAddress: null, timeProvider: null)
    {
    }

    /// <summary>
    /// The test constructor: takes the innermost handler and, optionally, a different base address
    /// and clock.
    /// </summary>
    /// <param name="options">Resolved configuration: base URL, timeout, and whether a token exists.</param>
    /// <param name="credential">Supplies the <c>Authorization</c> header per request.</param>
    /// <param name="loggerFactory">Source of the handlers' loggers.</param>
    /// <param name="transport">
    /// The innermost handler. Disposed with this client. In production a
    /// <see cref="SocketsHttpHandler"/>; in tests, a stub.
    /// </param>
    /// <param name="baseAddress">
    /// Overrides <see cref="SonarQubeMcpOptions.BaseUrl"/>. Tests use it to keep fixture URLs off
    /// the real host; it must end with a slash or the last path segment is dropped when relative
    /// URLs are resolved against it.
    /// </param>
    /// <param name="timeProvider">Clock and delay source for <see cref="RetryHandler"/>.</param>
    internal SonarApiClient(
        SonarQubeMcpOptions options,
        StaticTokenCredential credential,
        ILoggerFactory loggerFactory,
        HttpMessageHandler transport,
        Uri? baseAddress = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(transport);

        _options = options;
        _credential = credential;
        _logger = loggerFactory.CreateLogger<SonarApiClient>();
        _timeProvider = timeProvider ?? TimeProvider.System;

        var retry = new RetryHandler(loggerFactory.CreateLogger<RetryHandler>(), timeProvider)
        {
            InnerHandler = transport,
        };

        var authentication = new AuthenticationHandler(credential)
        {
            InnerHandler = retry,
        };

        _httpClient = new HttpClient(authentication, disposeHandler: true)
        {
            BaseAddress = baseAddress ?? options.BaseUrl,
            Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds),
        };

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // TryParseAdd rather than Add: a malformed User-Agent is not worth failing every request
        // over, and the version string comes from an assembly attribute we do not fully control.
        _ = _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(
            $"{ServerVersion.Name}/{ServerVersion.Value} (+{ProjectUrl})");

        // Deliberately absent: DefaultRequestHeaders.Authorization. See AuthenticationHandler.
    }

    // ------------------------------------------------------------------ projects and components

    /// <summary>
    /// Lists an organization's projects.
    /// </summary>
    /// <remarks>
    /// <c>components/search</c> rather than <c>projects/search</c>: the latter needs
    /// organization-administration rights and answers <c>401</c> to everyone else, verified live.
    /// <c>qualifiers</c> is undocumented on this action but accepted, also verified live.
    /// </remarks>
    /// <param name="organization">The organization key. Required by the endpoint.</param>
    /// <param name="query">Substring matched against project key and name.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<ComponentsSearchResponseDto> SearchComponentsAsync(
        string organization,
        string? query = null,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        RequirePaging(page, pageSize);

        var url = SonarRequestBuilder.Api("components", "search")
            .Query("organization", organization)
            .Query("qualifiers", "TRK")
            .Query("q", query)
            .Query("p", page)
            .Query("ps", pageSize)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.ComponentsSearchResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Walks a project's component tree.</summary>
    /// <param name="component">The subtree root, as a component key.</param>
    /// <param name="strategy"><c>all</c>, <c>children</c> or <c>leaves</c>.</param>
    /// <param name="qualifiers">Component kinds to include, for example <c>FIL,UTS</c>.</param>
    /// <param name="query">Substring matched against name and path, within the chosen strategy.</param>
    /// <param name="branch">Branch scope; mutually exclusive with <paramref name="pullRequest"/>.</param>
    /// <param name="pullRequest">Pull request scope, as the SCM number.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<ComponentsTreeResponseDto> GetComponentsTreeAsync(
        string component,
        string? strategy = null,
        IReadOnlyList<string>? qualifiers = null,
        string? query = null,
        string? branch = null,
        string? pullRequest = null,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        RequirePaging(page, pageSize);

        var url = SonarRequestBuilder.Api("components", "tree")
            .Query("component", SonarRequestBuilder.RequireComponentKey(component))
            .Query("strategy", strategy)
            .QueryList("qualifiers", qualifiers)
            .Query("q", query)
            .Query("branch", branch)
            .Query("pullRequest", pullRequest)
            .Query("p", page)
            .Query("ps", pageSize)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.ComponentsTreeResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lists a project's analysed branches. The endpoint is not paginated.</summary>
    internal async Task<BranchesListResponseDto> ListBranchesAsync(
        string project,
        CancellationToken cancellationToken = default)
    {
        var url = SonarRequestBuilder.Api("project_branches", "list")
            .Query("project", SonarRequestBuilder.RequireComponentKey(project))
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.BranchesListResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lists a project's analysed pull requests. The endpoint is not paginated.</summary>
    internal async Task<PullRequestsListResponseDto> ListPullRequestsAsync(
        string project,
        CancellationToken cancellationToken = default)
    {
        var url = SonarRequestBuilder.Api("project_pull_requests", "list")
            .Query("project", SonarRequestBuilder.RequireComponentKey(project))
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.PullRequestsListResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the quality gate verdict for a project, branch or pull request.</summary>
    /// <remarks>
    /// <c>{"status":"NONE","conditions":[]}</c> is a successful response describing a scope with no
    /// gate, not a failure.
    /// </remarks>
    internal async Task<ProjectStatusResponseDto> GetQualityGateProjectStatusAsync(
        string projectKey,
        string? branch = null,
        string? pullRequest = null,
        CancellationToken cancellationToken = default)
    {
        var url = SonarRequestBuilder.Api("qualitygates", "project_status")
            .Query("projectKey", SonarRequestBuilder.RequireComponentKey(projectKey))
            .Query("branch", branch)
            .Query("pullRequest", pullRequest)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.ProjectStatusResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ issues

    /// <summary>
    /// Searches issues.
    /// </summary>
    /// <param name="componentKeys">
    /// Project, directory or file keys to scope the search to. Comma-joined on the wire.
    /// </param>
    /// <param name="issues">Specific issue keys — how a single issue is fetched.</param>
    /// <param name="issueStatuses"><c>OPEN, CONFIRMED, FALSE_POSITIVE, ACCEPTED, FIXED</c>.</param>
    /// <param name="impactSeverities"><c>INFO, LOW, MEDIUM, HIGH, BLOCKER</c>.</param>
    /// <param name="impactSoftwareQualities"><c>MAINTAINABILITY, RELIABILITY, SECURITY</c>.</param>
    /// <param name="rules">Rule keys, as <c>repository:S1234</c>.</param>
    /// <param name="tags">Issue tags.</param>
    /// <param name="languages">Language keys.</param>
    /// <param name="assignees">Assignee logins; <c>__me__</c> is accepted by the API.</param>
    /// <param name="createdAfter">Lower bound, as a date or ISO datetime.</param>
    /// <param name="createdInLast">Relative window, for example <c>7d</c>. The API rejects it together with <paramref name="createdAfter"/>.</param>
    /// <param name="inNewCodePeriod">Restricts to the new-code period; sent as <c>sinceLeakPeriod</c>.</param>
    /// <param name="sortBy">A value of the endpoint's <c>s</c> parameter.</param>
    /// <param name="ascending">Sort direction; the endpoint's own default is ascending.</param>
    /// <param name="additionalFields">
    /// Sidecar arrays to include. Never <c>_all</c>, which drags in a fifty-element
    /// <c>languages</c> array on every response.
    /// </param>
    /// <param name="branch">Branch scope; mutually exclusive with <paramref name="pullRequest"/>.</param>
    /// <param name="pullRequest">Pull request scope, as the SCM number.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<IssuesSearchResponseDto> SearchIssuesAsync(
        IReadOnlyList<string>? componentKeys = null,
        IReadOnlyList<string>? issues = null,
        IReadOnlyList<string>? issueStatuses = null,
        IReadOnlyList<string>? impactSeverities = null,
        IReadOnlyList<string>? impactSoftwareQualities = null,
        IReadOnlyList<string>? rules = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? languages = null,
        IReadOnlyList<string>? assignees = null,
        string? createdAfter = null,
        string? createdInLast = null,
        bool? inNewCodePeriod = null,
        string? sortBy = null,
        bool? ascending = null,
        IReadOnlyList<string>? additionalFields = null,
        string? branch = null,
        string? pullRequest = null,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        RequirePaging(page, pageSize);

        var url = SonarRequestBuilder.Api("issues", "search")
            .QueryList("componentKeys", componentKeys)
            .QueryList("issues", issues)
            .QueryList("issueStatuses", issueStatuses)
            .QueryList("impactSeverities", impactSeverities)
            .QueryList("impactSoftwareQualities", impactSoftwareQualities)
            .QueryList("rules", rules)
            .QueryList("tags", tags)
            .QueryList("languages", languages)
            .QueryList("assignees", assignees)
            .Query("createdAfter", createdAfter)
            .Query("createdInLast", createdInLast)
            .Query("sinceLeakPeriod", inNewCodePeriod)
            .Query("s", sortBy)
            .Query("asc", ascending)
            .QueryList("additionalFields", additionalFields)
            .Query("branch", branch)
            .Query("pullRequest", pullRequest)
            .Query("p", page)
            .Query("ps", pageSize)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.IssuesSearchResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a transition to an issue.
    /// </summary>
    /// <remarks>
    /// The endpoint rejects a transition that is not legal from the issue's current state, which is
    /// information worth surfacing rather than swallowing: it almost always means the issue is not
    /// in the state the caller believed.
    /// </remarks>
    /// <param name="issue">The issue key.</param>
    /// <param name="transition">The transition name, lower case.</param>
    /// <param name="comment">A comment posted along with the transition.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<IssueOperationResponseDto> DoIssueTransitionAsync(
        string issue,
        string transition,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issue);
        ArgumentException.ThrowIfNullOrWhiteSpace(transition);

        var body = new FormBody()
            .Add("issue", issue.Trim())
            .Add("transition", transition.Trim())
            .Add("comment", comment);

        return await PostAsync(
                SonarRequestBuilder.Api("issues", "do_transition").Build(),
                body,
                SonarWireJsonContext.Default.IssueOperationResponseDto,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Assigns an issue, or unassigns it.
    /// </summary>
    /// <param name="issue">The issue key.</param>
    /// <param name="assignee">
    /// A <b>login</b> — not a UUID and not a display name. <see langword="null"/> or the empty
    /// string unassigns: both are sent as <c>assignee=</c>, which is what the endpoint reads as "no
    /// assignee". This is the one place <see cref="FormBody"/>'s empty-versus-absent distinction is
    /// deliberately collapsed, because omitting the parameter entirely would leave the current
    /// assignee in place and the caller who passed <see langword="null"/> meant to clear it.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<IssueOperationResponseDto> AssignIssueAsync(
        string issue,
        string? assignee = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issue);

        var body = new FormBody()
            .Add("issue", issue.Trim())
            .Add("assignee", assignee?.Trim() ?? string.Empty);

        return await PostAsync(
                SonarRequestBuilder.Api("issues", "assign").Build(),
                body,
                SonarWireJsonContext.Default.IssueOperationResponseDto,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Adds a comment to an issue. The text is SonarQube-flavoured markdown.</summary>
    internal async Task<IssueOperationResponseDto> AddIssueCommentAsync(
        string issue,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issue);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var body = new FormBody()
            .Add("issue", issue.Trim())
            .Add("text", text);

        return await PostAsync(
                SonarRequestBuilder.Api("issues", "add_comment").Build(),
                body,
                SonarWireJsonContext.Default.IssueOperationResponseDto,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ security hotspots

    /// <summary>
    /// Searches security hotspots.
    /// </summary>
    /// <param name="projectKey">The project to search.</param>
    /// <param name="files">File component keys to narrow to.</param>
    /// <param name="status"><c>TO_REVIEW</c> or <c>REVIEWED</c>.</param>
    /// <param name="resolution"><c>FIXED</c> or <c>SAFE</c>. This endpoint rejects <c>ACKNOWLEDGED</c>.</param>
    /// <param name="onlyMine">Restrict to hotspots assigned to the token's own account.</param>
    /// <param name="inNewCodePeriod">Sent as <c>sinceLeakPeriod</c>.</param>
    /// <param name="branch">
    /// Branch scope. Undocumented on this action but accepted — verified live, including that a
    /// bogus value produces "Project doesn't exist" rather than being ignored.
    /// </param>
    /// <param name="pullRequest">Pull request scope. Undocumented on this action but accepted.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<HotspotsSearchResponseDto> SearchHotspotsAsync(
        string projectKey,
        IReadOnlyList<string>? files = null,
        string? status = null,
        string? resolution = null,
        bool? onlyMine = null,
        bool? inNewCodePeriod = null,
        string? branch = null,
        string? pullRequest = null,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        RequirePaging(page, pageSize);

        var url = SonarRequestBuilder.Api("hotspots", "search")
            .Query("projectKey", SonarRequestBuilder.RequireComponentKey(projectKey))
            .QueryList("files", files)
            .Query("status", status)
            .Query("resolution", resolution)
            .Query("onlyMine", onlyMine)
            .Query("sinceLeakPeriod", inNewCodePeriod)
            .Query("branch", branch)
            .Query("pullRequest", pullRequest)
            .Query("p", page)
            .Query("ps", pageSize)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.HotspotsSearchResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches one hotspot in full. The parameter is <c>hotspot</c>, not <c>hotspotKey</c>.
    /// </summary>
    /// <remarks>
    /// The response shape is <em>not</em> the search shape: <c>component</c> and <c>project</c> are
    /// objects rather than strings, and <c>assignee</c> is a login rather than a UUID. Hence
    /// <see cref="HotspotShowResponseDto"/> as a type of its own.
    /// </remarks>
    internal async Task<HotspotShowResponseDto> ShowHotspotAsync(
        string hotspot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hotspot);

        var url = SonarRequestBuilder.Api("hotspots", "show")
            .Query("hotspot", hotspot.Trim())
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.HotspotShowResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a hotspot's review status.
    /// </summary>
    /// <remarks>
    /// The endpoint answers <c>204</c> with <b>no body</b>, so there is nothing to deserialise and
    /// nothing to return: a caller that wants the resulting state has to re-read it with
    /// <see cref="ShowHotspotAsync"/>.
    /// </remarks>
    /// <param name="hotspot">The hotspot key.</param>
    /// <param name="status"><c>TO_REVIEW</c> or <c>REVIEWED</c>.</param>
    /// <param name="resolution">
    /// <c>FIXED</c> or <c>SAFE</c>, required with <c>REVIEWED</c>. SonarQube Cloud does <b>not</b>
    /// accept <c>ACKNOWLEDGED</c> here, whatever the SonarQube Server documentation says.
    /// </param>
    /// <param name="comment">A comment recorded with the change. Appended every time, so repeating a
    /// no-op status change still adds a comment.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task ChangeHotspotStatusAsync(
        string hotspot,
        string status,
        string? resolution = null,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hotspot);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var body = new FormBody()
            .Add("hotspot", hotspot.Trim())
            .Add("status", status.Trim())
            .Add("resolution", resolution?.Trim())
            .Add("comment", comment);

        var url = SonarRequestBuilder.Api("hotspots", "change_status").Build();

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body.Build() };

        await SendNoContentAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ measures and metrics

    /// <summary>
    /// Reads measures for one component.
    /// </summary>
    /// <remarks>
    /// A metric with no data is <b>silently omitted</b> from the response, so the caller must diff
    /// what came back against what it asked for rather than reading absence as zero.
    /// </remarks>
    /// <param name="component">The component key; the project key reads project-level measures.</param>
    /// <param name="metricKeys">Metric keys; at most <see cref="MaxComponentMetricKeys"/>.</param>
    /// <param name="additionalFields"><c>metrics</c> and/or <c>periods</c> — never <c>period</c> singular, which is a 400.</param>
    /// <param name="branch">Branch scope.</param>
    /// <param name="pullRequest">Pull request scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<MeasuresComponentResponseDto> GetComponentMeasuresAsync(
        string component,
        IReadOnlyList<string> metricKeys,
        IReadOnlyList<string>? additionalFields = null,
        string? branch = null,
        string? pullRequest = null,
        CancellationToken cancellationToken = default)
    {
        RequireMetricKeys(metricKeys, MaxComponentMetricKeys);

        var url = SonarRequestBuilder.Api("measures", "component")
            .Query("component", SonarRequestBuilder.RequireComponentKey(component))
            .QueryList("metricKeys", metricKeys)
            .QueryList("additionalFields", additionalFields)
            .Query("branch", branch)
            .Query("pullRequest", pullRequest)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.MeasuresComponentResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads measures across a component's subtree — the "worst files by metric" query.
    /// </summary>
    /// <param name="component">The subtree root.</param>
    /// <param name="metricKeys">Metric keys; at most <see cref="MaxTreeMetricKeys"/>, which is the API's own limit.</param>
    /// <param name="strategy"><c>all</c>, <c>children</c> or <c>leaves</c>.</param>
    /// <param name="qualifiers">Component kinds to include.</param>
    /// <param name="query">Substring matched against name and path.</param>
    /// <param name="sortByMetric">A metric key to sort by; sent as <c>s=metric</c> plus <c>metricSort</c>.</param>
    /// <param name="ascending">Sort direction.</param>
    /// <param name="additionalFields"><c>metrics</c> and/or <c>periods</c>.</param>
    /// <param name="branch">Branch scope.</param>
    /// <param name="pullRequest">Pull request scope.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<MeasuresComponentTreeResponseDto> GetComponentTreeMeasuresAsync(
        string component,
        IReadOnlyList<string> metricKeys,
        string? strategy = null,
        IReadOnlyList<string>? qualifiers = null,
        string? query = null,
        string? sortByMetric = null,
        bool? ascending = null,
        IReadOnlyList<string>? additionalFields = null,
        string? branch = null,
        string? pullRequest = null,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        RequireMetricKeys(metricKeys, MaxTreeMetricKeys);
        RequirePaging(page, pageSize);

        var builder = SonarRequestBuilder.Api("measures", "component_tree")
            .Query("component", SonarRequestBuilder.RequireComponentKey(component))
            .QueryList("metricKeys", metricKeys)
            .Query("strategy", strategy)
            .QueryList("qualifiers", qualifiers)
            .Query("q", query);

        if (!string.IsNullOrWhiteSpace(sortByMetric))
        {
            // Three parameters that only mean anything together: sorting by a metric needs the sort
            // field set to the literal "metric", the metric named separately, and the filter that
            // drops components with no value for it — otherwise the components without the metric
            // sort to the top and the "worst files" answer is a list of files with no data.
            builder
                .Query("s", "metric")
                .Query("metricSort", sortByMetric.Trim())
                .Query("metricSortFilter", "withMeasuresOnly");
        }

        var url = builder
            .Query("asc", ascending)
            .QueryList("additionalFields", additionalFields)
            .Query("branch", branch)
            .Query("pullRequest", pullRequest)
            .Query("p", page)
            .Query("ps", pageSize)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.MeasuresComponentTreeResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a component's measure history.
    /// </summary>
    /// <remarks>
    /// The parameter is <c>metrics</c>, not <c>metricKeys</c>, and the response's <c>total</c>
    /// counts <b>analyses</b> rather than measures.
    /// </remarks>
    internal async Task<MeasuresHistoryResponseDto> SearchMeasuresHistoryAsync(
        string component,
        IReadOnlyList<string> metrics,
        string? from = null,
        string? to = null,
        string? branch = null,
        string? pullRequest = null,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        RequireMetricKeys(metrics, MaxComponentMetricKeys);
        RequirePaging(page, pageSize);

        var url = SonarRequestBuilder.Api("measures", "search_history")
            .Query("component", SonarRequestBuilder.RequireComponentKey(component))
            .QueryList("metrics", metrics)
            .Query("from", from)
            .Query("to", to)
            .Query("branch", branch)
            .Query("pullRequest", pullRequest)
            .Query("p", page)
            .Query("ps", pageSize)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.MeasuresHistoryResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lists metric definitions.
    /// </summary>
    /// <remarks>
    /// <b>No <c>f</c> parameter is sent.</b> Its documented values are
    /// <c>name, description, domain, direction, qualitative, hidden, decimalScale</c> — <c>type</c>
    /// is <em>not</em> among them, so asking for it is a 400 (verified live 2026-08-11). Omitting
    /// <c>f</c> altogether returns every field including <c>type</c>, which is what this server
    /// needs, so there is nothing to gain by naming a subset.
    /// </remarks>
    internal async Task<MetricsSearchResponseDto> SearchMetricsAsync(
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        RequirePaging(page, pageSize);

        var url = SonarRequestBuilder.Api("metrics", "search")
            .Query("p", page)
            .Query("ps", pageSize)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.MetricsSearchResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ rules and sources

    /// <summary>
    /// Fetches one rule by key, with its description sections when the credential is entitled to
    /// them.
    /// </summary>
    /// <remarks>
    /// <c>rules/search?rule_key=</c> rather than <c>rules/show</c>, because only this action has an
    /// <c>f</c> parameter and therefore only this action can be asked for
    /// <c>descriptionSections</c>. An anonymous request comes back with no sections at all, which
    /// the caller must handle as degraded rather than broken.
    /// </remarks>
    /// <param name="organization">The organization key.</param>
    /// <param name="ruleKey">The rule key, as <c>repository:S1234</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<RulesSearchResponseDto> SearchRulesAsync(
        string organization,
        string ruleKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleKey);

        var url = SonarRequestBuilder.Api("rules", "search")
            .Query("organization", organization)
            .Query("rule_key", ruleKey.Trim())
            .Query("f", RuleFields)
            .Query("ps", 1)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.RulesSearchResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a file's per-line coverage, duplication and new-code facts.
    /// </summary>
    /// <remarks>
    /// The <c>key</c> parameter takes a full component key. The endpoint answers <c>404</c> — not
    /// <c>403</c> — both for a component that does not exist and for one the credential may not
    /// read, so the two cases are indistinguishable from the status alone.
    /// </remarks>
    /// <param name="key">The file's component key.</param>
    /// <param name="from">First line, 1-based.</param>
    /// <param name="to">Last line, inclusive.</param>
    /// <param name="branch">Branch scope.</param>
    /// <param name="pullRequest">Pull request scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<SourcesLinesResponseDto> GetSourceLinesAsync(
        string key,
        int? from = null,
        int? to = null,
        string? branch = null,
        string? pullRequest = null,
        CancellationToken cancellationToken = default)
    {
        var url = SonarRequestBuilder.Api("sources", "lines")
            .Query("key", SonarRequestBuilder.RequireComponentKey(key))
            .Query("from", from)
            .Query("to", to)
            .Query("branch", branch)
            .Query("pullRequest", pullRequest)
            .Build();

        return await GetAsync(url, SonarWireJsonContext.Default.SourcesLinesResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asks SonarQube whether the credential it was sent is valid.
    /// </summary>
    /// <remarks>
    /// <b>It answers <c>{"valid":true}</c> to an anonymous request too</b> (verified live), so it is
    /// a token-<em>rejection</em> probe and never a token-<em>presence</em> one. Callers must check
    /// <see cref="StaticTokenCredential.HasToken"/> before deciding what a <c>true</c> means.
    /// </remarks>
    internal async Task<ValidateResponseDto> ValidateAuthenticationAsync(
        CancellationToken cancellationToken = default)
    {
        var url = SonarRequestBuilder.Api("authentication", "validate").Build();

        return await GetAsync(url, SonarWireJsonContext.Default.ValidateResponseDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    // ------------------------------------------------------------------ plumbing

    private static SocketsHttpHandler CreateTransport() => new()
    {
        // Redirects are not followed anywhere: no api/ endpoint is expected to redirect, and
        // SocketsHttpHandler strips the Authorization header on every automatic redirect — including
        // same-origin ones — so a followed redirect would silently go out unauthenticated. A 3xx
        // becomes a loud error naming the Location instead.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,

        // Bounded so a long-lived server eventually notices DNS changes and load-balancer moves.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),

        // Far tighter than the overall timeout: a connect that has not happened in 15 s will not.
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// Enforces the two paging limits before a request is built, so the failure names the argument
    /// rather than quoting SonarQube back at the caller.
    /// </summary>
    /// <remarks>
    /// The API enforces both itself, and the funnel still maps its 400s in case a future limit
    /// differs. Doing it here as well is what turns "10100th result asked" — which tells a model
    /// nothing about what to do — into a message about narrowing the search.
    /// </remarks>
    private static void RequirePaging(int? page, int? pageSize)
    {
        if (pageSize is { } size)
        {
            if (size < 1 || size > MaxPageSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageSize),
                    size,
                    $"pageSize must be between 1 and {MaxPageSize.ToString(CultureInfo.InvariantCulture)}; " +
                    "SonarQube Cloud rejects anything larger.");
            }
        }

        if (page is { } index && index < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), index, "page is 1-based, so it must be at least 1.");
        }

        if (page is not { } p || pageSize is not { } ps)
        {
            return;
        }

        // long, because 500 * 2^31 overflows int and the overflow would wrap into a value that
        // passes the check.
        var lastResult = (long) p * ps;

        if (lastResult > MaxResultWindow)
        {
            throw new InvalidOperationException(
                $"SonarQube Cloud returns only the first {MaxResultWindow.ToString(CultureInfo.InvariantCulture)} " +
                $"results, and page {p.ToString(CultureInfo.InvariantCulture)} at pageSize " +
                $"{ps.ToString(CultureInfo.InvariantCulture)} asks for result " +
                $"{lastResult.ToString(CultureInfo.InvariantCulture)}. Paging deeper is not possible — narrow the " +
                "search instead, then start again from page 1.");
        }
    }

    private static void RequireMetricKeys(IReadOnlyList<string> metricKeys, int max)
    {
        ArgumentNullException.ThrowIfNull(metricKeys);

        if (metricKeys.Count == 0)
        {
            throw new ArgumentException(
                "at least one metric key is required; call listMetrics to find one.",
                nameof(metricKeys));
        }

        if (metricKeys.Count > max)
        {
            throw new ArgumentException(
                $"{metricKeys.Count.ToString(CultureInfo.InvariantCulture)} metric keys were given but this " +
                $"endpoint accepts at most {max.ToString(CultureInfo.InvariantCulture)}. Ask for fewer metrics.",
                nameof(metricKeys));
        }
    }

    private async Task<T> GetAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        return await SendJsonAsync(request, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> PostAsync<T>(
        string url,
        FormBody body,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body.Build() };

        return await SendJsonAsync(request, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        RetryAttemptCounter attempts,
        CancellationToken cancellationToken)
    {
        request.Options.Set(RetryHandler.RetryAttemptsKey, attempts);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("SonarQube Cloud {Method} {Url}", request.Method.Method, request.RequestUri);
        }

        // ResponseHeadersRead so a large metrics or issues page is streamed into the deserialiser
        // rather than buffered whole before the status code is even looked at.
        return await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<T> SendJsonAsync<T>(
        HttpRequestMessage request,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var attempts = new RetryAttemptCounter();

        using var response = await SendAsync(request, attempts, cancellationToken).ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, attempts.Value, cancellationToken).ConfigureAwait(false);

        return await ReadJsonAsync(response, typeInfo, attempts.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendNoContentAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempts = new RetryAttemptCounter();

        using var response = await SendAsync(request, attempts, cancellationToken).ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, attempts.Value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        int retryAttempts,
        CancellationToken cancellationToken)
    {
        T? value;

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            value = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SonarApiException(
                response.StatusCode,
                SyntheticError("SonarQube Cloud returned a success status with a body this server could not parse."),
                rawBody: null,
                retryAttempts,
                retryAfterSeconds: null,
                ex);
        }

        return value ?? throw new SonarApiException(
            response.StatusCode,
            SyntheticError("SonarQube Cloud returned a success status with an empty body."),
            rawBody: null,
            retryAttempts);
    }

    /// <summary>
    /// Turns a non-2xx response into an exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three cases are separated here because they need three different answers. A <c>3xx</c> is not
    /// a failure the API meant to report — nothing follows redirects, so it becomes an error naming
    /// the <c>Location</c> and pointing at <c>SONARQUBE_URL</c>. A <c>401</c> with no token
    /// configured is the "you never set a token" story, which no status-code table can tell as well
    /// as <see cref="AuthenticationRequiredException"/> does. Everything else is a
    /// <see cref="SonarApiException"/> for the tool-layer funnel to map.
    /// </para>
    /// <para>
    /// The <c>Retry-After</c> read here is the one on the response that finally failed — the retry
    /// handler has already honoured (or declined) the earlier ones, so this is the wait the caller
    /// still has ahead of it.
    /// </para>
    /// </remarks>
    private async Task ThrowIfNotSuccessAsync(
        HttpResponseMessage response,
        int retryAttempts,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int) response.StatusCode;

        if (status is >= 300 and < 400)
        {
            var location = response.Headers.Location?.OriginalString ?? "an unnamed location";

            throw new SonarApiException(
                response.StatusCode,
                SyntheticError(
                    $"SonarQube Cloud redirected this request to {location}, which this server does not follow — " +
                    "the API is not expected to redirect. Check SONARQUBE_URL."),
                rawBody: null,
                retryAttempts);
        }

        // Read before the 401 branch so the body is drained either way; an invalid-token 401 has
        // none, which is exactly the case that must not produce a null dereference downstream.
        var rawBody = await ReadBodySnippetAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized && !_credential.HasToken)
        {
            throw new AuthenticationRequiredException(_options.BaseUrlText);
        }

        throw new SonarApiException(
            response.StatusCode,
            TryParseError(rawBody),
            rawBody,
            retryAttempts,
            RetryHandler.RetryAfterSeconds(response, _timeProvider));
    }

    /// <summary>Synthesises an error envelope for a failure SonarQube did not describe itself.</summary>
    private static ErrorEnvelopeDto SyntheticError(string message) => new()
    {
        Errors = [new ErrorDto { Msg = message }],
    };

    private static ErrorEnvelopeDto? TryParseError(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(rawBody, SonarWireJsonContext.Default.ErrorEnvelopeDto);
        }
        catch (JsonException)
        {
            // Best effort by contract: an HTML maintenance page or a proxy's error body is not JSON,
            // and the raw text travels on the exception regardless.
            return null;
        }
    }

    /// <summary>
    /// Reads at most <see cref="SonarApiException.MaxRawBodyLength"/> bytes of a failed response.
    /// Bounded because this text ends up in a log line and in the model's context, and unbounded
    /// because of a failure is how a bad day becomes an expensive one.
    /// </summary>
    private static async Task<string> ReadBodySnippetAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[SonarApiException.MaxRawBodyLength];

            var read = await stream
                .ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (HttpRequestException)
        {
            // The status code is the useful part of a failure; losing the body to a broken
            // connection must not replace it with a different, less informative exception.
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
