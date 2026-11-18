using System.Reflection;
using System.Text.Json;

using NUnit.Framework;

using SonarQube.Mcp.Tools.Models;

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
    [Test]
    public void ProductionSerializerOptionsPutOurContextFirstAndAreReadOnly()
    {
        var options = McpServerSetup.CreateToolSerializerOptions();

        Assert.That(options.IsReadOnly, Is.True);
        Assert.That(options.TypeInfoResolverChain.Count, Is.EqualTo(2));
        Assert.That(options.TypeInfoResolverChain[0], Is.TypeOf<SonarToolJsonContext>());
    }

    /// <summary>
    /// The frozen schema table. The property list is ordered exactly as the parameters are declared,
    /// because that order is what a model reads top to bottom; the required list is what it must
    /// supply. Note that the three non-nullable <c>bool</c> parameters (<c>ascending</c>,
    /// <c>includeDataMetrics</c>, <c>onlyUncovered</c>) appear as properties but are <b>not</b>
    /// required — each has a default, and defaulting is the whole point of them.
    /// </summary>
    [TestCase("listProjects", "query,page,pageSize", "")]
    [TestCase("listComponents", "projectKey,query,scope,branch,pullRequest,page,pageSize", "")]
    [TestCase("listBranches", "projectKey", "")]
    [TestCase("listPullRequests", "projectKey", "")]
    [TestCase("getQualityGateStatus", "projectKey,branch,pullRequest", "")]
    [TestCase("getAnalysisStatus", "projectKey", "")]
    [TestCase(
        "searchIssues",
        "projectKey,component,branch,pullRequest,issueStatuses,impactSeverities,impactSoftwareQualities,rules,tags," +
        "languages,assignees,authors,createdAfter,createdInLast,inNewCodePeriod,fixableByAgent,sortBy,ascending," +
        "page,pageSize",
        "")]
    [TestCase(
        "summarizeIssues",
        "projectKey,component,branch,pullRequest,groupBy,issueStatuses,impactSeverities," +
        "impactSoftwareQualities,rules,tags,languages,assignees,authors,createdAfter,createdInLast," +
        "inNewCodePeriod,fixableByAgent,countBy",
        "")]
    [TestCase("getIssue", "issueKey,projectKey,branch,pullRequest", "issueKey")]
    [TestCase("getIssueChangelog", "issueKey,projectKey", "issueKey")]
    [TestCase("getRule", "ruleKey,sections,context", "ruleKey")]
    [TestCase(
        "searchHotspots",
        "projectKey,component,status,resolution,onlyMine,inNewCodePeriod,branch,pullRequest,page,pageSize",
        "")]
    [TestCase("getHotspot", "hotspotKey", "hotspotKey")]
    [TestCase("getComponentMeasures", "metricKeys,projectKey,component,branch,pullRequest", "metricKeys")]
    [TestCase(
        "listComponentMeasures",
        "metricKeys,projectKey,component,scope,sortByMetric,ascending,query,branch,pullRequest,page,pageSize",
        "metricKeys")]
    [TestCase(
        "getMeasuresHistory",
        "metrics,projectKey,component,from,to,branch,pullRequest,page,pageSize",
        "metrics")]
    [TestCase("listMetrics", "query,domain,includeDataMetrics", "")]
    [TestCase(
        "getFileCoverage",
        "component,projectKey,from,to,onlyUncovered,branch,pullRequest",
        "component")]
    [TestCase("transitionIssue", "issueKey,transition,comment", "issueKey,transition")]
    [TestCase("assignIssue", "issueKey,assignee", "issueKey")]
    [TestCase("addIssueComment", "issueKey,text", "issueKey,text")]
    [TestCase("setHotspotStatus", "hotspotKey,status,resolution,comment", "hotspotKey,status")]
    [TestCase("bulkUpdateIssues", "issueKeys,transition,assignee,comment", "issueKeys")]
    public void InputSchemaExposesExactlyTheModelSuppliedArguments(string name, string properties, string required)
    {
        var schema = ToolTestHost.Find(name).ProtocolTool.InputSchema;

        Assert.That(schema.GetProperty("type").GetString(), Is.EqualTo("object"));

        var actualProperties = schema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.That(actualProperties, Is.EqualTo(Split(properties)));

        var actualRequired = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [];

        Assert.That(actualRequired, Is.EqualTo(Split(required)));
    }

    /// <summary>
    /// The table above must cover every tool. Without this a tool added without a row would have no
    /// frozen schema at all, which is the one thing the table exists to prevent.
    /// </summary>
    [Test]
    public void EveryToolHasARowInTheFrozenSchemaTable()
    {
        // The tool name is the first argument of every [TestCase] row.
        var covered = typeof(ToolSchemaTests)
            .GetMethod(nameof(InputSchemaExposesExactlyTheModelSuppliedArguments))!
            .GetCustomAttributes<TestCaseAttribute>()
            .Select(row => (string)row.Arguments[0]!)
            .ToArray();

        Assert.That(
            covered.Order(StringComparer.Ordinal),
            Is.EqualTo(ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal)));
    }

    [Test]
    public void NoInputSchemaMentionsAnInjectedParameter()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            foreach (var property in tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject())
            {
                Assert.That(NeverInSchema, Has.None.EqualTo(property.Name).Using(StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    [Test]
    public void EveryInputPropertyIsCamelCaseAndDescribed()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var toolName = tool.ProtocolTool.Name;

            foreach (var property in tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject())
            {
                Assert.That(
                    IsCamelCase(property.Name),
                    Is.True,
                    $"{toolName}.{property.Name} is not camelCase.");

                var described = property.Value.TryGetProperty("description", out var description)
                    && !string.IsNullOrWhiteSpace(description.GetString());

                Assert.That(described, Is.True, $"{toolName}.{property.Name} has no schema description.");
            }
        }
    }

    /// <summary>
    /// Output properties carry no description, and that is not an oversight: the exporter reads
    /// <c>[Description]</c>, which the result records deliberately do not have — their meaning is
    /// documented in the tool description, where a model reads it once instead of per property. So
    /// the assertion here is the naming policy alone.
    /// </summary>
    [Test]
    public void EveryToolHasAnObjectOutputSchemaInCamelCase()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var outputSchema = tool.ProtocolTool.OutputSchema;

            Assert.That(outputSchema, Is.Not.Null);
            Assert.That(outputSchema!.Value.GetProperty("type").GetString(), Is.EqualTo("object"));

            AssertCamelCaseProperties(outputSchema.Value, tool.ProtocolTool.Name);
        }
    }

    /// <summary>Every paginated result hands the caller everything needed to ask for the next page.</summary>
    [TestCase("listProjects")]
    [TestCase("listComponents")]
    [TestCase("searchIssues")]
    [TestCase("searchHotspots")]
    [TestCase("listComponentMeasures")]
    [TestCase("getMeasuresHistory")]
    public void PaginatedOutputSchemasCarryTheWholeEnvelope(string name)
    {
        var properties = OutputProperties(name);

        foreach (var property in PagingEnvelope)
        {
            Assert.That(
                properties.TryGetProperty(property, out _),
                Is.True,
                $"{name} is paginated but its result has no '{property}'.");
        }
    }

    /// <summary>
    /// The other direction: a tool whose endpoint has no paging must not advertise any of it. An
    /// invented <c>hasMore: false</c> on <c>listBranches</c> would be a promise about a second page
    /// the API has no concept of. <c>summarizeIssues</c> belongs here for a different reason: it
    /// hits a paginated endpoint but its facets are whole-result aggregates that do not page, so a
    /// paging envelope would invite a second request that returns exactly the same counts.
    /// </summary>
    [TestCase("listBranches")]
    [TestCase("listPullRequests")]
    [TestCase("getQualityGateStatus")]
    [TestCase("getComponentMeasures")]
    [TestCase("getIssue")]
    [TestCase("getRule")]
    [TestCase("getHotspot")]
    [TestCase("getFileCoverage")]
    [TestCase("transitionIssue")]
    [TestCase("assignIssue")]
    [TestCase("addIssueComment")]
    [TestCase("setHotspotStatus")]
    [TestCase("getAnalysisStatus")]
    [TestCase("getIssueChangelog")]
    [TestCase("bulkUpdateIssues")]
    [TestCase("summarizeIssues")]
    public void UnpaginatedOutputSchemasCarryNoneOfTheEnvelope(string name)
    {
        var properties = OutputProperties(name);

        foreach (var property in PagingEnvelope)
        {
            Assert.That(
                properties.TryGetProperty(property, out _),
                Is.False,
                $"{name} is not paginated but its result carries '{property}'.");
        }
    }

    /// <summary>
    /// <c>listMetrics</c> is the one legitimate middle case: it fetches the whole catalogue in one
    /// call and filters client-side, so there is a count to report but no page to ask for.
    /// </summary>
    [Test]
    public void ListMetricsReportsATotalWithoutOfferingAPage()
    {
        var properties = OutputProperties("listMetrics");

        Assert.That(properties.TryGetProperty("totalCount", out _), Is.True);
        Assert.That(properties.TryGetProperty("page", out _), Is.False);
        Assert.That(properties.TryGetProperty("pageSize", out _), Is.False);
        Assert.That(properties.TryGetProperty("hasMore", out _), Is.False);
    }

    private static JsonElement OutputProperties(string name)
    {
        var outputSchema = ToolTestHost.Find(name).ProtocolTool.OutputSchema;

        Assert.That(outputSchema, Is.Not.Null);
        return outputSchema!.Value.GetProperty("properties");
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
                Assert.That(
                    IsCamelCase(property.Name),
                    Is.True,
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
