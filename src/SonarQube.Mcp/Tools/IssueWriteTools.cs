using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;
using SonarQube.Mcp.Tools.Models;

namespace SonarQube.Mcp.Tools;

/// <summary>
/// The four tools that change something: triaging an issue, assigning it, commenting on it, and
/// recording a security hotspot's review.
/// </summary>
/// <remarks>
/// <para>
/// The annotations are the load-bearing part of this file. <c>Destructive</c> defaults to
/// <see langword="true"/> in the SDK, so all four say <c>false</c> explicitly — none of them deletes
/// anything, and every one of them is either reversible or additive. Getting that wrong makes a
/// client prompt for confirmation before every comment, which teaches a user to click through the
/// prompts that would have mattered.
/// </para>
/// <para>
/// <c>transitionIssue</c> is the one judgement call. It is <b>not destructive</b>: every transition
/// exposed here is reversible (<c>reopen</c> undoes <c>resolve</c>, <c>falsepositive</c>,
/// <c>wontfix</c> and <c>accept</c>; <c>unconfirm</c> undoes <c>confirm</c>), nothing is deleted, and
/// SonarQube records each change in the issue's changelog with the actor and timestamp. The real
/// objection — that marking an issue false-positive removes it from the organization's counts and can
/// flip a quality gate — is shared-state visibility, which is what <c>openWorldHint</c> already says.
/// It is also <b>not idempotent</b>: the caller names a transition, not an end state, and SonarQube
/// rejects one that is not legal from the issue's current state. That rejection is information — it
/// almost always means the issue is not in the state the model believed — so it is surfaced rather
/// than swallowed, and the tool returns <c>availableTransitions</c> so the next attempt is informed.
/// </para>
/// <para>
/// <c>setHotspotStatus</c> is idempotent because it names a target state rather than a transition;
/// <c>assignIssue</c> is idempotent for the same reason. <c>addIssueComment</c> is not: two calls
/// make two comments.
/// </para>
/// <para>
/// Registration of this whole class is skipped when <c>SONARQUBE_MCP_READ_ONLY</c> is set, so in
/// read-only mode these four tools are absent from <c>tools/list</c> rather than failing when called.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class IssueWriteTools
{
    /// <summary>What the transition re-read asks for when the operation response did not carry it.</summary>
    private static readonly string[] TransitionFields = ["transitions"];

    private IssueWriteTools()
    {
    }

    [McpServerTool(
        Name = "transitionIssue",
        Title = "Transition issue",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Applies a triage transition to an issue: accept (the code stays as it is), confirm, falsepositive, " +
        "reopen, resolve, unconfirm, or the legacy spelling wontfix. The change takes effect immediately on the " +
        "real organization and is visible to everyone — accepting or dismissing an issue removes it from the " +
        "organization's counts and can change a quality gate. It is reversible: reopen undoes the rest, and " +
        "SonarQube records every change in the issue's changelog. A transition is only legal from certain " +
        "states, so call getIssue first and pick from its availableTransitions; the result returns the " +
        "transitions that are legal afterwards.")]
    public static async Task<IssueTransitionResult> TransitionIssueAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The issue key, as searchIssues reports it (for example AZ_xePOumT_q4T_1FWf8).")]
        string issueKey,
        [Description("The transition to apply: accept, confirm, falsepositive, reopen, resolve, unconfirm or wontfix. accept supersedes wontfix. Use getIssue's availableTransitions to see which are legal now.")]
        string transition,
        [Description("A comment posted with the transition, explaining the decision. Strongly recommended for accept and falsepositive — the next reader has only this.")]
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var key = ToolDefaults.RequireIssueKey(issueKey);
        var name = ToolDefaults.ResolveTransition(transition);

        var context = new ToolCallContext(
            "transitionIssue", options.DefaultProject, Component: null, EntityKey: "issue " + key);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client
                .DoIssueTransitionAsync(key, name, comment, cancellationToken)
                .ConfigureAwait(false);

            // The operation response usually carries the new transitions already. When it does not,
            // one extra read is worth it: without them the caller has no way to know what it can do
            // next except by trying transitions and reading the failures.
            var transitions = response.Issue?.Transitions
                ?? await ReadTransitionsAsync(client, key, cancellationToken).ConfigureAwait(false);

            return ResultMapper.Transition(response, transitions, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "assignIssue",
        Title = "Assign issue",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Assigns an issue to a SonarQube account, or clears the assignee. The value is a login (ada@github), " +
        "not a display name and not the UUID searchHotspots reports — searchIssues reports assignees in the " +
        "form this accepts, and __me__ assigns to the account the configured token belongs to. Omit assignee, " +
        "or pass an empty string, to unassign. Assigning the same person twice changes nothing.")]
    public static async Task<IssueAssignResult> AssignIssueAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The issue key, as searchIssues reports it.")]
        string issueKey,
        [Description("The assignee's login (ada@github), or __me__ for the token's own account. Omit it or pass an empty string to unassign.")]
        string? assignee = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var key = ToolDefaults.RequireIssueKey(issueKey);

        var context = new ToolCallContext(
            "assignIssue", options.DefaultProject, Component: null, EntityKey: "issue " + key);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client
                .AssignIssueAsync(key, assignee, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Assign(response, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "addIssueComment",
        Title = "Add issue comment",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Posts a comment on an issue. The comment is visible to everyone with access to the project and cannot " +
        "be deleted through this server. Calling this twice posts two comments — it is not idempotent, so " +
        "never retry a call whose outcome is unknown without reading the issue first with getIssue.")]
    public static async Task<IssueCommentResult> AddIssueCommentAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The issue key, as searchIssues reports it.")]
        string issueKey,
        [Description("The comment, in SonarQube's markdown dialect.")]
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var key = ToolDefaults.RequireIssueKey(issueKey);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new McpException(
                "text is required: SonarQube rejects an empty comment. Pass the note you want the next reader " +
                "of this issue to see.");
        }

        var context = new ToolCallContext(
            "addIssueComment", options.DefaultProject, Component: null, EntityKey: "issue " + key);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var response = await client
                .AddIssueCommentAsync(key, text, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Comment(response, text, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "setHotspotStatus",
        Title = "Set hotspot status",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Records the review of a security hotspot: REVIEWED with resolution SAFE (looked at, not a problem " +
        "here) or FIXED (the risky code was changed), or TO_REVIEW to put it back on the list. This is a " +
        "security decision that a human normally makes, and it takes effect immediately for the whole " +
        "organization — read the hotspot with getHotspot first, and check its canChangeStatus, which is false " +
        "when the account may not do this. ACKNOWLEDGED is not accepted on SonarQube Cloud. Setting the same " +
        "status twice changes nothing, but a comment is appended every time.")]
    public static async Task<HotspotStatusResult> SetHotspotStatusAsync(
        SonarApiClient client,
        SonarQubeMcpOptions options,
        [Description("The hotspot key, as searchHotspots reports it.")]
        string hotspotKey,
        [Description("TO_REVIEW to leave the hotspot open, or REVIEWED to close it (which requires a resolution).")]
        string status,
        [Description("Required with REVIEWED: SAFE when the code was reviewed and is not a problem, FIXED when the risky code was changed. Must be omitted with TO_REVIEW.")]
        string? resolution = null,
        [Description("A comment recorded with the change, explaining the decision. Appended every time this is called, including on a no-op.")]
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var key = ToolDefaults.RequireHotspotKey(hotspotKey);
        var target = ToolDefaults.ResolveHotspotStatus(status, resolution);

        var context = new ToolCallContext(
            "setHotspotStatus", options.DefaultProject, Component: null, EntityKey: "hotspot " + key);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            // The endpoint answers 204 with no body, so the resulting state has to be read back —
            // which is also what makes the result worth structuring: it reports what SonarQube
            // stored rather than echoing what was asked for.
            await client
                .ChangeHotspotStatusAsync(key, target.Status, target.Resolution, comment, cancellationToken)
                .ConfigureAwait(false);

            var hotspot = await client.ShowHotspotAsync(key, cancellationToken).ConfigureAwait(false);

            return ResultMapper.HotspotStatus(hotspot, options.BaseUrlText);
        }).ConfigureAwait(false);
    }

    /// <summary>Re-reads an issue for the transitions that are legal from its new state.</summary>
    private static async Task<IReadOnlyList<string>?> ReadTransitionsAsync(
        SonarApiClient client,
        string issueKey,
        CancellationToken cancellationToken)
    {
        var response = await client.SearchIssuesAsync(
                componentKeys: null,
                issues: [issueKey],
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
                TransitionFields,
                branch: null,
                pullRequest: null,
                page: null,
                pageSize: 1,
                cancellationToken)
            .ConfigureAwait(false);

        return response.Issues is { Count: > 0 } issues ? issues[0].Transitions : null;
    }
}
