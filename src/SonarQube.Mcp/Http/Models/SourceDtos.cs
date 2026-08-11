using System.Text.Json.Serialization;

using SonarQube.Mcp.Json;

namespace SonarQube.Mcp.Http.Models;
/// <summary><c>api/sources/lines</c>.</summary>
/// <remarks>
/// This action is <b>not listed by <c>api/webservices/list</c> at all</b>, with or without
/// <c>include_internals</c> (checked 2026-08-11), yet it answers <c>200</c> anonymously on a public
/// project. Any future parameter-drift test has to treat it as verified-but-undocumented rather
/// than expecting to find it in the catalogue.
/// </remarks>
internal sealed record SourcesLinesResponseDto
{
    /// <summary>One entry per line in the requested range.</summary>
    [JsonPropertyName("sources")]
    public IReadOnlyList<SourceLineDto>? Sources { get; init; }
}

/// <summary>
/// One source line, with whatever coverage, duplication and new-code facts SonarQube has for it.
/// </summary>
/// <remarks>
/// <see cref="Code"/> is <b>syntax-highlighted HTML</b>, not source text (verified live: it comes
/// back full of <c>&lt;span class="k"&gt;</c>). The tool layer drops it — the agent already has the
/// file — and keeps the numbers, which are the part a checkout cannot supply.
/// <para>
/// The coverage fields are absent, not zero, on a project with no coverage data. Absent means "not
/// measured"; zero would mean "measured and not covered", and they call for opposite actions.
/// </para>
/// </remarks>
internal sealed record SourceLineDto
{
    /// <summary>The line number, 1-based.</summary>
    [JsonPropertyName("line")]
    public int? Line { get; init; }

    /// <summary>The line's source as syntax-highlighted HTML.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>The commit that last touched the line.</summary>
    [JsonPropertyName("scmRevision")]
    public string? ScmRevision { get; init; }

    /// <summary>Who last touched it.</summary>
    [JsonPropertyName("scmAuthor")]
    public string? ScmAuthor { get; init; }

    /// <summary>When they did. SonarQube's basic-format offset applies here too.</summary>
    [JsonPropertyName("scmDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? ScmDate { get; init; }

    /// <summary>Whether the line is part of a duplicated block.</summary>
    [JsonPropertyName("duplicated")]
    public bool? Duplicated { get; init; }

    /// <summary>Whether the line falls in the new-code period.</summary>
    [JsonPropertyName("isNew")]
    public bool? IsNew { get; init; }

    /// <summary>How many times tests executed the line. <c>0</c> means uncovered.</summary>
    [JsonPropertyName("lineHits")]
    public int? LineHits { get; init; }

    /// <summary>How many branches the line has.</summary>
    [JsonPropertyName("conditions")]
    public int? Conditions { get; init; }

    /// <summary>How many of them tests exercised. Fewer than <see cref="Conditions"/> is partial coverage.</summary>
    [JsonPropertyName("coveredConditions")]
    public int? CoveredConditions { get; init; }
}
