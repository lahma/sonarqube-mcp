using System.Text.Json.Serialization;

namespace SonarQube.Mcp.Http.Models;

/// <summary>
/// <c>api/rules/search</c> — flat paging (<c>total</c>/<c>p</c>/<c>ps</c>), no <c>paging</c> object.
/// </summary>
/// <remarks>
/// This endpoint, not <c>api/rules/show</c>, is how a rule's description is fetched. On SonarQube
/// Cloud <c>rules/show</c> accepts only <c>actives</c>, <c>key</c> and <c>organization</c> — it has
/// no <c>f</c> parameter, so there is no way to ask it for <c>descriptionSections</c>. Confirmed
/// against the API's own <c>webservices/list</c> metadata.
/// </remarks>
internal sealed record RulesSearchResponseDto : PagedEnvelopeDto
{
    /// <summary>The matching rules.</summary>
    [JsonPropertyName("rules")]
    public IReadOnlyList<RuleDto>? Rules { get; init; }
}

/// <summary>
/// One rule.
/// </summary>
/// <remarks>
/// <b><see cref="DescriptionSections"/> can legitimately be absent.</b> Verified live 2026-08-11:
/// an anonymous <c>rules/search?f=descriptionSections</c> returns the rule with its name, impacts
/// and clean-code attribute but <em>no</em> sections, and with a <see cref="RequiredEntitlements"/>
/// field present. That looks like an entitlement restriction rather than a parameter error, so the
/// tool layer degrades — sections omitted, a note explaining why — instead of treating it as a
/// failure. Whether an authenticated token gets the sections is unproven and is Phase F's job.
/// </remarks>
internal sealed record RuleDto
{
    /// <summary><c>repository:S1234</c>.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The rule repository, for example <c>csharpsquid</c>.</summary>
    [JsonPropertyName("repo")]
    public string? Repo { get; init; }

    /// <summary>The rule's title.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Language key, for example <c>cs</c>.</summary>
    [JsonPropertyName("lang")]
    public string? Lang { get; init; }

    /// <summary>Language display name, for example <c>C#</c>.</summary>
    [JsonPropertyName("langName")]
    public string? LangName { get; init; }

    /// <summary>Legacy type: <c>CODE_SMELL</c>, <c>BUG</c>, <c>VULNERABILITY</c>, <c>SECURITY_HOTSPOT</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Legacy severity.</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    /// <summary><c>READY</c>, <c>BETA</c>, <c>DEPRECATED</c> or <c>REMOVED</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Clean-code attribute, for example <c>LOGICAL</c>.</summary>
    [JsonPropertyName("cleanCodeAttribute")]
    public string? CleanCodeAttribute { get; init; }

    /// <summary>Its category, for example <c>INTENTIONAL</c>.</summary>
    [JsonPropertyName("cleanCodeAttributeCategory")]
    public string? CleanCodeAttributeCategory { get; init; }

    /// <summary>The modern severity vocabulary.</summary>
    [JsonPropertyName("impacts")]
    public IReadOnlyList<ImpactDto>? Impacts { get; init; }

    /// <summary>The rule's own tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Tags SonarQube attaches itself and users cannot remove.</summary>
    [JsonPropertyName("sysTags")]
    public IReadOnlyList<string>? SysTags { get; init; }

    /// <summary>External standards this rule maps to, for example <c>cwe:476</c>.</summary>
    [JsonPropertyName("securityStandards")]
    public IReadOnlyList<string>? SecurityStandards { get; init; }

    /// <summary>Which secure-coding principles the rule teaches.</summary>
    [JsonPropertyName("educationPrinciples")]
    public IReadOnlyList<string>? EducationPrinciples { get; init; }

    /// <summary>
    /// Entitlements the caller needs to see the full rule. Present — and empty — on an anonymous
    /// request that also came back without description sections.
    /// </summary>
    [JsonPropertyName("requiredEntitlements")]
    public IReadOnlyList<string>? RequiredEntitlements { get; init; }

    /// <summary>The rule's prose, split into sections. HTML. May be absent entirely.</summary>
    [JsonPropertyName("descriptionSections")]
    public IReadOnlyList<RuleDescriptionSectionDto>? DescriptionSections { get; init; }

    /// <summary>The whole description as one HTML blob, for rules that predate sections.</summary>
    [JsonPropertyName("htmlDesc")]
    public string? HtmlDesc { get; init; }

    /// <summary>The rule's configurable parameters.</summary>
    [JsonPropertyName("params")]
    public IReadOnlyList<RuleParamDto>? Params { get; init; }
}

/// <summary>One section of a rule's description.</summary>
internal sealed record RuleDescriptionSectionDto
{
    /// <summary>
    /// <c>introduction</c>, <c>root_cause</c>, <c>assess_the_problem</c>, <c>how_to_fix</c> or
    /// <c>resources</c>.
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The section's HTML.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>
    /// Which framework the section applies to, when a rule documents several. A rule with contexts
    /// sends the same section key several times, once per context.
    /// </summary>
    [JsonPropertyName("context")]
    public RuleContextDto? Context { get; init; }
}

/// <summary>The framework a description section applies to.</summary>
internal sealed record RuleContextDto
{
    /// <summary>The context key, for example <c>asp_net_core</c>.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>Its display name.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }
}

/// <summary>One configurable parameter of a rule.</summary>
internal sealed record RuleParamDto
{
    /// <summary>The parameter name.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>What it does. HTML.</summary>
    [JsonPropertyName("htmlDesc")]
    public string? HtmlDesc { get; init; }

    /// <summary>Its default, as text.</summary>
    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; init; }

    /// <summary>Its type, in SonarQube's own parameter-type vocabulary.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }
}
