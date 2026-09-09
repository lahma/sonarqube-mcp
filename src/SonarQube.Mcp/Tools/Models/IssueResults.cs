namespace SonarQube.Mcp.Tools.Models;

/// <summary>
/// One clean-code impact: which software quality an issue harms, and how badly.
/// </summary>
/// <remarks>
/// This is the vocabulary SonarQube filters on today. The legacy <c>type</c> and <c>severity</c>
/// fields still travel alongside it on every issue and say roughly the same thing in words the UI no
/// longer uses.
/// </remarks>
internal sealed record Impact
{
    /// <summary><c>MAINTAINABILITY</c>, <c>RELIABILITY</c> or <c>SECURITY</c>.</summary>
    public string? SoftwareQuality { get; init; }

    /// <summary><c>INFO</c>, <c>LOW</c>, <c>MEDIUM</c>, <c>HIGH</c> or <c>BLOCKER</c>.</summary>
    public string? Severity { get; init; }
}

/// <summary>An issue as a list entry: enough to triage it without a second call.</summary>
internal sealed record IssueSummary
{
    /// <summary>The issue key — what getIssue and every write tool take as <c>issueKey</c>.</summary>
    public string? Key { get; init; }

    /// <summary>The rule that raised it, as <c>repository:S1234</c>. getRule explains it.</summary>
    public string? Rule { get; init; }

    /// <summary>The rule's title, when the response carried the rule sidecar.</summary>
    public string? RuleName { get; init; }

    /// <summary>What the rule flagged here — the actionable sentence.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// The project-relative file path, joined from the response's component sidecar. This is the
    /// path a checkout has on disk; <see cref="Component"/> is the key to pass back to the API.
    /// </summary>
    public string? File { get; init; }

    /// <summary>The primary line. A file-level issue has none.</summary>
    public int? Line { get; init; }

    /// <summary>The last line the issue spans, when it covers more than one.</summary>
    public int? EndLine { get; init; }

    /// <summary>The component key — pass this back as <c>component</c> to scope a search to this file.</summary>
    public string? Component { get; init; }

    /// <summary>The project key.</summary>
    public string? Project { get; init; }

    /// <summary><c>OPEN</c>, <c>CONFIRMED</c>, <c>FALSE_POSITIVE</c>, <c>ACCEPTED</c> or <c>FIXED</c>.</summary>
    public string? IssueStatus { get; init; }

    /// <summary>The clean-code impacts — the severities SonarQube ranks issues by today.</summary>
    public IReadOnlyList<Impact> Impacts { get; init; } = [];

    /// <summary>The legacy severity (<c>MINOR</c>, <c>MAJOR</c>, <c>CRITICAL</c>…), kept because rules still carry it.</summary>
    public string? Severity { get; init; }

    /// <summary>The legacy type: <c>CODE_SMELL</c>, <c>BUG</c> or <c>VULNERABILITY</c>.</summary>
    public string? Type { get; init; }

    /// <summary>The clean-code attribute, for example <c>LOGICAL</c>.</summary>
    public string? CleanCodeAttribute { get; init; }

    /// <summary>Its category: <c>ADAPTABLE</c>, <c>CONSISTENT</c>, <c>INTENTIONAL</c> or <c>RESPONSIBLE</c>.</summary>
    public string? CleanCodeAttributeCategory { get; init; }

    /// <summary>The issue's tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The assignee's <b>login</b>, or absent when unassigned. assignIssue takes this form.</summary>
    public string? Assignee { get; init; }

    /// <summary>SonarQube's remediation estimate, as text (<c>4min</c>).</summary>
    public string? Effort { get; init; }

    /// <summary>When the issue was first raised.</summary>
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset? UpdateDate { get; init; }

    /// <summary>The issue's page in SonarQube.</summary>
    public string? Url { get; init; }
}

/// <summary>One page of issues.</summary>
internal sealed record IssueSearchResult
{
    /// <summary>The issues on this page.</summary>
    public IReadOnlyList<IssueSummary> Issues { get; init; } = [];

    /// <summary>The 1-based page this is.</summary>
    public int Page { get; init; }

    /// <summary>How many items a full page holds.</summary>
    public int PageSize { get; init; }

    /// <summary>Total matching issues, capped by SonarQube at 10000.</summary>
    public int? TotalCount { get; init; }

    /// <summary>Whether another page exists.</summary>
    public bool HasMore { get; init; }

    /// <summary>Set when the result cap is in reach, or the filter needs explaining.</summary>
    public string? Note { get; init; }
}

