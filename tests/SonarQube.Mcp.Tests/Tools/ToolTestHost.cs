using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Server;

using SonarQube.Mcp.Authentication;
using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;
using SonarQube.Mcp.Tests.Http;
using SonarQube.Mcp.Tools;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// Shared scaffolding for the tool-layer tests: the four tool classes, the nineteen
/// <see cref="McpServerTool"/> instances built exactly the way the server builds them, and the
/// collaborators the tool methods take as plain parameters.
/// </summary>
/// <remarks>
/// <para>
/// The tools are constructed through <see cref="McpServerTool.Create(MethodInfo, object?, McpServerToolCreateOptions)"/>
/// with <c>Services</c> and <c>SerializerOptions</c> set the same way <c>WithTools&lt;T&gt;(jsonOptions)</c>
/// sets them, so the schemas and annotations these tests inspect are the ones an MCP client receives
/// from <c>tools/list</c>. The serializer options come from
/// <see cref="McpServerSetup.CreateToolSerializerOptions"/> — the production factory itself, not a
/// copy that could drift.
/// </para>
/// <para>
/// The service provider only has to contain the types the tool methods expect to be injected: the
/// SDK asks <c>IServiceProviderIsService</c> which parameters to leave out of the generated schema,
/// so a missing registration shows up as an extra schema property rather than as a binding failure.
/// </para>
/// </remarks>
internal static class ToolTestHost
{
    /// <summary>The organization every test is configured with.</summary>
    internal const string Organization = "quartznet";

    /// <summary>The project key the golden fixtures were captured from.</summary>
    internal const string Project = "quartznet_quartznet";

    /// <summary>A file component key inside <see cref="Project"/>.</summary>
    internal const string FileComponent = "quartznet_quartznet:src/Quartz/Core/QuartzScheduler.cs";

    /// <summary>
    /// Serialises tool construction across the whole test assembly.
    /// </summary>
    /// <remarks>
    /// The SDK caches the reflected function descriptor for a method process-wide, so two test
    /// classes building tools for the same methods at the same time — which xunit's parallelism
    /// makes possible and the server itself never does — race on that cache. One lock and one shared
    /// options instance remove the only concurrency in play.
    /// </remarks>
    private static readonly Lock BuildGate = new();

    /// <summary>The production tool-facing serializer options, created once for the whole assembly.</summary>
    internal static JsonSerializerOptions SerializerOptions { get; } = McpServerSetup.CreateToolSerializerOptions();

    /// <summary>The four classes registered with <c>WithTools&lt;T&gt;</c>.</summary>
    internal static IReadOnlyList<Type> ToolTypes { get; } =
        [typeof(ProjectReadTools), typeof(IssueReadTools), typeof(MeasureReadTools), typeof(IssueWriteTools)];

    /// <summary>Every <c>[McpServerTool]</c> method, discovered the way the SDK discovers them.</summary>
    internal static IReadOnlyList<MethodInfo> ToolMethods { get; } = DiscoverToolMethods(ToolTypes);

    /// <summary>The built tools, ordered by MCP name so a failure names a stable tool.</summary>
    internal static IReadOnlyList<McpServerTool> Tools { get; } = BuildTools(ToolTypes);

    /// <summary>Finds a built tool by its MCP name.</summary>
    internal static McpServerTool Find(string name) =>
        Tools.Single(tool => string.Equals(tool.ProtocolTool.Name, name, StringComparison.Ordinal));

