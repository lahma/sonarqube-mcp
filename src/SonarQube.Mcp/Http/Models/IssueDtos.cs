using System.Text.Json.Serialization;

using SonarQube.Mcp.Json;

namespace SonarQube.Mcp.Http.Models;

/// <summary>
/// <c>api/issues/search</c>.
/// </summary>
/// <remarks>
/// This response carries both paging shapes at once — the flat <c>total</c>/<c>p</c>/<c>ps</c>
/// trio and a <c>paging</c> object with the same numbers (verified live 2026-08-11) — which is why
/// <see cref="PagedResult"/> has to prefer one rather than assume only one is present.
/// <para>
/// The sidecar arrays are the point of <c>additionalFields</c>: <see cref="Components"/> is what
/// turns each issue's component <em>key</em> into a file path, and <see cref="Rules"/> is what
/// turns its rule key into a rule name. Neither is populated unless the request asks for it.
/// </para>
/// </remarks>
internal sealed record IssuesSearchResponseDto : PagedEnvelopeDto
{
    /// <summary>The issues on this page.</summary>
    [JsonPropertyName("issues")]
    public IReadOnlyList<IssueDto>? Issues { get; init; }

    /// <summary>Every component referenced by an issue on this page, keyed for the path join.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyList<ComponentDto>? Components { get; init; }

    /// <summary>Rule names for the rules on this page. Present only with <c>additionalFields=rules</c>.</summary>
    [JsonPropertyName("rules")]
    public IReadOnlyList<RuleRefDto>? Rules { get; init; }

    /// <summary>Assignee display names. Present only with <c>additionalFields=users</c>.</summary>
    [JsonPropertyName("users")]
    public IReadOnlyList<UserRefDto>? Users { get; init; }

    /// <summary>Facet counts. Present only when <c>facets</c> was requested; never by this server.</summary>
    [JsonPropertyName("facets")]
    public IReadOnlyList<FacetDto>? Facets { get; init; }

    /// <summary>Total remediation effort of the matching issues, in minutes.</summary>
    [JsonPropertyName("effortTotal")]
    public int? EffortTotal { get; init; }

    /// <summary>The same number under its pre-clean-code name; SonarQube still sends both.</summary>
    [JsonPropertyName("debtTotal")]
    public int? DebtTotal { get; init; }
}

/// <summary>
/// One issue.
/// </summary>
/// <remarks>
/// <para>
/// Two vocabularies coexist here and both are sent on every issue. The modern one is
/// <see cref="IssueStatus"/> + <see cref="Impacts"/> + <see cref="CleanCodeAttribute"/>; the legacy
/// one is <see cref="Status"/> + <see cref="Resolution"/> + <see cref="Severity"/> +
/// <see cref="Type"/>. They do not map one-to-one — <c>issueStatus: ACCEPTED</c> is
/// <c>status: RESOLVED</c> plus <c>resolution: WONTFIX</c> — so both are kept and the tool layer
/// decides which to show.
/// </para>
/// <para>
/// <see cref="Component"/> is a <em>key</em>, not a path. Resolving it needs the sibling
/// <see cref="IssuesSearchResponseDto.Components"/> array.
/// </para>
/// <para>
/// <see cref="Assignee"/> is a <b>login</b> (<c>ada@github</c>). The identically named field on a
/// hotspot search result is a UUID. Verified live 2026-08-11.
/// </para>
/// </remarks>
internal sealed record IssueDto
{
    /// <summary>The issue key, stable across analyses.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The rule that raised it, as <c>repository:S1234</c>.</summary>
    [JsonPropertyName("rule")]
    public string? Rule { get; init; }

    /// <summary>Legacy severity: <c>INFO</c>, <c>MINOR</c>, <c>MAJOR</c>, <c>CRITICAL</c>, <c>BLOCKER</c>.</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    /// <summary>The component <em>key</em> the issue is on.</summary>
    [JsonPropertyName("component")]
    public string? Component { get; init; }

    /// <summary>The project key.</summary>
    [JsonPropertyName("project")]
    public string? Project { get; init; }

    /// <summary>The project's display name.</summary>
    [JsonPropertyName("projectName")]
    public string? ProjectName { get; init; }

    /// <summary>The primary line, when the issue is on one. File-level issues have none.</summary>
    [JsonPropertyName("line")]
    public int? Line { get; init; }

    /// <summary>Hash of the line's contents, used by SonarQube to track the issue across edits.</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; init; }

    /// <summary>The exact span the issue covers.</summary>
    [JsonPropertyName("textRange")]
    public TextRangeDto? TextRange { get; init; }