/// <summary>One comment on an issue.</summary>
internal sealed record IssueComment
{
    /// <summary>The comment key.</summary>
    public string? Key { get; init; }

    /// <summary>The author's login.</summary>
    public string? Author { get; init; }

    /// <summary>The comment as it was written, in SonarQube's markdown dialect.</summary>
    public string? Text { get; init; }

    /// <summary>When it was posted.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>A span of source an issue covers.</summary>
internal sealed record IssueTextRange
{
    /// <summary>First line, 1-based.</summary>
    public int? StartLine { get; init; }

    /// <summary>Last line, 1-based and inclusive.</summary>
    public int? EndLine { get; init; }

    /// <summary>Character offset within the first line, 0-based.</summary>
    public int? StartOffset { get; init; }

    /// <summary>Character offset within the last line, 0-based and exclusive.</summary>
    public int? EndOffset { get; init; }
}

/// <summary>One step in a flow: another place in the code that explains the issue.</summary>
internal sealed record IssueFlowLocation
{
    /// <summary>The project-relative path of the file this step is in.</summary>
    public string? File { get; init; }

    /// <summary>The component key, when the step is in a different file from the issue.</summary>
    public string? Component { get; init; }

    /// <summary>The line.</summary>
    public int? Line { get; init; }

    /// <summary>What this step contributes, when the rule explains itself.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// One flow: an ordered list of locations that together explain an issue — how a null reaches a
/// dereference, how tainted input reaches a sink.
/// </summary>
internal sealed record IssueFlow
{
    /// <summary>The locations, in the order they should be read.</summary>
    public IReadOnlyList<IssueFlowLocation> Locations { get; init; } = [];
}

/// <summary>
/// One issue in full: everything <see cref="IssueSummary"/> carries plus the fields that only a
/// single-issue read returns.
/// </summary>
/// <remarks>
/// <see cref="AvailableTransitions"/> is what makes transitionIssue usable without guessing: it
/// lists the transitions that are legal from this issue's <em>current</em> state, which is not the
/// same as the transitions the tool accepts.
/// </remarks>
internal sealed record IssueDetail
{
    /// <summary>The issue key.</summary>
    public string? Key { get; init; }

    /// <summary>The rule that raised it, as <c>repository:S1234</c>.</summary>
    public string? Rule { get; init; }

    /// <summary>The rule's title.</summary>
    public string? RuleName { get; init; }

    /// <summary>What the rule flagged here.</summary>
    public string? Message { get; init; }

    /// <summary>The project-relative file path.</summary>
    public string? File { get; init; }

    /// <summary>The primary line.</summary>
    public int? Line { get; init; }

    /// <summary>The last line the issue spans.</summary>
    public int? EndLine { get; init; }

    /// <summary>The component key.</summary>
    public string? Component { get; init; }

    /// <summary>The project key.</summary>
    public string? Project { get; init; }

    /// <summary><c>OPEN</c>, <c>CONFIRMED</c>, <c>FALSE_POSITIVE</c>, <c>ACCEPTED</c> or <c>FIXED</c>.</summary>
    public string? IssueStatus { get; init; }

    /// <summary>The clean-code impacts.</summary>
    public IReadOnlyList<Impact> Impacts { get; init; } = [];

    /// <summary>The legacy severity.</summary>
    public string? Severity { get; init; }

    /// <summary>The legacy type.</summary>
    public string? Type { get; init; }

    /// <summary>The clean-code attribute.</summary>
    public string? CleanCodeAttribute { get; init; }

    /// <summary>Its category.</summary>
    public string? CleanCodeAttributeCategory { get; init; }

    /// <summary>The issue's tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The assignee's login.</summary>
    public string? Assignee { get; init; }

    /// <summary>SonarQube's remediation estimate.</summary>
    public string? Effort { get; init; }

    /// <summary>When the issue was first raised.</summary>
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset? UpdateDate { get; init; }

    /// <summary>
    /// The transitions that are legal right now — what transitionIssue's <c>transition</c> argument
    /// may be for this issue. Empty means the credential may not transition it at all.
    /// </summary>
    public IReadOnlyList<string> AvailableTransitions { get; init; } = [];

    /// <summary>The issue's comments, oldest first.</summary>
    public IReadOnlyList<IssueComment> Comments { get; init; } = [];

    /// <summary>Secondary locations, grouped into flows.</summary>
    public IReadOnlyList<IssueFlow> Flows { get; init; } = [];

