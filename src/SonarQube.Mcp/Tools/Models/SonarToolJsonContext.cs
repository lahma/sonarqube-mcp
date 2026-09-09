using System.Text.Json.Serialization;

namespace SonarQube.Mcp.Tools.Models;

/// <summary>
/// The source-generated serializer contract for everything the tool layer hands back to an MCP
/// client — result records and the primitive parameter types the SDK builds tool schemas from.
/// </summary>
/// <remarks>
/// <para>
/// This is the context that goes <b>first</b> in the MCP server's <c>TypeInfoResolverChain</c>, with
/// the SDK's own resolver second. First-match-wins, so ours answers for our types and falls through
/// for MCP protocol types — which is what makes JIT and AOT resolve identically instead of one of
/// them silently reaching for reflection.
/// </para>
/// <para>
/// Deliberately separate from <c>SonarWireJsonContext</c>, which carries explicit
/// <c>[JsonPropertyName]</c> on every property and is never chained in here: SonarQube's shapes are
/// wire contracts we do not control, these are ours.
/// </para>
/// <para>
/// The primitive registrations at the bottom are not decorative. With
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> the schema exporter can only describe a
/// parameter whose type some resolver in the chain knows, so every type used as a tool parameter has
/// to be resolvable — including the nullable value types, which no other context in the chain
/// declares.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]

// Project, component, branch, pull request and quality gate results.
[JsonSerializable(typeof(ProjectSummary))]
[JsonSerializable(typeof(ProjectListResult))]
[JsonSerializable(typeof(ComponentSummary))]
[JsonSerializable(typeof(ComponentListResult))]
[JsonSerializable(typeof(BranchSummary))]
[JsonSerializable(typeof(BranchListResult))]
[JsonSerializable(typeof(PullRequestSummary))]
[JsonSerializable(typeof(PullRequestListResult))]
[JsonSerializable(typeof(QualityGateCondition))]
[JsonSerializable(typeof(QualityGateResult))]
[JsonSerializable(typeof(AnalysisTask))]
[JsonSerializable(typeof(AnalysisStatusResult))]

// Issue, rule and hotspot results.
[JsonSerializable(typeof(Impact))]
[JsonSerializable(typeof(IssueSummary))]
[JsonSerializable(typeof(IssueSearchResult))]
[JsonSerializable(typeof(IssueComment))]
[JsonSerializable(typeof(IssueTextRange))]
[JsonSerializable(typeof(IssueFlowLocation))]
[JsonSerializable(typeof(IssueFlow))]
[JsonSerializable(typeof(IssueDetail))]
[JsonSerializable(typeof(IssueFacetBucket))]
[JsonSerializable(typeof(IssueFacet))]
[JsonSerializable(typeof(IssueSummaryResult))]
[JsonSerializable(typeof(IssueChangeDiff))]
[JsonSerializable(typeof(IssueChangeEntry))]
[JsonSerializable(typeof(IssueChangelogResult))]
[JsonSerializable(typeof(RuleSection))]
[JsonSerializable(typeof(RuleDetail))]
[JsonSerializable(typeof(HotspotSummary))]
[JsonSerializable(typeof(HotspotSearchResult))]
[JsonSerializable(typeof(HotspotRule))]
[JsonSerializable(typeof(HotspotComment))]
[JsonSerializable(typeof(HotspotChange))]
[JsonSerializable(typeof(HotspotChangelogEntry))]
[JsonSerializable(typeof(HotspotDetail))]

// Measure, metric and coverage results.
[JsonSerializable(typeof(MeasureValue))]
[JsonSerializable(typeof(MeasuresResult))]
[JsonSerializable(typeof(ComponentMeasures))]
[JsonSerializable(typeof(ComponentMeasuresResult))]
[JsonSerializable(typeof(MeasureHistoryPoint))]
[JsonSerializable(typeof(MetricHistory))]
[JsonSerializable(typeof(MeasuresHistoryResult))]
[JsonSerializable(typeof(MetricSummary))]
[JsonSerializable(typeof(MetricListResult))]
[JsonSerializable(typeof(FileCoverageLine))]
[JsonSerializable(typeof(FileCoverageResult))]

// Write results.
[JsonSerializable(typeof(IssueTransitionResult))]
[JsonSerializable(typeof(IssueAssignResult))]
[JsonSerializable(typeof(IssueCommentResult))]
[JsonSerializable(typeof(HotspotStatusResult))]
[JsonSerializable(typeof(BulkUpdateResult))]

// Tool parameter types, for schema generation.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(bool?))]
internal sealed partial class SonarToolJsonContext : JsonSerializerContext;
