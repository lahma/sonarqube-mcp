using System.Globalization;

using ModelContextProtocol;

using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;

namespace SonarQube.Mcp.Tools;

/// <summary>
/// The analysis a call is scoped to: a branch, a pull request, or neither — which means the
/// project's main branch.
/// </summary>
/// <remarks>
/// The two are mutually exclusive on every SonarQube endpoint that takes them, so they are validated
/// once by <see cref="ToolDefaults.ResolveScope"/> and travel together afterwards. A tool that took
/// them as two loose strings could forward one and forget the other; this struct is what makes that
/// impossible.
/// </remarks>
/// <param name="Branch">The analysed branch name, or <see langword="null"/>.</param>
/// <param name="PullRequest">The SCM pull request number as a string, or <see langword="null"/>.</param>
internal readonly record struct AnalysisScope(string? Branch, string? PullRequest);

/// <summary>
/// The limits and shared argument handling every tool applies before it touches the API.
/// </summary>
/// <remarks>
/// <para>
/// Validation lives here rather than in each tool so that nineteen tools cannot drift into nineteen
/// different opinions about what a page size, a missing project or a legacy severity name means.
/// Everything thrown from this class is already an <see cref="McpException"/>: these are the caller's
/// mistakes, and the message is the fix.
/// </para>
/// <para>
/// Two of these validators exist purely because SonarQube changed its vocabulary and left both
/// spellings in circulation. A model trained on <c>MAJOR</c>/<c>CODE_SMELL</c> will reach for them,
/// and the API answers with a 400 that lists the accepted values without saying which of them means
/// what the caller asked for. <see cref="ResolveImpactSeverities"/> and
/// <see cref="ResolveImpactSoftwareQualities"/> answer that question instead.
/// </para>
/// </remarks>
internal static class ToolDefaults
{
    /// <summary>The environment variable that makes the <c>projectKey</c> parameter optional.</summary>
    internal const string DefaultProjectVariable = "SONARQUBE_MCP_DEFAULT_PROJECT";

    /// <summary>The environment variable that supplies the organization key.</summary>
    internal const string OrganizationVariable = "SONARQUBE_ORG";

    /// <summary>The environment variable that bounds <c>getFileCoverage</c>'s line span.</summary>
    internal const string MaxSourceLinesVariable = "SONARQUBE_MCP_MAX_SOURCE_LINES";

    /// <summary>
    /// Longest rule or hotspot prose section returned, in characters. Truncation is never silent:
    /// the text carries a visible marker and the result carries a flag.
    /// </summary>
    internal const int MaxRuleSectionChars = 4000;

    /// <summary>The <c>metricKeys</c> cap on <c>listComponentMeasures</c> — the API's own limit.</summary>
    internal const int MaxTreeMetricKeys = SonarApiClient.MaxTreeMetricKeys;

    /// <summary>The <c>metricKeys</c> cap on <c>getComponentMeasures</c>.</summary>
    internal const int MaxComponentMetricKeys = SonarApiClient.MaxComponentMetricKeys;

    /// <summary>The default <c>issueStatuses</c> filter: the issues somebody still has to deal with.</summary>
    internal static readonly string[] DefaultIssueStatuses = ["OPEN", "CONFIRMED"];

    /// <summary>The default <c>sections</c> of <c>getRule</c>: what it is, why, and how to fix it.</summary>
    internal static readonly string[] DefaultRuleSections = ["introduction", "root_cause", "how_to_fix"];

    /// <summary>The modern issue statuses. The legacy set is rejected with a mapping, not silently translated.</summary>
    private static readonly string[] IssueStatuses =
        ["OPEN", "CONFIRMED", "FALSE_POSITIVE", "ACCEPTED", "FIXED"];

    /// <summary>The clean-code severity vocabulary.</summary>
    private static readonly string[] ImpactSeverities = ["INFO", "LOW", "MEDIUM", "HIGH", "BLOCKER"];

    /// <summary>The three software qualities an issue can affect.</summary>
    private static readonly string[] SoftwareQualities = ["MAINTAINABILITY", "RELIABILITY", "SECURITY"];