    /// <summary>The exact span the issue covers.</summary>
    public IssueTextRange? TextRange { get; init; }

    /// <summary>The issue's page in SonarQube.</summary>
    public string? Url { get; init; }
}

/// <summary>One section of a rule's description, converted from HTML to plain text.</summary>
internal sealed record RuleSection
{
    /// <summary><c>introduction</c>, <c>root_cause</c>, <c>assess_the_problem</c>, <c>how_to_fix</c> or <c>resources</c>.</summary>
    public string? Key { get; init; }

    /// <summary>The section's text, with code fenced. Capped, and marked inline where it was cut.</summary>
    public string? Content { get; init; }

    /// <summary>
    /// Which framework this variant applies to, for a rule that documents several. A rule with
    /// contexts returns the same section key more than once, one per context.
    /// </summary>
    public string? Context { get; init; }
}

/// <summary>One rule: what it flags, how severe SonarQube considers it, and how to fix it.</summary>
internal sealed record RuleDetail
{
    /// <summary>The rule key, as <c>repository:S1234</c>.</summary>
    public string? Key { get; init; }

    /// <summary>The rule's title.</summary>
    public string? Name { get; init; }

    /// <summary>The language it applies to.</summary>
    public string? Language { get; init; }

    /// <summary>The rule repository, for example <c>csharpsquid</c>.</summary>
    public string? Repository { get; init; }

    /// <summary>The legacy type: <c>CODE_SMELL</c>, <c>BUG</c>, <c>VULNERABILITY</c> or <c>SECURITY_HOTSPOT</c>.</summary>
    public string? Type { get; init; }

    /// <summary>The legacy severity.</summary>
    public string? Severity { get; init; }

    /// <summary>The clean-code attribute.</summary>
    public string? CleanCodeAttribute { get; init; }

    /// <summary>Its category.</summary>
    public string? CleanCodeAttributeCategory { get; init; }

    /// <summary>The clean-code impacts this rule's issues carry.</summary>
    public IReadOnlyList<Impact> Impacts { get; init; } = [];

    /// <summary>The rule's tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>External standards the rule maps to, for example <c>cwe:476</c>.</summary>
    public IReadOnlyList<string> SecurityStandards { get; init; } = [];

    /// <summary>The requested description sections, in the order they were asked for.</summary>
    public IReadOnlyList<RuleSection> Sections { get; init; } = [];

    /// <summary>Whether any section was cut at the length cap. The cut is also marked in the text.</summary>
    public bool Truncated { get; init; }

    /// <summary>The rule's page in SonarQube.</summary>
    public string? Url { get; init; }

    /// <summary>Set when the descriptions could not be read — see the degraded case in getRule's description.</summary>
    public string? Note { get; init; }
}

/// <summary>A security hotspot as the search endpoint returns it.</summary>
internal sealed record HotspotSummary
{
    /// <summary>The hotspot key — what getHotspot and setHotspotStatus take.</summary>
    public string? Key { get; init; }

    /// <summary>The project-relative file path.</summary>
    public string? File { get; init; }

    /// <summary>The line the hotspot is on.</summary>
    public int? Line { get; init; }

    /// <summary>What the rule flagged.</summary>
    public string? Message { get; init; }

    /// <summary>The security category, for example <c>auth</c> or <c>weak-cryptography</c>.</summary>
    public string? SecurityCategory { get; init; }

    /// <summary><c>LOW</c>, <c>MEDIUM</c> or <c>HIGH</c> — how likely this is to be a real vulnerability.</summary>
    public string? VulnerabilityProbability { get; init; }

    /// <summary><c>TO_REVIEW</c> or <c>REVIEWED</c>.</summary>
    public string? Status { get; init; }

    /// <summary><c>FIXED</c> or <c>SAFE</c>, once reviewed.</summary>
    public string? Resolution { get; init; }

    /// <summary>The rule that raised it.</summary>
    public string? RuleKey { get; init; }

    /// <summary>
    /// The assignee's internal <b>UUID</b>, not a login: the search endpoint reports it that way,
    /// unlike issues and unlike getHotspot. Hence the name — it cannot be passed anywhere that wants
    /// a login.
    /// </summary>
    public string? AssigneeId { get; init; }

    /// <summary>When the hotspot was first raised.</summary>
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset? UpdateDate { get; init; }

    /// <summary>The component key.</summary>
    public string? Component { get; init; }

