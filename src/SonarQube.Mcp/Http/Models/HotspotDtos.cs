using System.Text.Json.Serialization;

using SonarQube.Mcp.Json;

namespace SonarQube.Mcp.Http.Models;

/// <summary><c>api/hotspots/search</c>.</summary>
internal sealed record HotspotsSearchResponseDto : PagedEnvelopeDto
{
    /// <summary>The hotspots on this page.</summary>
    [JsonPropertyName("hotspots")]
    public IReadOnlyList<HotspotDto>? Hotspots { get; init; }

    /// <summary>Every component referenced above, for the same key-to-path join issues use.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyList<ComponentDto>? Components { get; init; }
}

/// <summary>
/// One security hotspot, <b>as the search endpoint returns it</b>.
/// </summary>
/// <remarks>
/// <para>
/// Two fields differ from <see cref="HotspotShowResponseDto"/> and both differences are traps
/// (verified live 2026-08-11):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Component"/> and <see cref="Project"/> are <b>strings</b> here; on <c>hotspots/show</c>
/// they are objects. There is no way to share a DTO across the two.
/// </description></item>
/// <item><description>
/// <see cref="Assignee"/> is a <b>UUID</b> here (<c>AYgE5F7pEoXHSow6lKjD</c>); on
/// <c>hotspots/show</c> the same field is a <b>login</b> (<c>ada@github</c>), and on an issue it is
/// always a login. The tool layer names this field <c>assigneeId</c> for exactly that reason.
/// </description></item>
/// </list>
/// </remarks>
internal sealed record HotspotDto
{
    /// <summary>The hotspot key.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The component <em>key</em>, as a string.</summary>
    [JsonPropertyName("component")]
    public string? Component { get; init; }

    /// <summary>The project <em>key</em>, as a string.</summary>
    [JsonPropertyName("project")]
    public string? Project { get; init; }

    /// <summary>The OWASP-style category, for example <c>auth</c>.</summary>
    [JsonPropertyName("securityCategory")]
    public string? SecurityCategory { get; init; }

    /// <summary><c>LOW</c>, <c>MEDIUM</c> or <c>HIGH</c> — how likely this is to be a real vulnerability.</summary>
    [JsonPropertyName("vulnerabilityProbability")]
    public string? VulnerabilityProbability { get; init; }

    /// <summary><c>TO_REVIEW</c> or <c>REVIEWED</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary><c>FIXED</c> or <c>SAFE</c>, once reviewed.</summary>
    [JsonPropertyName("resolution")]
    public string? Resolution { get; init; }

    /// <summary>The line the hotspot is on.</summary>
    [JsonPropertyName("line")]
    public int? Line { get; init; }

    /// <summary>What the rule flagged.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>The assignee's <b>UUID</b> on this endpoint. Not a login.</summary>
    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    /// <summary>The SCM author of the line.</summary>
    [JsonPropertyName("author")]
    public string? Author { get; init; }