    /// <summary>Secondary locations, grouped into flows. Empty for most rules, large for data-flow ones.</summary>
    [JsonPropertyName("flows")]
    public IReadOnlyList<FlowDto>? Flows { get; init; }

    /// <summary>Legacy status: <c>OPEN</c>, <c>CONFIRMED</c>, <c>REOPENED</c>, <c>RESOLVED</c>, <c>CLOSED</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>
    /// Modern status: <c>OPEN</c>, <c>CONFIRMED</c>, <c>FALSE_POSITIVE</c>, <c>ACCEPTED</c>,
    /// <c>FIXED</c>. Note the underscore — the legacy <see cref="Resolution"/> spells the same idea
    /// <c>FALSE-POSITIVE</c>, with a hyphen.
    /// </summary>
    [JsonPropertyName("issueStatus")]
    public string? IssueStatus { get; init; }

    /// <summary>Legacy resolution: <c>FALSE-POSITIVE</c>, <c>WONTFIX</c>, <c>FIXED</c>, <c>REMOVED</c>.</summary>
    [JsonPropertyName("resolution")]
    public string? Resolution { get; init; }

    /// <summary>The rule's message for this occurrence — the actionable sentence.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Estimated remediation time, as text (<c>4min</c>).</summary>
    [JsonPropertyName("effort")]
    public string? Effort { get; init; }

    /// <summary>The same estimate under its pre-clean-code name.</summary>
    [JsonPropertyName("debt")]
    public string? Debt { get; init; }

    /// <summary>Assignee <b>login</b>, or absent when unassigned.</summary>
    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    /// <summary>The SCM author of the line, which is not the same thing as the assignee.</summary>
    [JsonPropertyName("author")]
    public string? Author { get; init; }

    /// <summary>Issue tags, which are the rule's tags plus any added by hand.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// The transitions legal from the issue's <em>current</em> state. Present only with
    /// <c>additionalFields=transitions</c>, and the reason <c>getIssue</c> exists as a separate tool
    /// from <c>searchIssues</c>.
    /// </summary>
    [JsonPropertyName("transitions")]
    public IReadOnlyList<string>? Transitions { get; init; }

    /// <summary>The actions the current user may take. Present only with <c>additionalFields=actions</c>.</summary>
    [JsonPropertyName("actions")]
    public IReadOnlyList<string>? Actions { get; init; }

    /// <summary>The issue's comments. Present only with <c>additionalFields=comments</c>.</summary>
    [JsonPropertyName("comments")]
    public IReadOnlyList<IssueCommentDto>? Comments { get; init; }