    /// <summary>What <c>issues/search</c> accepts as its <c>s</c> parameter, of the values worth exposing.</summary>
    private static readonly string[] IssueSorts =
        ["CREATION_DATE", "UPDATE_DATE", "CLOSE_DATE", "SEVERITY", "STATUS", "ASSIGNEE", "FILE_LINE"];

    /// <summary>
    /// The issue transitions this server exposes. <c>close</c> is not a user action, and the four
    /// hotspot transitions belong to <c>setHotspotStatus</c>.
    /// </summary>
    private static readonly string[] Transitions =
        ["accept", "confirm", "falsepositive", "reopen", "resolve", "unconfirm", "wontfix"];

    /// <summary>The two states a security hotspot can be in.</summary>
    private static readonly string[] HotspotStatuses = ["TO_REVIEW", "REVIEWED"];

    /// <summary>
    /// The resolutions SonarQube <b>Cloud</b> accepts. <c>ACKNOWLEDGED</c> is documented for
    /// SonarQube Server and rejected here — verified against the API's own parameter metadata.
    /// </summary>
    private static readonly string[] HotspotResolutions = ["FIXED", "SAFE"];

    /// <summary>The five description sections a rule can carry.</summary>
    private static readonly string[] RuleSections =
        ["introduction", "root_cause", "assess_the_problem", "how_to_fix", "resources"];