    /// <summary>The hotspot's page in SonarQube.</summary>
    public string? Url { get; init; }
}

/// <summary>One page of security hotspots.</summary>
internal sealed record HotspotSearchResult
{
    /// <summary>The hotspots on this page.</summary>
    public IReadOnlyList<HotspotSummary> Hotspots { get; init; } = [];

    /// <summary>The 1-based page this is.</summary>
    public int Page { get; init; }

    /// <summary>How many items a full page holds.</summary>
    public int PageSize { get; init; }

    /// <summary>Total matching hotspots.</summary>
    public int? TotalCount { get; init; }

    /// <summary>Whether another page exists.</summary>
    public bool HasMore { get; init; }

    /// <summary>Set only when there is something about this result the caller has to know.</summary>
    public string? Note { get; init; }
}

/// <summary>The rule behind a hotspot, with the three explanations that make it reviewable.</summary>
internal sealed record HotspotRule
{
    /// <summary>The rule key.</summary>
    public string? Key { get; init; }

    /// <summary>The rule's title.</summary>
    public string? Name { get; init; }

    /// <summary>What is risky about the pattern.</summary>
    public string? RiskDescription { get; init; }

    /// <summary>What an attacker could do with it.</summary>
    public string? VulnerabilityDescription { get; init; }

    /// <summary>How to make it safe.</summary>
    public string? FixRecommendations { get; init; }
}

/// <summary>One comment on a hotspot.</summary>
internal sealed record HotspotComment
{
    /// <summary>The comment key.</summary>
    public string? Key { get; init; }

    /// <summary>The author's login.</summary>
    public string? Author { get; init; }

    /// <summary>The comment as it was written.</summary>
    public string? Text { get; init; }

    /// <summary>When it was posted.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>One field change inside a changelog entry.</summary>
internal sealed record HotspotChange
{
    /// <summary>The field that changed, for example <c>status</c> or <c>resolution</c>.</summary>
    public string? Field { get; init; }

    /// <summary>What it was.</summary>
    public string? OldValue { get; init; }

    /// <summary>What it became.</summary>
    public string? NewValue { get; init; }
}

/// <summary>One entry in a hotspot's review history.</summary>
internal sealed record HotspotChangelogEntry
{
    /// <summary>The login of whoever made the change.</summary>
    public string? User { get; init; }

    /// <summary>Their display name.</summary>
    public string? UserName { get; init; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>The fields that changed.</summary>
    public IReadOnlyList<HotspotChange> Changes { get; init; } = [];
}

/// <summary>One security hotspot in full, as the show endpoint returns it.</summary>
/// <remarks>
/// <see cref="Assignee"/> is a <b>login</b> here, where the search endpoint's equivalent field is a
/// UUID. Same concept, two spellings, one API — which is why the two records name the field
/// differently.
/// </remarks>
internal sealed record HotspotDetail
{
    /// <summary>The hotspot key.</summary>
    public string? Key { get; init; }

    /// <summary>The project-relative file path.</summary>
    public string? File { get; init; }

    /// <summary>The line the hotspot is on.</summary>
    public int? Line { get; init; }

    /// <summary>What the rule flagged.</summary>
    public string? Message { get; init; }

    /// <summary><c>TO_REVIEW</c> or <c>REVIEWED</c>.</summary>
    public string? Status { get; init; }

    /// <summary><c>FIXED</c> or <c>SAFE</c>, once reviewed.</summary>
    public string? Resolution { get; init; }

    /// <summary>The security category.</summary>
    public string? SecurityCategory { get; init; }

    /// <summary><c>LOW</c>, <c>MEDIUM</c> or <c>HIGH</c>.</summary>
    public string? VulnerabilityProbability { get; init; }

    /// <summary>The assignee's <b>login</b> on this endpoint, unlike searchHotspots' UUID.</summary>
    public string? Assignee { get; init; }

    /// <summary>The rule, with its three explanations converted from HTML.</summary>
    public HotspotRule? Rule { get; init; }

    /// <summary>The review discussion, oldest first.</summary>
    public IReadOnlyList<HotspotComment> Comments { get; init; } = [];

    /// <summary>Every status change, with who made it and when.</summary>
    public IReadOnlyList<HotspotChangelogEntry> Changelog { get; init; } = [];

    /// <summary>
    /// Whether this credential may change the status. Read it before calling setHotspotStatus:
    /// <see langword="false"/> means that call would be a 403.
    /// </summary>
    public bool? CanChangeStatus { get; init; }