    /// <summary>When the issue was first raised.</summary>
    [JsonPropertyName("creationDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>When it last changed.</summary>
    [JsonPropertyName("updateDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? UpdateDate { get; init; }

    /// <summary>When it was closed, for a closed issue.</summary>
    [JsonPropertyName("closeDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? CloseDate { get; init; }

    /// <summary>Legacy type: <c>CODE_SMELL</c>, <c>BUG</c>, <c>VULNERABILITY</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The organization key.</summary>
    [JsonPropertyName("organization")]
    public string? Organization { get; init; }

    /// <summary>Clean-code attribute, for example <c>DISTINCT</c>.</summary>
    [JsonPropertyName("cleanCodeAttribute")]
    public string? CleanCodeAttribute { get; init; }

    /// <summary>Its category: <c>ADAPTABLE</c>, <c>CONSISTENT</c>, <c>INTENTIONAL</c>, <c>RESPONSIBLE</c>.</summary>
    [JsonPropertyName("cleanCodeAttributeCategory")]
    public string? CleanCodeAttributeCategory { get; init; }

    /// <summary>The modern severity vocabulary: one entry per affected software quality.</summary>
    [JsonPropertyName("impacts")]
    public IReadOnlyList<ImpactDto>? Impacts { get; init; }

    /// <summary>Whether SonarQube can apply a fix automatically in the IDE.</summary>
    [JsonPropertyName("quickFixAvailable")]
    public bool? QuickFixAvailable { get; init; }

    /// <summary>Which language-specific variant of the rule description applies here.</summary>
    [JsonPropertyName("ruleDescriptionContextKey")]
    public string? RuleDescriptionContextKey { get; init; }

    /// <summary><c>MAIN</c> or <c>TEST</c> — which source set the component belongs to.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

/// <summary>One comment on an issue.</summary>
internal sealed record IssueCommentDto
{
    /// <summary>The comment key, which is what identifies it for a later edit or delete.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The author's login.</summary>
    [JsonPropertyName("login")]
    public string? Login { get; init; }

    /// <summary>The comment rendered to HTML.</summary>
    [JsonPropertyName("htmlText")]
    public string? HtmlText { get; init; }

    /// <summary>The comment as the author typed it, in SonarQube's markdown dialect.</summary>
    [JsonPropertyName("markdown")]
    public string? Markdown { get; init; }

    /// <summary>Whether the current user may edit it.</summary>
    [JsonPropertyName("updatable")]
    public bool? Updatable { get; init; }

    /// <summary>When it was posted.</summary>
    [JsonPropertyName("createdAt")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>
/// One flow: an ordered list of secondary locations that together explain an issue.
/// </summary>
/// <remarks>
/// A data-flow rule can attach a dozen of these to a single issue, each with its own locations —
/// which is why a fixture of two issues can be five kilobytes and why the tool layer summarises
/// rather than forwards them.
/// </remarks>
internal sealed record FlowDto
{
    /// <summary>The locations, in the order they should be read.</summary>
    [JsonPropertyName("locations")]
    public IReadOnlyList<FlowLocationDto>? Locations { get; init; }
}

/// <summary>One secondary location within a flow.</summary>
internal sealed record FlowLocationDto
{
    /// <summary>The component key the location is in — not necessarily the issue's own component.</summary>
    [JsonPropertyName("component")]
    public string? Component { get; init; }

    /// <summary>The span.</summary>
    [JsonPropertyName("textRange")]
    public TextRangeDto? TextRange { get; init; }

    /// <summary>What this step contributes, when the rule explains itself.</summary>
    [JsonPropertyName("msg")]
    public string? Msg { get; init; }
}

/// <summary>A rule as it appears in a sidecar <c>rules[]</c> array: enough to name it, nothing more.</summary>
internal sealed record RuleRefDto
{
    /// <summary><c>repository:S1234</c>.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The rule's title.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Language key, for example <c>cs</c>.</summary>
    [JsonPropertyName("lang")]
    public string? Lang { get; init; }

    /// <summary>Language display name, for example <c>C#</c>.</summary>
    [JsonPropertyName("langName")]
    public string? LangName { get; init; }

    /// <summary><c>READY</c>, <c>BETA</c>, <c>DEPRECATED</c> or <c>REMOVED</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>A user as it appears in a sidecar <c>users[]</c> array.</summary>
internal sealed record UserRefDto
{
    /// <summary>The login, which is what an issue's <c>assignee</c> holds.</summary>
    [JsonPropertyName("login")]
    public string? Login { get; init; }

    /// <summary>The display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Gravatar-style avatar hash.</summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; init; }

    /// <summary>Whether the account is still active.</summary>
    [JsonPropertyName("active")]
    public bool? Active { get; init; }
}

/// <summary>One facet: a count of matching issues, grouped by a field.</summary>
/// <remarks>
/// Nothing here requests facets — they are large and answer a question a coding agent does not ask.
/// The DTO exists so that a response carrying <c>"facets":[]</c> (which every issues search does)
/// deserialises without the property being silently ignored.
/// </remarks>
internal sealed record FacetDto
{
    /// <summary>The field the counts are grouped by.</summary>
    [JsonPropertyName("property")]
    public string? Property { get; init; }

    /// <summary>The counts.</summary>
    [JsonPropertyName("values")]
    public IReadOnlyList<FacetValueDto>? Values { get; init; }
}

/// <summary>One value within a facet.</summary>
internal sealed record FacetValueDto
{
    /// <summary>The value.</summary>
    [JsonPropertyName("val")]
    public string? Val { get; init; }

    /// <summary>How many issues carry it.</summary>
    [JsonPropertyName("count")]
    public int? Count { get; init; }
}

/// <summary>
/// The shared response of <c>issues/do_transition</c>, <c>issues/assign</c> and
/// <c>issues/add_comment</c>: the updated issue plus the same sidecar arrays a search returns.
/// </summary>
/// <remarks>
/// One DTO for all three because SonarQube genuinely answers all three identically. That is also
/// what makes the write tools able to return the issue's <em>new</em> state without a second call —
/// with the exception of <c>hotspots/change_status</c>, which answers <c>204</c> and forces one.
/// </remarks>
internal sealed record IssueOperationResponseDto
{
    /// <summary>The issue as it stands after the operation.</summary>
    [JsonPropertyName("issue")]
    public IssueDto? Issue { get; init; }

    /// <summary>Components referenced by the issue, for the path join.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyList<ComponentDto>? Components { get; init; }

    /// <summary>The issue's rule.</summary>
    [JsonPropertyName("rules")]
    public IReadOnlyList<RuleRefDto>? Rules { get; init; }

    /// <summary>Users referenced by the issue.</summary>
    [JsonPropertyName("users")]
    public IReadOnlyList<UserRefDto>? Users { get; init; }
}
