using System.Text.Json;

using SonarQube.Mcp.Tools.Models;

using Xunit;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// The JSON schemas an MCP client receives from <c>tools/list</c>, generated with the server's own
/// serializer options.
/// </summary>
/// <remarks>
/// <para>
/// A schema is the only description of a tool the model ever sees, so the failures worth catching
/// here are the silent ones: an injected collaborator leaking in as a required argument the model
/// then has to invent, a parameter losing its description, or the naming policy slipping so that
/// the schema says <c>PageSize</c> while the tool binds <c>pageSize</c>.
/// </para>
/// <para>
/// The paging assertions run in both directions on purpose (AGENTS.md, pagination decision): a paginated tool cannot
/// lose its envelope, and an unpaginated one cannot grow a fake one. <c>listBranches</c> and
/// <c>listPullRequests</c> address endpoints with no paging at all, and inventing a
/// <c>hasMore: false</c> there would be a claim SonarQube never made.
/// </para>
/// </remarks>
public class ToolSchemaTests
{
    /// <summary>Parameter names the SDK must never expose: they are bound from DI or the protocol.</summary>
    private static readonly string[] NeverInSchema = ["client", "options", "cancellationToken"];

    /// <summary>The four properties every paginated result carries, and no other result may.</summary>
    private static readonly string[] PagingEnvelope = ["page", "pageSize", "totalCount", "hasMore"];

    /// <summary>
    /// The schemas above are only the shipped ones if the options that produced them are the
    /// shipped ones — our context first, the SDK's resolver second, and the whole thing frozen.
    /// </summary>
    [Fact]
    public void ProductionSerializerOptionsPutOurContextFirstAndAreReadOnly()
    {
        var options = McpServerSetup.CreateToolSerializerOptions();

        Assert.True(options.IsReadOnly);
        Assert.Equal(2, options.TypeInfoResolverChain.Count);
        Assert.IsType<SonarToolJsonContext>(options.TypeInfoResolverChain[0]);
    }

    /// <summary>
    /// The frozen schema table. The property list is ordered exactly as the parameters are declared,
    /// because that order is what a model reads top to bottom; the required list is what it must
    /// supply. Note that the three non-nullable <c>bool</c> parameters (<c>ascending</c>,
    /// <c>includeDataMetrics</c>, <c>onlyUncovered</c>) appear as properties but are <b>not</b>
    /// required — each has a default, and defaulting is the whole point of them.
    /// </summary>
    [Theory]
    [InlineData("listProjects", "query,page,pageSize", "")]
    [InlineData("listComponents", "projectKey,query,scope,branch,pullRequest,page,pageSize", "")]
    [InlineData("listBranches", "projectKey", "")]
    [InlineData("listPullRequests", "projectKey", "")]
    [InlineData("getQualityGateStatus", "projectKey,branch,pullRequest", "")]
    [InlineData(
        "searchIssues",
        "projectKey,component,branch,pullRequest,issueStatuses,impactSeverities,impactSoftwareQualities,rules,tags," +
        "languages,assignees,createdAfter,createdInLast,inNewCodePeriod,sortBy,ascending,page,pageSize",
        "")]
    [InlineData("getIssue", "issueKey,branch,pullRequest", "issueKey")]
    [InlineData("getRule", "ruleKey,sections", "ruleKey")]
    [InlineData(
        "searchHotspots",
        "projectKey,component,status,resolution,onlyMine,inNewCodePeriod,branch,pullRequest,page,pageSize",
        "")]
    [InlineData("getHotspot", "hotspotKey", "hotspotKey")]
    [InlineData("getComponentMeasures", "metricKeys,projectKey,component,branch,pullRequest", "metricKeys")]
    [InlineData(
        "listComponentMeasures",
        "metricKeys,projectKey,component,scope,sortByMetric,ascending,query,branch,pullRequest,page,pageSize",
        "metricKeys")]
    [InlineData(
        "getMeasuresHistory",
        "metrics,projectKey,component,from,to,branch,pullRequest,page,pageSize",
        "metrics")]
    [InlineData("listMetrics", "query,domain,includeDataMetrics", "")]
    [InlineData(
        "getFileCoverage",
        "component,projectKey,from,to,onlyUncovered,branch,pullRequest",
        "component")]
    [InlineData("transitionIssue", "issueKey,transition,comment", "issueKey,transition")]
    [InlineData("assignIssue", "issueKey,assignee", "issueKey")]
    [InlineData("addIssueComment", "issueKey,text", "issueKey,text")]
    [InlineData("setHotspotStatus", "hotspotKey,status,resolution,comment", "hotspotKey,status")]
    public void InputSchemaExposesExactlyTheModelSuppliedArguments(string name, string properties, string required)
    {
        var schema = ToolTestHost.Find(name).ProtocolTool.InputSchema;

        Assert.Equal("object", schema.GetProperty("type").GetString());

        var actualProperties = schema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(Split(properties), actualProperties);

        var actualRequired = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [];

        Assert.Equal(Split(required), actualRequired);
    }