    /// <summary>When the hotspot was first raised.</summary>
    [JsonPropertyName("creationDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>When it last changed.</summary>
    [JsonPropertyName("updateDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? UpdateDate { get; init; }

    /// <summary>The exact span.</summary>
    [JsonPropertyName("textRange")]
    public TextRangeDto? TextRange { get; init; }

    /// <summary>Secondary locations, in the same shape an issue uses.</summary>
    [JsonPropertyName("flows")]
    public IReadOnlyList<FlowDto>? Flows { get; init; }

    /// <summary>The rule that raised it, as <c>repository:S1234</c>.</summary>
    [JsonPropertyName("ruleKey")]
    public string? RuleKey { get; init; }
}

/// <summary>
/// <c>api/hotspots/show</c> — a separate DTO, not a variant of <see cref="HotspotDto"/>.
/// </summary>
/// <remarks>
/// <para>
/// The response is not the search shape with extra fields: <see cref="Component"/> and
/// <see cref="Project"/> are <em>objects</em> here where the search returns strings, so no amount of
/// shared base class would work. <see cref="Assignee"/> is also a login here rather than the search
/// endpoint's UUID.
/// </para>
/// <para>
/// <b>The comments array is called <c>comment</c>, singular</b> (verified live 2026-08-11) — every
/// other collection in this API is plural, so the obvious spelling silently deserialises to null.
/// </para>
/// </remarks>
internal sealed record HotspotShowResponseDto
{
    /// <summary>The hotspot key.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The component, as a full object.</summary>
    [JsonPropertyName("component")]
    public ComponentDto? Component { get; init; }

    /// <summary>The project, as a full object.</summary>
    [JsonPropertyName("project")]
    public ComponentDto? Project { get; init; }

    /// <summary>The rule, with the three prose fields that make this endpoint worth calling.</summary>
    [JsonPropertyName("rule")]
    public HotspotRuleDto? Rule { get; init; }

    /// <summary><c>TO_REVIEW</c> or <c>REVIEWED</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary><c>FIXED</c> or <c>SAFE</c>, once reviewed.</summary>
    [JsonPropertyName("resolution")]
    public string? Resolution { get; init; }

    /// <summary>The line the hotspot is on.</summary>
    [JsonPropertyName("line")]
    public int? Line { get; init; }

    /// <summary>What the rule flagged.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>The assignee's <b>login</b> on this endpoint. Not the UUID the search returns.</summary>
    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    /// <summary>The SCM author of the line.</summary>
    [JsonPropertyName("author")]
    public string? Author { get; init; }

    /// <summary>When the hotspot was first raised.</summary>
    [JsonPropertyName("creationDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>When it last changed.</summary>
    [JsonPropertyName("updateDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? UpdateDate { get; init; }

    /// <summary>The exact span.</summary>
    [JsonPropertyName("textRange")]
    public TextRangeDto? TextRange { get; init; }

    /// <summary>Every status change, with who made it and when.</summary>
    [JsonPropertyName("changelog")]
    public IReadOnlyList<HotspotChangelogDto>? Changelog { get; init; }

    /// <summary>The comments. Singular on the wire — see the type remarks.</summary>
    [JsonPropertyName("comment")]
    public IReadOnlyList<HotspotCommentDto>? Comment { get; init; }

    /// <summary>Display names for the logins referenced above.</summary>
    [JsonPropertyName("users")]
    public IReadOnlyList<UserRefDto>? Users { get; init; }

    /// <summary>
    /// Whether the current credential may change the status. Reading it before attempting a change
    /// turns a 403 into an answer.
    /// </summary>
    [JsonPropertyName("canChangeStatus")]
    public bool? CanChangeStatus { get; init; }
}

/// <summary>
/// The rule behind a hotspot, with the three prose fields that make <c>hotspots/show</c> worth a
/// call: what the risk is, what the vulnerability would be, and how to fix it. All three are HTML.
/// </summary>
internal sealed record HotspotRuleDto
{
    /// <summary><c>repository:S1234</c>.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The rule's title.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The OWASP-style category.</summary>
    [JsonPropertyName("securityCategory")]
    public string? SecurityCategory { get; init; }

    /// <summary><c>LOW</c>, <c>MEDIUM</c> or <c>HIGH</c>.</summary>
    [JsonPropertyName("vulnerabilityProbability")]
    public string? VulnerabilityProbability { get; init; }

    /// <summary>What is risky about the pattern. HTML.</summary>
    [JsonPropertyName("riskDescription")]
    public string? RiskDescription { get; init; }

    /// <summary>What an attacker could do with it. HTML.</summary>
    [JsonPropertyName("vulnerabilityDescription")]
    public string? VulnerabilityDescription { get; init; }

    /// <summary>How to make it safe. HTML.</summary>
    [JsonPropertyName("fixRecommendations")]
    public string? FixRecommendations { get; init; }
}

/// <summary>One entry in a hotspot's changelog.</summary>
internal sealed record HotspotChangelogDto
{
    /// <summary>The login of whoever made the change.</summary>
    [JsonPropertyName("user")]
    public string? User { get; init; }

    /// <summary>Their display name.</summary>
    [JsonPropertyName("userName")]
    public string? UserName { get; init; }

    /// <summary>Gravatar-style avatar hash.</summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; init; }

    /// <summary>When it happened.</summary>
    [JsonPropertyName("creationDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>The fields that changed.</summary>
    [JsonPropertyName("diffs")]
    public IReadOnlyList<HotspotChangelogDiffDto>? Diffs { get; init; }

    /// <summary>Whether that account is still active.</summary>
    [JsonPropertyName("isUserActive")]
    public bool? IsUserActive { get; init; }
}

/// <summary>One field change within a changelog entry.</summary>
internal sealed record HotspotChangelogDiffDto
{
    /// <summary>The field name, for example <c>status</c>.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>What it became.</summary>
    [JsonPropertyName("newValue")]
    public string? NewValue { get; init; }

    /// <summary>What it was.</summary>
    [JsonPropertyName("oldValue")]
    public string? OldValue { get; init; }
}

/// <summary>One comment on a hotspot.</summary>
/// <remarks>
/// The same shape as <see cref="IssueCommentDto"/> but a separate type, because these arrive under a
/// differently named property on a differently shaped response and sharing one DTO across the two
/// would make a change to either look safe when it is not.
/// </remarks>
internal sealed record HotspotCommentDto
{
    /// <summary>The comment key.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The author's login.</summary>
    [JsonPropertyName("login")]
    public string? Login { get; init; }

    /// <summary>The comment rendered to HTML.</summary>
    [JsonPropertyName("htmlText")]
    public string? HtmlText { get; init; }

    /// <summary>The comment as typed, in SonarQube's markdown dialect.</summary>
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
