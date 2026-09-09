using System.ComponentModel;
using System.Reflection;

using ModelContextProtocol.Server;

using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;

using Xunit;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// The tool surface as a contract: which tools exist, what they are called, and what an MCP client
/// is told about each one before it decides whether to ask the user first.
/// </summary>
/// <remarks>
/// <para>
/// The annotation table is the load-bearing part. <c>Destructive</c> defaults to
/// <see langword="true"/> in the SDK, so a write tool that forgets to say otherwise makes clients
/// prompt before every comment, and a read tool that says anything at all adds noise to a decision
/// <c>readOnlyHint</c> has already made.
/// </para>
/// <para>
/// Everything is asserted against the values an MCP client actually receives — the
/// <see cref="McpServerTool.ProtocolTool"/> built through the SDK with the production serializer
/// options — rather than against the attribute, so an SDK change in how attributes become
/// annotations cannot pass unnoticed.
/// </para>
/// </remarks>
public class ToolInventoryTests
{
    /// <summary>
    /// The design's tool list (the AGENTS.md tool table), in full and in ordinal order. Adding a tool means
    /// editing this array, the AGENTS.md tool table, <c>Build.cs</c>'s <c>ExpectedToolNames</c> and
    /// the skill.
    /// </summary>
    private static readonly string[] ExpectedToolNames =
    [
        "addIssueComment",
        "assignIssue",
        "bulkUpdateIssues",
        "getAnalysisStatus",
        "getComponentMeasures",
        "getFileCoverage",
        "getHotspot",
        "getIssue",
        "getIssueChangelog",
        "getMeasuresHistory",
        "getQualityGateStatus",
        "getRule",
        "listBranches",
        "listComponentMeasures",
        "listComponents",
        "listMetrics",
        "listProjects",
        "listPullRequests",
        "searchHotspots",
        "searchIssues",
        "setHotspotStatus",
        "summarizeIssues",
        "transitionIssue",
    ];

    /// <summary>
    /// The parameter types the SDK binds from DI or from the protocol, and therefore leaves out of
    /// the generated schema. Everything else is a model-supplied argument.
    /// </summary>
    private static readonly Type[] InjectedParameterTypes =
    [
        typeof(SonarApiClient),
        typeof(SonarQubeMcpOptions),
        typeof(CancellationToken),
    ];

    [Fact]
    public void ExactlyTwentyThreeToolsAreDeclaredAcrossTheFourToolClasses()
    {
        Assert.Equal(4, ToolTestHost.ToolTypes.Count);
        Assert.Equal(ExpectedToolNames.Length, ToolTestHost.ToolMethods.Count);
        Assert.Equal(ExpectedToolNames.Length, ToolTestHost.Tools.Count);
        Assert.Equal(23, ExpectedToolNames.Length);
    }

    [Fact]
    public void ToolNamesAreExactlyThePlannedOnes()
    {
        var actual = ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name).ToArray();