    /// <summary>
    /// Resolves the project to operate on, falling back to <c>SONARQUBE_MCP_DEFAULT_PROJECT</c>.
    /// </summary>
    /// <exception cref="McpException">Neither the argument nor the environment variable is set.</exception>
    internal static string ResolveProject(string? projectKey, SonarQubeMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(projectKey))
        {
            return projectKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultProject))
        {
            return options.DefaultProject;
        }

        throw new McpException(
            "No project key was given and " + DefaultProjectVariable + " is not set. Pass projectKey — it is " +
            "the `id` parameter in a sonarcloud.io project URL (for example myorg_myrepo), not the repository " +
            "name — or set " + DefaultProjectVariable + " in the environment the MCP client launches this " +
            "server with. Call listProjects to see which keys this organization has.");
    }

    /// <summary>
    /// Returns the configured organization key, which several endpoints require and no tool takes as
    /// a parameter.
    /// </summary>
    /// <remarks>
    /// One organization per server instance: a model cannot discover an organization key it was never
    /// told, and a wrong one is an opaque 400. Two organizations means two entries in the MCP client's
    /// configuration, which is one line of JSON.
    /// </remarks>
    /// <exception cref="McpException"><c>SONARQUBE_ORG</c> is not set.</exception>
    internal static string RequireOrganization(SonarQubeMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.Organization))
        {
            return options.Organization;
        }

        throw new McpException(
            OrganizationVariable + " is not set and this call needs it. It is the organization key — the " +
            "{key} segment of https://sonarcloud.io/organizations/{key} — not the organization's display " +
            "name. Set it in the environment the MCP client launches this server with and restart the " +
            "server; `sonarqube-mcp status` reports what this server currently sees.");
    }

    /// <summary>
    /// Turns a file path or a component key into the component key SonarQube expects.
    /// </summary>
    /// <remarks>
    /// This is the single most important ergonomic decision in the tool layer: a coding agent has
    /// file paths, never component keys. A value that already contains <c>:</c> is a key and is used
    /// verbatim; anything else is treated as a project-relative path, normalised (backslashes to
    /// slashes, a leading <c>./</c> or <c>/</c> stripped) and prefixed with the project key.
    /// </remarks>
    /// <param name="component">A path, a full component key, or <see langword="null"/> for the project itself.</param>
    /// <param name="projectKey">The already-resolved project key.</param>
    /// <exception cref="McpException">The value normalises to nothing.</exception>
    internal static string ResolveComponent(string? component, string projectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);

        if (string.IsNullOrWhiteSpace(component))
        {
            return projectKey;
        }

        var trimmed = component.Trim();

        // A key is anything already carrying the project prefix, and the project key itself is the
        // component that means "the whole project".
        if (trimmed.Contains(':', StringComparison.Ordinal)
            || string.Equals(trimmed, projectKey, StringComparison.Ordinal))
        {
            return trimmed;
        }

        var path = NormalizePath(trimmed);

        if (path.Length == 0)
        {
            throw new McpException(
                $"component does not name anything: '{component}'. Pass a project-relative file or directory " +
                "path (src/Widget.cs, or src\\Widget.cs on Windows), or a full component key " +
                "(myorg_myrepo:src/Widget.cs). Omit it to address the project as a whole; call listComponents " +
                "to see the paths this project has.");
        }

        return projectKey + ":" + path;
    }

    /// <summary>
    /// Turns a path or a component key into the <b>project-relative path</b>, which is what
    /// <c>hotspots/search</c>'s <c>files</c> parameter takes.
    /// </summary>
    /// <remarks>
    /// Verified live 2026-08-11: <c>files=src/App/appsettings.json</c> matches two hotspots, and
    /// <c>files=proj:src/App/appsettings.json</c> matches none. The failure is silent — an empty
    /// result rather than an error — so the conversion has to happen here rather than being left to
    /// whoever passes a key. Every other endpoint in this server takes the key form, which is why
    /// this is a separate method rather than a flag on <see cref="ResolveComponent"/>.
    /// </remarks>
    /// <returns><see langword="null"/> when no component was named.</returns>
    internal static string? ResolveComponentPath(string? component, string projectKey)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return null;
        }

        var key = ResolveComponent(component, projectKey);
        var separator = key.IndexOf(':', StringComparison.Ordinal);

        // The project key itself has no path part: "the whole project" is not a file filter, and
        // sending it as one would match nothing.
        return separator >= 0 && separator + 1 < key.Length ? key[(separator + 1)..] : null;
    }

    /// <summary>Normalises a repository-relative path into the form a component key uses.</summary>
    internal static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path.Trim().Replace('\\', '/');

        // "./src/Widget.cs" and "/src/Widget.cs" are both how a caller spells a path it already has;
        // neither prefix is part of a component key.
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart('/');

        return normalized is "." ? string.Empty : normalized;
    }

    /// <summary>Validates the branch/pull-request pair and bundles it.</summary>
    /// <exception cref="McpException">Both were supplied.</exception>
    internal static AnalysisScope ResolveScope(string? branch, string? pullRequest)
    {
        var resolvedBranch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
        var resolvedPullRequest = string.IsNullOrWhiteSpace(pullRequest) ? null : pullRequest.Trim();

        if (resolvedBranch is not null && resolvedPullRequest is not null)
        {
            throw new McpException(
                "branch and pullRequest are mutually exclusive; pass one or neither, and neither means the " +
                "project's main branch. branch is an analysed branch name from listBranches; pullRequest is " +
                "the pull request number as a string, which is the `key` listPullRequests returns.");
        }

        return new AnalysisScope(resolvedBranch, resolvedPullRequest);
    }

    /// <summary>
    /// Resolves the page and page size a tool will send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clamping <c>pageSize</c> is silent, deliberately: the binding constraint is the model's
    /// context rather than the API's limit, so a caller asking for 500 rows is not making a mistake
    /// worth an error — it is making a request this server answers with fewer rows.
    /// </para>
    /// <para>
    /// The 10,000-result window is <b>not</b> silent, because there is no smaller answer to give: the
    /// requested page does not exist and never will. Enforcing it here rather than letting SonarQube
    /// answer <c>Can return only the first 10000 results. 10100th result asked.</c> is what turns a
    /// fact about the API into an instruction about the filter.
    /// </para>
    /// </remarks>
    /// <exception cref="McpException">The requested page lies beyond the result window.</exception>
    internal static (int Page, int PageSize) ResolvePaging(int? page, int? pageSize, SonarQubeMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolvedPageSize = pageSize is { } size
            ? Math.Clamp(size, 1, options.MaxPageSize)
            : options.DefaultPageSize;

        var resolvedPage = page is { } index ? Math.Max(index, 1) : 1;

        // long: 500 * int.MaxValue overflows an int, and the overflow would wrap into a value that
        // passes the check.
        var lastResult = (long) resolvedPage * resolvedPageSize;

        if (lastResult > SonarApiClient.MaxResultWindow)
        {
            throw new McpException(string.Create(
                CultureInfo.InvariantCulture,
                $"SonarQube Cloud returns only the first {SonarApiClient.MaxResultWindow} results, and page " +
                $"{resolvedPage} at pageSize {resolvedPageSize} asks for result {lastResult}. Paging deeper is " +
                $"not possible — narrow the search instead: scope it with component, add issueStatuses or " +
                $"impactSeverities, or set createdAfter/createdInLast, then start again from page 1."));
        }

        return (resolvedPage, resolvedPageSize);
    }

    /// <summary>Rejects a blank issue key before it becomes a confusing empty result.</summary>
    /// <exception cref="McpException"><paramref name="issueKey"/> is missing.</exception>
    internal static string RequireIssueKey(string? issueKey)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
        {
            throw new McpException(
                "issueKey is required: it is the `key` searchIssues reports for an issue (for example " +
                "AZ_xePOumT_q4T_1FWf8), not the rule key and not a line number.");
        }

        return issueKey.Trim();
    }

    /// <summary>Rejects a blank hotspot key.</summary>
    /// <exception cref="McpException"><paramref name="hotspotKey"/> is missing.</exception>
    internal static string RequireHotspotKey(string? hotspotKey)
    {
        if (string.IsNullOrWhiteSpace(hotspotKey))
        {
            throw new McpException(
                "hotspotKey is required: it is the `key` searchHotspots reports for a security hotspot. " +
                "Hotspots and issues have separate keys — an issue key will not resolve here.");
        }

        return hotspotKey.Trim();
    }

    /// <summary>Validates a rule key, which is always <c>repository:rule</c>.</summary>
    /// <exception cref="McpException">The key is blank or carries no repository prefix.</exception>
    internal static string RequireRuleKey(string? ruleKey)
    {
        if (string.IsNullOrWhiteSpace(ruleKey))
        {
            throw new McpException(
                "ruleKey is required and is spelled repository:rule, for example csharpsquid:S2259. " +
                "searchIssues reports each issue's rule key in exactly that form.");
        }

        var trimmed = ruleKey.Trim();

        if (!trimmed.Contains(':', StringComparison.Ordinal))
        {
            throw new McpException(
                $"ruleKey must be spelled repository:rule, for example csharpsquid:S2259 or java:S1118; got " +
                $"'{trimmed}'. The bare rule number on its own is not enough — searchIssues reports the full " +
                "key on every issue.");
        }

        return trimmed;
    }

    /// <summary>
    /// Validates the <c>issueStatuses</c> filter, defaulting to the issues that are still open work.
    /// </summary>
    /// <exception cref="McpException">A value is not a modern issue status.</exception>
    internal static IReadOnlyList<string> ResolveIssueStatuses(string[]? issueStatuses)
    {
        var cleaned = CleanList(issueStatuses);

        if (cleaned is null)
        {
            return DefaultIssueStatuses;
        }

        var resolved = new List<string>(cleaned.Count);

        foreach (var value in cleaned)
        {
            var normalized = value.ToUpperInvariant();

            if (!IssueStatuses.Contains(normalized, StringComparer.Ordinal))
            {
                throw new McpException(
                    $"issueStatuses does not accept '{value}'. The values are " +
                    string.Join(", ", IssueStatuses) + ". The pre-2024 statuses map onto them: REOPENED is now " +
                    "OPEN, CLOSED is now FIXED, and RESOLVED split into FALSE_POSITIVE, ACCEPTED and FIXED " +
                    "depending on why it was resolved. Omit issueStatuses for the default " +
                    string.Join(",", DefaultIssueStatuses) + ", which is the outstanding work.");
            }

            if (!resolved.Contains(normalized, StringComparer.Ordinal))
            {
                resolved.Add(normalized);
            }
        }

        return resolved;
    }

    /// <summary>Validates the <c>impactSeverities</c> filter.</summary>
    /// <exception cref="McpException">A value belongs to the legacy severity vocabulary, or to neither.</exception>
    internal static IReadOnlyList<string>? ResolveImpactSeverities(string[]? impactSeverities) =>
        ResolveEnumList(
            impactSeverities,
            ImpactSeverities,
            "impactSeverities",
            "The pre-clean-code severities map onto them: MINOR is now LOW, MAJOR is now MEDIUM and " +
            "CRITICAL is now HIGH. INFO and BLOCKER kept their names.");

    /// <summary>Validates the <c>impactSoftwareQualities</c> filter.</summary>
    /// <exception cref="McpException">A value belongs to the legacy issue-type vocabulary, or to neither.</exception>
    internal static IReadOnlyList<string>? ResolveImpactSoftwareQualities(string[]? impactSoftwareQualities) =>
        ResolveEnumList(
            impactSoftwareQualities,
            SoftwareQualities,
            "impactSoftwareQualities",
            "This is the clean-code vocabulary, not the issue types: CODE_SMELL is now MAINTAINABILITY, BUG " +
            "is now RELIABILITY and VULNERABILITY is now SECURITY.");

    /// <summary>Validates the <c>sortBy</c> parameter, defaulting to newest issues first.</summary>
    /// <exception cref="McpException"><paramref name="sortBy"/> is not a sortable field.</exception>
    internal static string ResolveIssueSort(string? sortBy)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            return IssueSorts[0];
        }

        var normalized = sortBy.Trim().ToUpperInvariant();

        if (!IssueSorts.Contains(normalized, StringComparer.Ordinal))
        {
            throw new McpException(
                $"sortBy must be one of {string.Join(", ", IssueSorts)}; got '{sortBy}'. Omit it for " +
                $"{IssueSorts[0]}, and leave ascending at false to see the newest issues first.");
        }

        return normalized;
    }

    /// <summary>Validates the <c>transition</c> parameter of <c>transitionIssue</c>.</summary>
    /// <exception cref="McpException"><paramref name="transition"/> is not an exposed transition.</exception>
    internal static string ResolveTransition(string? transition)
    {
        if (string.IsNullOrWhiteSpace(transition))
        {
            throw new McpException(
                "transition is required: one of " + string.Join(", ", Transitions) + ". Call getIssue first — " +
                "its availableTransitions lists the ones that are legal from the issue's current state.");
        }

        var normalized = transition.Trim().ToLowerInvariant();

        if (!Transitions.Contains(normalized, StringComparer.Ordinal))
        {
            throw new McpException(
                $"transition must be one of {string.Join(", ", Transitions)}; got '{transition}'. accept " +
                "supersedes wontfix — both mark an issue as accepted and accept is the current spelling. " +
                "A hotspot's status is not a transition: use setHotspotStatus. Which of these is legal right " +
                "now depends on the issue's current state, and getIssue reports that as availableTransitions.");
        }

        return normalized;
    }

    /// <summary>
    /// Validates the <c>status</c>/<c>resolution</c> pair of <c>setHotspotStatus</c>.
    /// </summary>
    /// <remarks>
    /// The two are one decision, not two parameters: <c>REVIEWED</c> without a resolution is
    /// incomplete and <c>TO_REVIEW</c> with one is contradictory, and SonarQube reports both as a
    /// bare 400.
    /// </remarks>
    /// <exception cref="McpException">Either value is unaccepted, or the pair is inconsistent.</exception>
    internal static (string Status, string? Resolution) ResolveHotspotStatus(string? status, string? resolution)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            throw new McpException(
                "status is required: TO_REVIEW puts the hotspot back on the review list, REVIEWED closes it " +
                "and needs a resolution of FIXED (the risky code was changed) or SAFE (it was reviewed and is " +
                "not a problem here).");
        }

        var normalizedStatus = status.Trim().ToUpperInvariant();

        if (!HotspotStatuses.Contains(normalizedStatus, StringComparer.Ordinal))
        {
            throw new McpException(
                $"status must be {string.Join(" or ", HotspotStatuses)}; got '{status}'.");
        }

        var normalizedResolution = string.IsNullOrWhiteSpace(resolution)
            ? null
            : resolution.Trim().ToUpperInvariant();

        if (normalizedResolution is not null
            && !HotspotResolutions.Contains(normalizedResolution, StringComparer.Ordinal))
        {
            throw new McpException(
                $"resolution must be {string.Join(" or ", HotspotResolutions)}; got '{resolution}'. " +
                "ACKNOWLEDGED is documented for SonarQube Server and is not accepted on SonarQube Cloud — use " +
                "SAFE for a hotspot that was reviewed and needs no change.");
        }

        if (string.Equals(normalizedStatus, "REVIEWED", StringComparison.Ordinal) && normalizedResolution is null)
        {
            throw new McpException(
                "status=REVIEWED needs a resolution: FIXED when the risky code was changed, SAFE when it was " +
                "reviewed and is not a problem here. Pass one, or use status=TO_REVIEW to leave the hotspot " +
                "open.");
        }

        if (string.Equals(normalizedStatus, "TO_REVIEW", StringComparison.Ordinal) && normalizedResolution is not null)
        {
            throw new McpException(
                "status=TO_REVIEW cannot carry a resolution: a hotspot that still has to be reviewed has not " +
                "been resolved. Drop resolution, or pass status=REVIEWED with it.");
        }

        return (normalizedStatus, normalizedResolution);
    }

    /// <summary>
    /// Validates the <c>status</c>/<c>resolution</c> filter of <c>searchHotspots</c>.
    /// </summary>
    /// <remarks>
    /// Same two value sets as <see cref="ResolveHotspotStatus"/>, different rules: this is a filter,
    /// so either may be omitted and a resolution without a status is a legitimate question ("which
    /// hotspots were closed as safe?").
    /// </remarks>
    /// <exception cref="McpException">Either value is unaccepted.</exception>
    internal static (string? Status, string? Resolution) ResolveHotspotSearchStatus(
        string? status,
        string? resolution)
    {
        string? normalizedStatus = null;

        if (!string.IsNullOrWhiteSpace(status))
        {
            normalizedStatus = status.Trim().ToUpperInvariant();

            if (!HotspotStatuses.Contains(normalizedStatus, StringComparer.Ordinal))
            {
                throw new McpException(
                    $"status must be {string.Join(" or ", HotspotStatuses)}; got '{status}'. Omit it to search " +
                    "both.");
            }
        }

        string? normalizedResolution = null;

        if (!string.IsNullOrWhiteSpace(resolution))
        {
            normalizedResolution = resolution.Trim().ToUpperInvariant();

            if (!HotspotResolutions.Contains(normalizedResolution, StringComparer.Ordinal))
            {
                throw new McpException(
                    $"resolution must be {string.Join(" or ", HotspotResolutions)}; got '{resolution}'. Only a " +
                    "REVIEWED hotspot has one, so a resolution filter never matches a hotspot that is still " +
                    "TO_REVIEW.");
            }
        }

        return (normalizedStatus, normalizedResolution);
    }

    /// <summary>
    /// Validates a metric key list against the endpoint's own cap.
    /// </summary>
    /// <param name="metricKeys">The requested keys; trimmed and deduplicated.</param>
    /// <param name="max">
    /// <see cref="MaxTreeMetricKeys"/> for <c>listComponentMeasures</c> — the API's own
    /// <c>maxValuesAllowed</c> — and <see cref="MaxComponentMetricKeys"/> elsewhere.
    /// </param>
    /// <param name="parameterName">
    /// What the tool calls the parameter. <c>getMeasuresHistory</c>'s really is <c>metrics</c>, and a
    /// message naming a parameter the caller does not have is worse than no message.
    /// </param>
    /// <exception cref="McpException">The list is empty or longer than <paramref name="max"/>.</exception>
    internal static IReadOnlyList<string> RequireMetricKeys(
        string[]? metricKeys,
        int max,
        string parameterName = "metricKeys")
    {
        var cleaned = CleanList(metricKeys)
            ?? throw new McpException(
                $"{parameterName} is required: pass at least one metric key, for example coverage, ncloc, " +
                "duplicated_lines_density or sqale_rating. Call listMetrics to find the key for what you want " +
                "to measure — a key that does not exist comes back as a silently missing measure, not an error.");

        var deduplicated = new List<string>(cleaned.Count);

        foreach (var key in cleaned)
        {
            if (!deduplicated.Contains(key, StringComparer.Ordinal))
            {
                deduplicated.Add(key);
            }
        }

        if (deduplicated.Count > max)
        {
            throw new McpException(string.Create(
                CultureInfo.InvariantCulture,
                $"{parameterName} has {deduplicated.Count} entries but this endpoint accepts at most {max}. " +
                $"Ask for the metrics you actually need and make a second call for the rest; listMetrics shows " +
                $"what each key measures."));
        }

        return deduplicated;
    }

    /// <summary>Validates the two mutually exclusive creation-date filters.</summary>
    /// <exception cref="McpException">Both were given, or <paramref name="createdInLast"/> is malformed.</exception>
    internal static (string? CreatedAfter, string? CreatedInLast) ResolveCreatedFilter(
        string? createdAfter,
        string? createdInLast)
    {
        var after = string.IsNullOrWhiteSpace(createdAfter) ? null : createdAfter.Trim();
        var inLast = string.IsNullOrWhiteSpace(createdInLast) ? null : createdInLast.Trim();

        if (after is not null && inLast is not null)
        {
            throw new McpException(
                "createdAfter and createdInLast are mutually exclusive and SonarQube rejects the combination. " +
                "Use createdAfter for an absolute lower bound (2026-01-31, or a full ISO datetime), or " +
                "createdInLast for a window ending now (7d, 2w, 1m, 1y).");
        }

        if (inLast is not null && !IsRelativePeriod(inLast))
        {
            throw new McpException(
                $"createdInLast must be a whole number followed by d (days), w (weeks), m (months) or y " +
                $"(years) — 7d, 2w, 1m, 1y; got '{inLast}'. For an absolute date use createdAfter instead.");
        }

        return (after, inLast);
    }

    /// <summary>
    /// Turns the <c>scope</c> parameter into the <c>strategy</c>/<c>qualifiers</c> pair the API takes.
    /// </summary>
    /// <remarks>
    /// One comprehensible choice instead of two raw parameters whose combinations mostly return
    /// nothing useful: <c>files</c> walks the whole subtree and keeps only files and test files,
    /// <c>directories</c> lists what is immediately below the component, and <c>all</c> is everything.
    /// </remarks>
    /// <exception cref="McpException"><paramref name="scope"/> is not one of the three.</exception>
    internal static (string Strategy, IReadOnlyList<string>? Qualifiers) ResolveComponentScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return ("leaves", ["FIL", "UTS"]);
        }

        return scope.Trim().ToLowerInvariant() switch
        {
            "files" => ("leaves", ["FIL", "UTS"]),
            "directories" => ("children", ["DIR"]),
            "all" => ("all", null),
            _ => throw new McpException(
                $"scope must be files, directories or all; got '{scope}'. files returns every file and test " +
                "file underneath the component, directories returns the directories immediately below it, and " +
                "all returns every descendant of any kind. Omit it for files."),
        };
    }

    /// <summary>
    /// Resolves the line range <c>getFileCoverage</c> will read.
    /// </summary>
    /// <remarks>
    /// Both ends are always resolved to a concrete number, including when the caller named neither:
    /// the alternative is asking SonarQube for every line of a file whose length is unknown, and the
    /// response carries one object per line. The result record reports the range that was actually
    /// read, so a capped request is visible rather than silent.
    /// </remarks>
    /// <exception cref="McpException">The range is inverted, starts below line 1, or is too wide.</exception>
    internal static (int From, int To) ResolveLineRange(int? from, int? to, SonarQubeMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var start = from ?? 1;

        if (start < 1)
        {
            throw new McpException(string.Create(
                CultureInfo.InvariantCulture,
                $"from must be 1 or greater; got {start}. Source lines are numbered from 1."));
        }

        var end = to ?? start + options.MaxSourceLines - 1;

        if (end < start)
        {
            throw new McpException(string.Create(
                CultureInfo.InvariantCulture,
                $"to must be greater than or equal to from; got from={start} and to={end}."));
        }

        var span = (long) end - start + 1;

        if (span > options.MaxSourceLines)
        {
            throw new McpException(string.Create(
                CultureInfo.InvariantCulture,
                $"from={start} to={end} asks for {span} lines, and this server reads at most " +
                $"{options.MaxSourceLines} in one call ({MaxSourceLinesVariable} sets that). Narrow the range, " +
                $"or raise the variable in this server's environment and restart it."));
        }

        return (start, end);
    }

    /// <summary>Validates the <c>sections</c> parameter of <c>getRule</c>.</summary>
    /// <exception cref="McpException">A value is not one of the five section keys.</exception>
    internal static IReadOnlyList<string> ResolveRuleSections(string[]? sections)
    {
        var cleaned = CleanList(sections);

        if (cleaned is null)
        {
            return DefaultRuleSections;
        }

        var resolved = new List<string>(cleaned.Count);

        foreach (var value in cleaned)
        {
            var normalized = value.ToLowerInvariant();

            if (!RuleSections.Contains(normalized, StringComparer.Ordinal))
            {
                throw new McpException(
                    $"sections does not accept '{value}'. The section keys are " +
                    string.Join(", ", RuleSections) + ". Omit sections for " +
                    string.Join(", ", DefaultRuleSections) + ", which is what the rule flags, why, and how to " +
                    "fix it.");
            }

            if (!resolved.Contains(normalized, StringComparer.Ordinal))
            {
                resolved.Add(normalized);
            }
        }

        return resolved;
    }

    /// <summary>Trims and drops blanks from a repeated string argument.</summary>
    /// <returns><see langword="null"/> when nothing usable was supplied.</returns>
    internal static IReadOnlyList<string>? CleanList(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return null;
        }

        var cleaned = new List<string>(values.Length);

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                cleaned.Add(value.Trim());
            }
        }

        return cleaned.Count == 0 ? null : cleaned;
    }

    /// <summary>
    /// The shared shape of the two enum-list validators: uppercase, check membership, and say what
    /// the old vocabulary maps to when the caller reached for it.
    /// </summary>
    private static List<string>? ResolveEnumList(
        string[]? values,
        string[] allowed,
        string parameterName,
        string legacyHint)
    {
        var cleaned = CleanList(values);

        if (cleaned is null)
        {
            return null;
        }

        var resolved = new List<string>(cleaned.Count);

        foreach (var value in cleaned)
        {
            var normalized = value.ToUpperInvariant();

            if (!allowed.Contains(normalized, StringComparer.Ordinal))
            {
                throw new McpException(
                    $"{parameterName} does not accept '{value}'. The values are {string.Join(", ", allowed)}. " +
                    legacyHint);
            }

            if (!resolved.Contains(normalized, StringComparer.Ordinal))
            {
                resolved.Add(normalized);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Whether a string is SonarQube's relative period form: digits followed by one unit letter.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than a regular expression: it is four lines, and pulling
    /// <c>System.Text.RegularExpressions</c> into a Native AOT binary for one pattern is not a trade
    /// worth making.
    /// </remarks>
    private static bool IsRelativePeriod(string value)
    {
        if (value.Length < 2)
        {
            return false;
        }

        if (value[^1] is not ('d' or 'w' or 'm' or 'y'))
        {
            return false;
        }

        foreach (var c in value.AsSpan(0, value.Length - 1))
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
