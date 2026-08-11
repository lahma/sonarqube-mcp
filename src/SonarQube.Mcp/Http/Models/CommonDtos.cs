using System.Text.Json.Serialization;

namespace SonarQube.Mcp.Http.Models;

/// <summary>
/// The paging numbers a SonarQube response may carry, in every shape it carries them in.
/// </summary>
/// <remarks>
/// <para>
/// Not sealed, and the base of every paginated response DTO, because SonarQube puts the paging
/// fields at the top level of the response object rather than in a wrapper the way the template's
/// API does — there is no <c>PageEnvelope&lt;T&gt;</c> to be had, only a set of sibling properties
/// that eight different responses each carry alongside their own payload.
/// </para>
/// <para>
/// All four are nullable and all four may be absent at once: which of them an endpoint fills in is
/// not something the API documents, and <see cref="PagedResult"/> is the single place that decides
/// what to do about it.
/// </para>
/// </remarks>
internal record PagedEnvelopeDto
{
    /// <summary>Flat total, as <c>rules/search</c> and <c>metrics/search</c> report it.</summary>
    [JsonPropertyName("total")]
    public int? Total { get; init; }

    /// <summary>Flat 1-based page index.</summary>
    [JsonPropertyName("p")]
    public int? P { get; init; }

    /// <summary>Flat page size.</summary>
    [JsonPropertyName("ps")]
    public int? Ps { get; init; }

    /// <summary>The nested form, which most endpoints send and some send <em>as well as</em> the flat trio.</summary>
    [JsonPropertyName("paging")]
    public PagingDto? Paging { get; init; }
}

/// <summary>The nested paging object: <c>{"pageIndex":1,"pageSize":50,"total":1606}</c>.</summary>
internal sealed record PagingDto
{
    /// <summary>1-based page number.</summary>
    [JsonPropertyName("pageIndex")]
    public int? PageIndex { get; init; }

    /// <summary>Items per page.</summary>
    [JsonPropertyName("pageSize")]
    public int? PageSize { get; init; }

    /// <summary>Total items across all pages, capped by SonarQube's 10,000-result ceiling.</summary>
    [JsonPropertyName("total")]
    public int? Total { get; init; }
}

/// <summary>
/// A component: a project, directory, file or test file.
/// </summary>
/// <remarks>
/// <para>
/// Not sealed because <see cref="ComponentMeasuresDto"/> extends it with <c>measures</c> — the
/// measures endpoints return the same component object with one extra property, and modelling that
/// as inheritance is what stops the eleven shared fields from being written twice.
/// </para>
/// <para>
/// It carries <b>both</b> <c>id</c> and <c>uuid</c>. They are the same value under two names: the
/// issues and components endpoints send <c>uuid</c>, the measures endpoints send <c>id</c>
/// (verified live 2026-08-11). Nothing here reads either, but omitting one would make the DTO a
/// misleading record of the wire shape.
/// </para>
/// </remarks>
internal record ComponentDto
{
    /// <summary>The organization key.</summary>
    [JsonPropertyName("organization")]
    public string? Organization { get; init; }

    /// <summary>Internal identifier, as the measures endpoints spell it.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The component key: <c>project</c> or <c>project:path/from/root</c>.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>Internal identifier, as the issues and components endpoints spell it.</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>Whether the component still exists in the latest analysis.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary><c>TRK</c> (project), <c>DIR</c>, <c>FIL</c>, <c>UTS</c> (test file), <c>BRC</c>.</summary>
    [JsonPropertyName("qualifier")]
    public string? Qualifier { get; init; }

    /// <summary>Short display name — the file name, for a file.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Long display name — usually the same as <see cref="Path"/>.</summary>
    [JsonPropertyName("longName")]
    public string? LongName { get; init; }

    /// <summary>
    /// The project-relative path. This is the field that turns a component key into something a
    /// model can act on, and it is why the sidecar <c>components[]</c> arrays are worth joining.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The owning project's key.</summary>
    [JsonPropertyName("project")]
    public string? Project { get; init; }