    /// <summary>The hotspot's page in SonarQube.</summary>
    public string? Url { get; init; }
}

/// <summary>One grouped count within a <c>summarizeIssues</c> result.</summary>
internal sealed record IssueFacetBucket
{
    /// <summary>The value the issues share, already translated out of any internal identifier.</summary>
    public string? Value { get; init; }

    /// <summary>A human-readable expansion of <see cref="Value"/>, when one exists — a rule's title.</summary>
    public string? Label { get; init; }

    /// <summary>How many issues are in this bucket, or how many minutes of effort under effort mode.</summary>
    public int Count { get; init; }
}

/// <summary>
/// One field's grouped counts.
/// </summary>
/// <remarks>
/// <see cref="Truncated"/> is not cosmetic: SonarQube caps a facet at a hundred values with no
/// marker of its own, so a rule ranking that stops at a hundred would otherwise read as the complete
/// list of rules the project breaks.
/// </remarks>
internal sealed record IssueFacet
{
    /// <summary>The field the counts are grouped by, spelled as the tool's own groupBy takes it.</summary>
    public string? GroupBy { get; init; }

    /// <summary>The buckets, largest first.</summary>
    public IReadOnlyList<IssueFacetBucket> Buckets { get; init; } = [];

    /// <summary>Whether SonarQube's hundred-value cap truncated this facet.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// The result of <c>summarizeIssues</c>: how the matching issues distribute, without listing them.
/// </summary>
/// <remarks>
/// <see cref="MatchingIssues"/> is the number of issues the filters actually matched. It is
/// deliberately not derivable from the buckets, because a facet is computed with its own filter
/// removed — see the tool description, where that is explained to the model that has to read these
/// numbers. It is also not called <c>totalCount</c>: that name belongs to the paging envelope every
/// paginated result carries, and this result does not page.
/// </remarks>
internal sealed record IssueSummaryResult
{
    /// <summary>The project the counts are for.</summary>
    public string? ProjectKey { get; init; }

    /// <summary>The branch the counts are for, when one was given.</summary>
    public string? Branch { get; init; }

    /// <summary>The pull request the counts are for, when one was given.</summary>
    public string? PullRequest { get; init; }

    /// <summary>How many issues matched the filters, before any grouping.</summary>
    public int MatchingIssues { get; init; }

    /// <summary>Total remediation effort of the matching issues, in minutes.</summary>
    public int? TotalEffortMinutes { get; init; }

    /// <summary>Whether the counts are issue counts or remediation minutes.</summary>
    public string? CountedIn { get; init; }

    /// <summary>The requested groupings, in the order they were asked for.</summary>
    public IReadOnlyList<IssueFacet> Facets { get; init; } = [];

    /// <summary>Anything the caller needs to know to read the numbers correctly.</summary>
    public string? Note { get; init; }

    /// <summary>The project's issue page, for a human.</summary>
    public string? Url { get; init; }
}

/// <summary>One field that moved in a single recorded change to an issue.</summary>
internal sealed record IssueChangeDiff
{
    /// <summary>The field name, for example <c>status</c>, <c>resolution</c> or <c>assignee</c>.</summary>
    public string? Field { get; init; }

    /// <summary>What it was; absent when the field had no previous value.</summary>
    public string? From { get; init; }

    /// <summary>What it became.</summary>
    public string? To { get; init; }
}

/// <summary>One entry in an issue's history.</summary>
internal sealed record IssueChangeEntry
{
    /// <summary>When the change was made.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>The login of whoever made it.</summary>
    public string? Author { get; init; }

    /// <summary>Their display name.</summary>
    public string? AuthorName { get; init; }

    /// <summary>Every field this one change moved. A transition usually moves two.</summary>
    public IReadOnlyList<IssueChangeDiff> Changes { get; init; } = [];
}

/// <summary>
/// The result of <c>getIssueChangelog</c>.
/// </summary>
/// <remarks>
/// An empty <see cref="Entries"/> means one of two different things, which is why
/// <see cref="Note"/> exists: either nobody has ever changed the issue, or the request went out
/// without a credential, because SonarQube answers an anonymous changelog request with an empty
/// list rather than a 401.
/// </remarks>
internal sealed record IssueChangelogResult
{
    /// <summary>The issue the history belongs to.</summary>
    public string? IssueKey { get; init; }

    /// <summary>The changes, oldest first.</summary>
    public IReadOnlyList<IssueChangeEntry> Entries { get; init; } = [];

    /// <summary>Why the list might be empty, when it is.</summary>
    public string? Note { get; init; }

    /// <summary>The issue's page, for a human.</summary>
    public string? Url { get; init; }
}
