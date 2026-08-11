using System.Text.Json.Serialization;

namespace SonarQube.Mcp.Http.Models;

/// <summary>
/// The source-generated serializer contract for everything that crosses the wire to SonarQube
/// Cloud.
/// </summary>
/// <remarks>
/// <para>
/// <b>This context is used by the HTTP client only, and is never chained into the MCP server's
/// <c>JsonSerializerOptions</c>.</b> SonarQube's shapes are wire contracts we do not control; the
/// tool-facing shapes are camelCase and ours. Mixing the two resolvers would let a wire type leak
/// into a tool schema and vice versa.
/// </para>
/// <para>
/// No naming policy is configured, deliberately. Every property carries an explicit
/// <see cref="JsonPropertyNameAttribute"/>, so a rename or a refactor cannot silently change what
/// is read off the wire, and reading the DTO tells you the exact JSON. It matters more here than in
/// most codebases: SonarQube's own naming is not internally consistent — <c>metricKeys</c> on one
/// endpoint and <c>metrics</c> on another, a <c>comment</c> array that is plural everywhere else —
/// so no policy could produce all of them anyway.
/// </para>
/// <para>
/// <c>DefaultIgnoreCondition = WhenWritingNull</c> is set for symmetry with the tool context and
/// because it costs nothing; nothing here is ever serialised on a production path, since every
/// write is form-encoded through <see cref="FormBody"/> and there are no request-body DTOs at all.
/// </para>
/// <para>
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> is set in both csproj files (D7), so a
/// type missing from this list fails at the first <c>dotnet test</c> rather than only after an AOT
/// publish. There are no closed generics to register: SonarQube's paged responses are concrete
/// types with the paging fields inlined, not a <c>Page&lt;T&gt;</c> wrapper, so
/// <c>TypeInfoPropertyName</c> is never needed.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]

// Issues.
[JsonSerializable(typeof(IssuesSearchResponseDto))]
[JsonSerializable(typeof(IssueOperationResponseDto))]

// Security hotspots. Two response types, because hotspots/show is not hotspots/search with extras.
[JsonSerializable(typeof(HotspotsSearchResponseDto))]
[JsonSerializable(typeof(HotspotShowResponseDto))]

// Measures and metrics.
[JsonSerializable(typeof(MeasuresComponentResponseDto))]
[JsonSerializable(typeof(MeasuresComponentTreeResponseDto))]
[JsonSerializable(typeof(MeasuresHistoryResponseDto))]
[JsonSerializable(typeof(MetricsSearchResponseDto))]

// Projects, components, quality gates, branches and pull requests.
[JsonSerializable(typeof(ComponentsSearchResponseDto))]
[JsonSerializable(typeof(ComponentsTreeResponseDto))]
[JsonSerializable(typeof(ProjectStatusResponseDto))]
[JsonSerializable(typeof(BranchesListResponseDto))]
[JsonSerializable(typeof(PullRequestsListResponseDto))]

// Rules and sources.
[JsonSerializable(typeof(RulesSearchResponseDto))]
[JsonSerializable(typeof(SourcesLinesResponseDto))]

// Cross-cutting.
[JsonSerializable(typeof(ErrorEnvelopeDto))]
[JsonSerializable(typeof(ValidateResponseDto))]
internal sealed partial class SonarWireJsonContext : JsonSerializerContext;