    [Fact]
    public void NoInputSchemaMentionsAnInjectedParameter()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            foreach (var property in tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, NeverInSchema, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void EveryInputPropertyIsCamelCaseAndDescribed()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var toolName = tool.ProtocolTool.Name;

            foreach (var property in tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject())
            {
                Assert.True(
                    IsCamelCase(property.Name),
                    $"{toolName}.{property.Name} is not camelCase.");

                var described = property.Value.TryGetProperty("description", out var description)
                    && !string.IsNullOrWhiteSpace(description.GetString());

                Assert.True(described, $"{toolName}.{property.Name} has no schema description.");
            }
        }
    }

    /// <summary>
    /// Output properties carry no description, and that is not an oversight: the exporter reads
    /// <c>[Description]</c>, which the result records deliberately do not have — their meaning is
    /// documented in the tool description, where a model reads it once instead of per property. So
    /// the assertion here is the naming policy alone.
    /// </summary>
    [Fact]
    public void EveryToolHasAnObjectOutputSchemaInCamelCase()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var outputSchema = tool.ProtocolTool.OutputSchema;

            Assert.NotNull(outputSchema);
            Assert.Equal("object", outputSchema.Value.GetProperty("type").GetString());

            AssertCamelCaseProperties(outputSchema.Value, tool.ProtocolTool.Name);
        }
    }

    /// <summary>Every paginated result hands the caller everything needed to ask for the next page.</summary>
    [Theory]
    [InlineData("listProjects")]
    [InlineData("listComponents")]
    [InlineData("searchIssues")]
    [InlineData("searchHotspots")]
    [InlineData("listComponentMeasures")]
    [InlineData("getMeasuresHistory")]
    public void PaginatedOutputSchemasCarryTheWholeEnvelope(string name)
    {
        var properties = OutputProperties(name);

        foreach (var property in PagingEnvelope)
        {
            Assert.True(
                properties.TryGetProperty(property, out _),
                $"{name} is paginated but its result has no '{property}'.");
        }
    }

    /// <summary>
    /// The other direction: a tool whose endpoint has no paging must not advertise any of it. An
    /// invented <c>hasMore: false</c> on <c>listBranches</c> would be a promise about a second page
    /// the API has no concept of.
    /// </summary>
    [Theory]
    [InlineData("listBranches")]
    [InlineData("listPullRequests")]
    [InlineData("getQualityGateStatus")]
    [InlineData("getComponentMeasures")]
    [InlineData("getIssue")]
    [InlineData("getRule")]
    [InlineData("getHotspot")]
    [InlineData("getFileCoverage")]
    [InlineData("transitionIssue")]
    [InlineData("assignIssue")]
    [InlineData("addIssueComment")]
    [InlineData("setHotspotStatus")]
    public void UnpaginatedOutputSchemasCarryNoneOfTheEnvelope(string name)
    {
        var properties = OutputProperties(name);

        foreach (var property in PagingEnvelope)
        {
            Assert.False(
                properties.TryGetProperty(property, out _),
                $"{name} is not paginated but its result carries '{property}'.");
        }
    }

    /// <summary>
    /// <c>listMetrics</c> is the one legitimate middle case: it fetches the whole catalogue in one
    /// call and filters client-side, so there is a count to report but no page to ask for.
    /// </summary>
    [Fact]
    public void ListMetricsReportsATotalWithoutOfferingAPage()
    {
        var properties = OutputProperties("listMetrics");

        Assert.True(properties.TryGetProperty("totalCount", out _));
        Assert.False(properties.TryGetProperty("page", out _));
        Assert.False(properties.TryGetProperty("pageSize", out _));
        Assert.False(properties.TryGetProperty("hasMore", out _));
    }

    private static JsonElement OutputProperties(string name)
    {
        var outputSchema = ToolTestHost.Find(name).ProtocolTool.OutputSchema;

        Assert.NotNull(outputSchema);
        return outputSchema.Value.GetProperty("properties");
    }

    private static string[] Split(string value) =>
        value.Length == 0 ? [] : value.Split(',');

    private static void AssertCamelCaseProperties(JsonElement schema, string toolName)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                Assert.True(
                    IsCamelCase(property.Name),
                    $"{toolName} output property '{property.Name}' is not camelCase.");

                AssertCamelCaseProperties(property.Value, toolName);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            AssertCamelCaseProperties(items, toolName);
        }
    }

    /// <summary>
    /// Hand-rolled rather than a regex: the rule is small, and the failure message matters more
    /// than the pattern.
    /// </summary>
    private static bool IsCamelCase(string name)
    {
        if (name.Length == 0 || !char.IsLower(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