        Assert.Equal(ExpectedToolNames, actual);
    }

    /// <summary>The four classes are exactly the ones the design names, and no fifth one exists.</summary>
    [Fact]
    public void TheToolClassesAreTheFourThePlanNames()
    {
        var names = ToolTestHost.ToolTypes.Select(type => type.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            ["IssueReadTools", "IssueWriteTools", "MeasureReadTools", "ProjectReadTools"],
            names);
    }

    [Theory]
    [InlineData("listProjects", "List projects")]
    [InlineData("listComponents", "List components")]
    [InlineData("listBranches", "List branches")]
    [InlineData("listPullRequests", "List pull requests")]
    [InlineData("getQualityGateStatus", "Get quality gate status")]
    [InlineData("getAnalysisStatus", "Get analysis status")]
    [InlineData("searchIssues", "Search issues")]
    [InlineData("getIssue", "Get issue")]
    [InlineData("getIssueChangelog", "Get issue changelog")]
    [InlineData("summarizeIssues", "Summarize issues")]
    [InlineData("getRule", "Get rule")]
    [InlineData("searchHotspots", "Search security hotspots")]
    [InlineData("getHotspot", "Get security hotspot")]
    [InlineData("getComponentMeasures", "Get measures")]
    [InlineData("listComponentMeasures", "List measures by component")]
    [InlineData("getMeasuresHistory", "Get measures history")]
    [InlineData("listMetrics", "List metrics")]
    [InlineData("getFileCoverage", "Get file coverage")]
    [InlineData("transitionIssue", "Transition issue")]
    [InlineData("assignIssue", "Assign issue")]
    [InlineData("addIssueComment", "Add issue comment")]
    [InlineData("setHotspotStatus", "Set hotspot status")]
    [InlineData("bulkUpdateIssues", "Update many issues")]
    public void TitleIsTheOneThePlanSpecifies(string name, string expectedTitle)
    {
        var tool = ToolTestHost.Find(name).ProtocolTool;

        Assert.Equal(expectedTitle, tool.Title);
        Assert.Equal(expectedTitle, tool.Annotations?.Title);
    }

    /// <summary>
    /// The eighteen read tools, whose whole annotation story is "changes nothing, same answer twice,
    /// talks to a system outside this process". <c>destructiveHint</c> must be <b>absent</b>: the
    /// SDK omits it unless the attribute sets it, and a destructive hint on a read-only tool is
    /// noise in front of a decision <c>readOnlyHint</c> has already made.
    /// <para>
    /// <c>getAnalysisStatus</c> is idempotent here in the sense the annotation means — calling it
    /// changes nothing — even though its answer is expected to differ between calls, which is the
    /// entire reason it exists. The hint is about effects, not about a stable response.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("listProjects")]
    [InlineData("listComponents")]
    [InlineData("listBranches")]
    [InlineData("listPullRequests")]
    [InlineData("getQualityGateStatus")]
    [InlineData("getAnalysisStatus")]
    [InlineData("searchIssues")]
    [InlineData("summarizeIssues")]
    [InlineData("getIssue")]
    [InlineData("getIssueChangelog")]
    [InlineData("getRule")]
    [InlineData("searchHotspots")]
    [InlineData("getHotspot")]
    [InlineData("getComponentMeasures")]
    [InlineData("listComponentMeasures")]
    [InlineData("getMeasuresHistory")]
    [InlineData("listMetrics")]
    [InlineData("getFileCoverage")]
    public void ReadToolsAreReadOnlyIdempotentOpenWorldAndSayNothingAboutDestruction(string name)
    {
        var annotations = ToolTestHost.Find(name).ProtocolTool.Annotations;

        Assert.NotNull(annotations);
        Assert.True(annotations.ReadOnlyHint);
        Assert.True(annotations.IdempotentHint);
        Assert.True(annotations.OpenWorldHint);
        Assert.Null(annotations.DestructiveHint);
    }

    /// <summary>
    /// The five write tools. None of them deletes anything, so all five say
    /// <c>destructiveHint: false</c> explicitly — the SDK's default is <see langword="true"/>.
    /// Idempotence splits on whether the caller names an end state (<c>assignIssue</c>,
    /// <c>setHotspotStatus</c>) or an action (<c>transitionIssue</c>, <c>addIssueComment</c>,
    /// <c>bulkUpdateIssues</c> — which can carry a comment, and a comment repeats).
    /// </summary>
    [Theory]
    [InlineData("transitionIssue", false)]
    [InlineData("assignIssue", true)]
    [InlineData("addIssueComment", false)]
    [InlineData("setHotspotStatus", true)]
    [InlineData("bulkUpdateIssues", false)]
    public void WriteToolsAreNonDestructiveAndDeclareIdempotenceExplicitly(string name, bool idempotent)
    {
        var annotations = ToolTestHost.Find(name).ProtocolTool.Annotations;

        Assert.NotNull(annotations);
        Assert.False(annotations.ReadOnlyHint);
        Assert.Equal(false, annotations.DestructiveHint);
        Assert.Equal(idempotent, annotations.IdempotentHint);
        Assert.True(annotations.OpenWorldHint);
    }

    [Fact]
    public void EveryToolAdvertisesStructuredOutput()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            Assert.True(
                tool.ProtocolTool.OutputSchema is not null,
                $"{tool.ProtocolTool.Name} has no output schema; UseStructuredContent must be true.");
        }
    }

    [Fact]
    public void EveryToolHasATitle()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(tool.ProtocolTool.Title),
                $"{tool.ProtocolTool.Name} has no title; it is what a client shows a human.");
        }
    }

    /// <summary>
    /// Sealed, not <c>static</c>: C# forbids a static class as the type argument of
    /// <c>WithTools&lt;T&gt;</c> (CS0718). The private constructor is what keeps it uninstantiable
    /// anyway, and the methods themselves must be static so no instance is activated per call.
    /// </summary>
    [Fact]
    public void ToolClassesAreSealedAttributedAndUninstantiable()
    {
        foreach (var type in ToolTestHost.ToolTypes)
        {
            Assert.True(type.IsSealed, $"{type.Name} must be sealed.");
            Assert.NotNull(type.GetCustomAttribute<McpServerToolTypeAttribute>());

            var publicConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.Empty(publicConstructors);
        }
    }

    [Fact]
    public void EveryToolMethodIsPublicStaticAndReturnsATaskOfAShapedResult()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            Assert.True(method.IsPublic, $"{method.Name} must be public.");
            Assert.True(method.IsStatic, $"{method.Name} must be static.");

            Assert.True(
                method.ReturnType.IsGenericType
                && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>),
                $"{method.Name} must return Task<T>, not {method.ReturnType.Name}.");

            var resultType = method.ReturnType.GetGenericArguments()[0];

            Assert.Equal("SonarQube.Mcp.Tools.Models", resultType.Namespace);
        }
    }

    [Fact]
    public void EveryToolMethodHasADescription()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;

            Assert.False(
                string.IsNullOrWhiteSpace(description),
                $"{method.Name} needs a [Description]; it is what the model reads to choose the tool.");
        }
    }

    [Fact]
    public void EveryModelSuppliedParameterHasADescription()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            foreach (var parameter in method.GetParameters())
            {
                if (InjectedParameterTypes.Contains(parameter.ParameterType))
                {
                    continue;
                }

                var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;

                Assert.False(
                    string.IsNullOrWhiteSpace(description),
                    $"{method.Name}({parameter.Name}) needs a [Description]; it becomes the schema description.");
            }
        }
    }

    /// <summary>
    /// The token is the SDK's cancellation wiring, and a defaulted trailing parameter is the only
    /// shape that keeps every other argument callable by name and position from a test.
    /// </summary>
    [Fact]
    public void CancellationTokenIsTheLastParameterOfEveryTool()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var parameters = method.GetParameters();
            var last = parameters[^1];

            Assert.Equal(typeof(CancellationToken), last.ParameterType);
            Assert.True(last.HasDefaultValue, $"{method.Name}'s cancellationToken must be optional.");

            Assert.DoesNotContain(
                parameters[..^1],
                parameter => parameter.ParameterType == typeof(CancellationToken));
        }
    }

    /// <summary>
    /// Both collaborators are plain parameters the container binds; every tool takes them, and takes
    /// them first, so the model-facing arguments are the tail of the signature.
    /// </summary>
    [Fact]
    public void EveryToolTakesItsCollaboratorsAsTheFirstTwoParameters()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var parameters = method.GetParameters();

            Assert.Equal(typeof(SonarApiClient), parameters[0].ParameterType);
            Assert.Equal(typeof(SonarQubeMcpOptions), parameters[1].ParameterType);
        }
    }

    /// <summary>
    /// Every tool method's name is its MCP name plus <c>Async</c>, so a stack trace, a log line and
    /// a <c>tools/list</c> entry all name the same thing.
    /// </summary>
    [Fact]
    public void EveryToolMethodIsNamedAfterTheToolItDeclares()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name;

            Assert.Equal(char.ToUpperInvariant(name![0]) + name[1..] + "Async", method.Name);
        }
    }
}