    /// <summary>Finds a tool method by the MCP name its attribute declares.</summary>
    internal static MethodInfo FindMethod(string name) =>
        ToolMethods.Single(method =>
            string.Equals(method.GetCustomAttribute<McpServerToolAttribute>()!.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Options as a fully configured server would resolve them, with every value a test might want
    /// to vary exposed as a parameter. Built through
    /// <see cref="SonarQubeMcpOptions.FromEnvironment(Func{string, string?})"/> so the clamps and
    /// fallbacks the production reader applies are applied here too.
    /// </summary>
    internal static SonarQubeMcpOptions CreateOptions(
        string? organization = Organization,
        string? defaultProject = Project,
        string? token = "test-token",
        int? maxPageSize = null,
        int? defaultPageSize = null,
        int? maxSourceLines = null,
        bool readOnly = false) =>
        SonarQubeMcpOptions.FromEnvironment(name => name switch
        {
            "SONARQUBE_TOKEN" => token,
            "SONARQUBE_ORG" => organization,
            "SONARQUBE_MCP_DEFAULT_PROJECT" => defaultProject,
            "SONARQUBE_MCP_READ_ONLY" => readOnly ? "1" : null,
            "SONARQUBE_MCP_MAX_PAGE_SIZE" => Text(maxPageSize),
            "SONARQUBE_MCP_DEFAULT_PAGE_SIZE" => Text(defaultPageSize),
            "SONARQUBE_MCP_MAX_SOURCE_LINES" => Text(maxSourceLines),
            _ => null,
        });

    /// <summary>A client over a stub transport, with a token configured.</summary>
    /// <param name="transport">The innermost handler.</param>
    /// <param name="timeProvider">The retry pipeline's clock, when a test drives a retried status.</param>
    internal static SonarApiClient CreateClient(HttpMessageHandler transport, TimeProvider? timeProvider = null) =>
        TestClient.Create(transport, timeProvider);

    /// <summary>A client with no token, which is how every golden fixture was captured.</summary>
    internal static SonarApiClient CreateAnonymousClient(HttpMessageHandler transport) =>
        TestClient.CreateAnonymous(transport);

    /// <summary>Discovers the tool methods of an arbitrary class set, the way the SDK would.</summary>
    internal static List<MethodInfo> DiscoverToolMethods(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        var methods = new List<MethodInfo>();

        foreach (var type in types)
        {
            // The same binding flags McpServerBuilderExtensions.WithTools<T> uses, so this test's
            // idea of "the tools" cannot be narrower than the server's.
            foreach (var method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                {
                    methods.Add(method);
                }
            }
        }

        // Reflection order is not contractual; sort so a failure message always names the same tool.
        methods.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return methods;
    }

    /// <summary>
    /// Builds the tools an arbitrary class set would advertise. Taking the set as an argument is
    /// what lets <c>ReadOnlyModeTests</c> ask what the read-only registration actually publishes,
    /// rather than asserting against a list of types.
    /// </summary>
    internal static List<McpServerTool> BuildTools(IEnumerable<Type> types)
    {
        var services = new ServiceCollection();
        var options = CreateOptions();

        services.AddSingleton(options);
        services.AddSingleton(_ => new SonarApiClient(
            options,
            new StaticTokenCredential(options),
            NullLoggerFactory.Instance,
            new StubHttpMessageHandler(),
            TestClient.BaseAddress));

        var provider = services.BuildServiceProvider();
        var tools = new List<McpServerTool>();

        lock (BuildGate)
        {
            foreach (var method in DiscoverToolMethods(types))
            {
                tools.Add(McpServerTool.Create(
                    method,
                    target: null,
                    new McpServerToolCreateOptions
                    {
                        Services = provider,
                        SerializerOptions = SerializerOptions,
                    }));
            }
        }

        tools.Sort(static (left, right) => string.CompareOrdinal(left.ProtocolTool.Name, right.ProtocolTool.Name));
        return tools;
    }

    private static string? Text(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The hand-written payloads the tool tests need and the golden fixtures cannot supply — a rule
/// that predates description sections, a hotspot carrying a comment, a metric catalogue small
/// enough to assert whole.
/// </summary>
/// <remarks>
/// Everything that <em>can</em> come from <c>Fixtures/</c> does: these are the shapes SonarQube
/// Cloud only produces for an authenticated account or for a rule old enough that no live capture
/// of it exists in this repository.
/// </remarks>
internal static class ToolPayloads
{
    /// <summary>A rule with no <c>descriptionSections</c> and a legacy <c>htmlDesc</c> blob.</summary>
    internal const string RuleWithHtmlDescOnly = """
        {
          "total": 1,
          "p": 1,
          "ps": 1,
          "rules": [
            {
              "key": "csharpsquid:S1118",
              "repo": "csharpsquid",
              "name": "Utility classes should not have public constructors",
              "langName": "C#",
              "htmlDesc": "<p>A utility class has only <code>static</code> members.</p><ul><li>Add a private constructor.</li></ul>"
            }
          ]
        }
        """;

    /// <summary>An empty <c>rules/search</c> answer — a key that does not exist.</summary>
    internal const string NoRules = """{"total":0,"p":1,"ps":1,"rules":[]}""";

    /// <summary>
    /// C8's trap, written by hand because SonarQube cannot produce it: the wire property is
    /// <c>comment</c>, singular, and the plural spelling beside it is the one a reader reaches for
    /// first. A capture proves the singular property is read
    /// (<c>Fixtures/hotspots-show-with-comment.json</c>); only a payload carrying <em>both</em> can
    /// prove the plural is not.
    /// </summary>
    internal const string HotspotShowWithBothCommentSpellings = """
        {
          "key": "AZvu8ZyfNsnCVHe5poFs",
          "project": { "key": "quartznet_quartznet", "qualifier": "TRK", "name": "quartznet" },
          "status": "REVIEWED",
          "resolution": "SAFE",
          "comments": [ { "key": "ignored", "markdown": "the plural spelling is not what SonarQube sends" } ],
          "comment": [
            {
              "key": "AaAoPgsRCRl6zX9b36TK",
              "login": "lahma@github",
              "htmlText": "the singular property",
              "markdown": "the singular property",
              "createdAt": "2026-08-22T06:52:29+0000"
            }
          ]
        }
        """;

    /// <summary>
    /// Two issues whose components carry the same key twice, one of them without a path — the
    /// sidecar shape that a naive dictionary build would throw on.
    /// </summary>
    internal const string IssuesWithDuplicateComponents = """
        {
          "total": 2,
          "p": 1,
          "ps": 2,
          "paging": { "pageIndex": 1, "pageSize": 2, "total": 2 },
          "issues": [
            {
              "key": "ISSUE-1",
              "rule": "csharpsquid:S1192",
              "component": "quartznet_quartznet:src/Quartz/Widget.cs",
              "project": "quartznet_quartznet",
              "line": 10,
              "message": "First",
              "issueStatus": "OPEN"
            },
            {
              "key": "ISSUE-2",
              "rule": "csharpsquid:S1192",
              "component": "quartznet_quartznet:src/Quartz/Other.cs",
              "project": "quartznet_quartznet",
              "line": 20,
              "message": "Second",
              "issueStatus": "OPEN"
            }
          ],
          "components": [
            {
              "key": "quartznet_quartznet:src/Quartz/Widget.cs",
              "qualifier": "FIL",
              "name": "Widget.cs",
              "path": "src/Quartz/Widget.cs"
            },
            {
              "key": "quartznet_quartznet:src/Quartz/Widget.cs",
              "qualifier": "FIL",
              "name": "Widget.cs",
              "path": "src/Quartz/Widget.cs"
            }
          ],
          "rules": [ { "key": "csharpsquid:S1192", "name": "String literals should not be duplicated" } ]
        }
        """;

    /// <summary>A transition response that carries no <c>transitions</c> array, forcing the re-read.</summary>
    internal const string TransitionWithoutTransitions = """
        {
          "issue": {
            "key": "AZ_xePOumT_q4T_1FWf8",
            "rule": "csharpsquid:S8970",
            "component": "quartznet_quartznet:src/Quartz/Core/QuartzScheduler.cs",
            "project": "quartznet_quartznet",
            "line": 943,
            "status": "RESOLVED",
            "resolution": "WONTFIX",
            "issueStatus": "ACCEPTED",
            "message": "Remove this null-forgiving operator; nullable warnings are disabled here."
          }
        }
        """;

    /// <summary>The re-read that answers with the transitions the operation response omitted.</summary>
    internal const string TransitionReread = """
        {
          "total": 1,
          "p": 1,
          "ps": 1,
          "issues": [ { "key": "AZ_xePOumT_q4T_1FWf8", "transitions": ["reopen"] } ]
        }
        """;

    /// <summary>Six source lines: uncovered, partially covered, covered, and two with no data at all.</summary>
    internal const string SourceLinesWithCoverage = """
        {
          "sources": [
            { "line": 1, "code": "<span>using System;</span>", "lineHits": 1, "isNew": true },
            { "line": 2, "code": "<span>if (x)</span>", "lineHits": 3, "conditions": 2, "coveredConditions": 1 },
            { "line": 3, "code": "<span>Throw();</span>", "lineHits": 0, "duplicated": true },
            { "line": 4, "code": "<span>}</span>", "lineHits": 2, "conditions": 2, "coveredConditions": 2 },
            { "line": 5, "code": "<span></span>" }
          ]
        }
        """;

    /// <summary>A file SonarQube analysed but never measured coverage for.</summary>
    internal const string SourceLinesWithoutCoverage = """
        {
          "sources": [
            { "line": 1, "code": "<span>using System;</span>" },
            { "line": 2, "code": "<span>// nothing measured</span>" }
          ]
        }
        """;

    /// <summary>
    /// A component tree whose ranking filter matched nothing.
    /// </summary>
    /// <remarks>
    /// Hand-written because the interesting half of it is what the API leaves out: ranking
    /// <c>quartznet_quartznet</c> by <c>coverage</c> — a project that publishes none — really does
    /// answer <c>total: 0</c> with an empty <c>components</c>, and the response says nothing at all
    /// about why. <c>baseComponent.measures</c> comes back <c>[]</c> here exactly as it does on the
    /// ranking that succeeded (<c>Fixtures/measures-component-tree-coverage-sorted.json</c>), so
    /// there is no field to tell the two apart and the reasoning has to be done by the mapper.
    /// </remarks>
    internal const string ComponentTreeRankedToNothing = """
        {
          "paging": { "pageIndex": 1, "pageSize": 50, "total": 0 },
          "baseComponent": {
            "id": "AYloNkLywDG_abJFLUp5",
            "key": "quartznet_quartznet",
            "name": "quartznet",
            "qualifier": "TRK",
            "measures": []
          },
          "components": []
        }
        """;

    /// <summary>A measure response whose rating and new-code values are both worth translating.</summary>
    internal const string MeasuresWithRatingAndPeriods = """
        {
          "component": {
            "id": "AYloNkLywDG_abJFLUp5",
            "key": "quartznet_quartznet",
            "name": "quartznet",
            "qualifier": "TRK",
            "measures": [
              { "metric": "sqale_rating", "value": "3.0", "bestValue": false },
              { "metric": "new_coverage", "periods": [ { "index": 1, "value": "82.5", "bestValue": false } ] },
              { "metric": "new_reliability_rating", "periods": [ { "index": 1, "value": "5.0" } ] }
            ]
          }
        }
        """;
}