    /// <summary>The analysed language, for a file.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary>The branch this component was analysed on, when the request named one.</summary>
    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    /// <summary>The pull request this component was analysed on, when the request named one.</summary>
    [JsonPropertyName("pullRequest")]
    public string? PullRequest { get; init; }
}

/// <summary>A 1-based, offset-addressed span of source: <c>{"startLine":216,"endLine":216,…}</c>.</summary>
internal sealed record TextRangeDto
{
    /// <summary>First line, 1-based.</summary>
    [JsonPropertyName("startLine")]
    public int? StartLine { get; init; }

    /// <summary>Last line, 1-based and inclusive.</summary>
    [JsonPropertyName("endLine")]
    public int? EndLine { get; init; }

    /// <summary>Character offset within <see cref="StartLine"/>, 0-based.</summary>
    [JsonPropertyName("startOffset")]
    public int? StartOffset { get; init; }

    /// <summary>Character offset within <see cref="EndLine"/>, 0-based and exclusive.</summary>
    [JsonPropertyName("endOffset")]
    public int? EndOffset { get; init; }
}

/// <summary>
/// One clean-code impact: <c>{"softwareQuality":"MAINTAINABILITY","severity":"LOW"}</c>.
/// </summary>
/// <remarks>
/// This is the modern vocabulary. The legacy <c>type</c>/<c>severity</c> pair still travels
/// alongside it on every issue, saying roughly the same thing in words that no longer match the UI.
/// </remarks>
internal sealed record ImpactDto
{
    /// <summary><c>MAINTAINABILITY</c>, <c>RELIABILITY</c> or <c>SECURITY</c>.</summary>
    [JsonPropertyName("softwareQuality")]
    public string? SoftwareQuality { get; init; }

    /// <summary><c>INFO</c>, <c>LOW</c>, <c>MEDIUM</c>, <c>HIGH</c> or <c>BLOCKER</c>.</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }
}

/// <summary>
/// SonarQube's error body: <c>{"errors":[{"msg":"'ps' value (501) must be less than 500"}]}</c>.
/// </summary>
/// <remarks>
/// <b>A non-2xx response does not always have one.</b> Verified live 2026-08-11: a <c>401</c> from
/// an invalid bearer token arrives with <c>Content-Length: 0</c> and no body at all, while a
/// <c>401</c> from no credential arrives with <c>{"errors":[{"msg":"Authentication is
/// required"}]}</c>. It is an array because a 400 can and does carry more than one message.
/// </remarks>
internal sealed record ErrorEnvelopeDto
{
    /// <summary>The messages, in the order SonarQube listed them.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<ErrorDto>? Errors { get; init; }
}

/// <summary>One error message.</summary>
/// <remarks>
/// SonarQube's 400 text is unusually good — <c>'ps' value (501) must be less than 500</c> names the
/// parameter, the value and the rule — which is why the error funnel quotes it verbatim rather than
/// paraphrasing it.
/// </remarks>
internal sealed record ErrorDto
{
    /// <summary>The message itself.</summary>
    [JsonPropertyName("msg")]
    public string? Msg { get; init; }
}

/// <summary>
/// <c>api/authentication/validate</c>: <c>{"valid":true}</c>.
/// </summary>
/// <remarks>
/// <b>It answers <c>{"valid":true}</c> to an anonymous request as well</b> (verified live
/// 2026-08-11), so it proves a token is not <em>rejected</em>, never that one was sent. Anything
/// that wants to know whether a token exists must ask <c>StaticTokenCredential.HasToken</c>.
/// </remarks>
internal sealed record ValidateResponseDto
{
    /// <summary>Whether the credential — if any was sent — was accepted.</summary>
    [JsonPropertyName("valid")]
    public bool? Valid { get; init; }
}
