using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.Reflection;

using ModelContextProtocol.Server;

using NUnit.Framework;

using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;

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

    [Test]
    public void ExactlyTwentyThreeToolsAreDeclaredAcrossTheFourToolClasses()
    {
        Assert.That(ToolTestHost.ToolTypes.Count, Is.EqualTo(4));
        Assert.That(ToolTestHost.ToolMethods.Count, Is.EqualTo(ExpectedToolNames.Length));
        Assert.That(ToolTestHost.Tools.Count, Is.EqualTo(ExpectedToolNames.Length));
        Assert.That(ExpectedToolNames.Length, Is.EqualTo(23));
    }

    [Test]
    public void ToolNamesAreExactlyThePlannedOnes()
    {
        var actual = ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name).ToArray();

        Assert.That(actual, Is.EqualTo(ExpectedToolNames));
    }

    /// <summary>The four classes are exactly the ones the design names, and no fifth one exists.</summary>
    [Test]
    public void TheToolClassesAreTheFourThePlanNames()
    {
        var names = ToolTestHost.ToolTypes.Select(type => type.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.That(
            names,
            Is.EqualTo<string[]>(["IssueReadTools", "IssueWriteTools", "MeasureReadTools", "ProjectReadTools"]));
    }

    [TestCase("listProjects", "List projects")]
    [TestCase("listComponents", "List components")]
    [TestCase("listBranches", "List branches")]
    [TestCase("listPullRequests", "List pull requests")]
    [TestCase("getQualityGateStatus", "Get quality gate status")]
    [TestCase("getAnalysisStatus", "Get analysis status")]
    [TestCase("searchIssues", "Search issues")]
    [TestCase("getIssue", "Get issue")]
    [TestCase("getIssueChangelog", "Get issue changelog")]
    [TestCase("summarizeIssues", "Summarize issues")]
    [TestCase("getRule", "Get rule")]
    [TestCase("searchHotspots", "Search security hotspots")]
    [TestCase("getHotspot", "Get security hotspot")]
    [TestCase("getComponentMeasures", "Get measures")]
    [TestCase("listComponentMeasures", "List measures by component")]
    [TestCase("getMeasuresHistory", "Get measures history")]
    [TestCase("listMetrics", "List metrics")]
    [TestCase("getFileCoverage", "Get file coverage")]
    [TestCase("transitionIssue", "Transition issue")]
    [TestCase("assignIssue", "Assign issue")]
    [TestCase("addIssueComment", "Add issue comment")]
    [TestCase("setHotspotStatus", "Set hotspot status")]
    [TestCase("bulkUpdateIssues", "Update many issues")]
    public void TitleIsTheOneThePlanSpecifies(string name, string expectedTitle)
    {
        var tool = ToolTestHost.Find(name).ProtocolTool;

        Assert.That(tool.Title, Is.EqualTo(expectedTitle));
        Assert.That(tool.Annotations?.Title, Is.EqualTo(expectedTitle));
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
    [TestCase("listProjects")]
    [TestCase("listComponents")]
    [TestCase("listBranches")]
    [TestCase("listPullRequests")]
    [TestCase("getQualityGateStatus")]
    [TestCase("getAnalysisStatus")]
    [TestCase("searchIssues")]
    [TestCase("summarizeIssues")]
    [TestCase("getIssue")]
    [TestCase("getIssueChangelog")]
    [TestCase("getRule")]
    [TestCase("searchHotspots")]
    [TestCase("getHotspot")]
    [TestCase("getComponentMeasures")]
    [TestCase("listComponentMeasures")]
    [TestCase("getMeasuresHistory")]
    [TestCase("listMetrics")]
    [TestCase("getFileCoverage")]
    public void ReadToolsAreReadOnlyIdempotentOpenWorldAndSayNothingAboutDestruction(string name)
    {
        var annotations = ToolTestHost.Find(name).ProtocolTool.Annotations;

        Assert.That(annotations, Is.Not.Null);
        Assert.That(annotations!.ReadOnlyHint, Is.True);
        Assert.That(annotations.IdempotentHint, Is.True);
        Assert.That(annotations.OpenWorldHint, Is.True);
        Assert.That(annotations.DestructiveHint, Is.Null);
    }

    /// <summary>
    /// The five write tools. None of them deletes anything, so all five say
    /// <c>destructiveHint: false</c> explicitly — the SDK's default is <see langword="true"/>.
    /// Idempotence splits on whether the caller names an end state (<c>assignIssue</c>,
    /// <c>setHotspotStatus</c>) or an action (<c>transitionIssue</c>, <c>addIssueComment</c>,
    /// <c>bulkUpdateIssues</c> — which can carry a comment, and a comment repeats).
    /// </summary>
    [TestCase("transitionIssue", false)]
    [TestCase("assignIssue", true)]
    [TestCase("addIssueComment", false)]
    [TestCase("setHotspotStatus", true)]
    [TestCase("bulkUpdateIssues", false)]
    public void WriteToolsAreNonDestructiveAndDeclareIdempotenceExplicitly(string name, bool idempotent)
    {
        var annotations = ToolTestHost.Find(name).ProtocolTool.Annotations;

        Assert.That(annotations, Is.Not.Null);
        Assert.That(annotations!.ReadOnlyHint, Is.False);
        Assert.That(annotations.DestructiveHint, Is.EqualTo(false));
        Assert.That(annotations.IdempotentHint, Is.EqualTo(idempotent));
        Assert.That(annotations.OpenWorldHint, Is.True);
    }

    [Test]
    public void EveryToolAdvertisesStructuredOutput()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            Assert.That(
                tool.ProtocolTool.OutputSchema is not null,
                Is.True,
                $"{tool.ProtocolTool.Name} has no output schema; UseStructuredContent must be true.");
        }
    }

    [Test]
    public void EveryToolHasATitle()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            Assert.That(
                string.IsNullOrWhiteSpace(tool.ProtocolTool.Title),
                Is.False,
                $"{tool.ProtocolTool.Name} has no title; it is what a client shows a human.");
        }
    }

    /// <summary>
    /// Sealed, not <c>static</c>: C# forbids a static class as the type argument of
    /// <c>WithTools&lt;T&gt;</c> (CS0718). The private constructor is what keeps it uninstantiable
    /// anyway, and the methods themselves must be static so no instance is activated per call.
    /// </summary>
    [Test]
    public void ToolClassesAreSealedAttributedAndUninstantiable()
    {
        foreach (var type in ToolTestHost.ToolTypes)
        {
            Assert.That(type.IsSealed, Is.True, $"{type.Name} must be sealed.");
            Assert.That(type.GetCustomAttribute<McpServerToolTypeAttribute>(), Is.Not.Null);

            var publicConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.That(publicConstructors, Is.Empty);
        }
    }

    [Test]
    public void EveryToolMethodIsPublicStaticAndReturnsATaskOfAShapedResult()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            Assert.That(method.IsPublic, Is.True, $"{method.Name} must be public.");
            Assert.That(method.IsStatic, Is.True, $"{method.Name} must be static.");

            Assert.That(
                method.ReturnType.IsGenericType
                && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>),
                Is.True,
                $"{method.Name} must return Task<T>, not {method.ReturnType.Name}.");

            var resultType = method.ReturnType.GetGenericArguments()[0];

            Assert.That(resultType.Namespace, Is.EqualTo("SonarQube.Mcp.Tools.Models"));
        }
    }

    [Test]
    public void EveryToolMethodHasADescription()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;

            Assert.That(
                string.IsNullOrWhiteSpace(description),
                Is.False,
                $"{method.Name} needs a [Description]; it is what the model reads to choose the tool.");
        }
    }

    [Test]
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

                Assert.That(
                    string.IsNullOrWhiteSpace(description),
                    Is.False,
                    $"{method.Name}({parameter.Name}) needs a [Description]; it becomes the schema description.");
            }
        }
    }

    /// <summary>
    /// The token is the SDK's cancellation wiring, and a defaulted trailing parameter is the only
    /// shape that keeps every other argument callable by name and position from a test.
    /// </summary>
    [Test]
    public void CancellationTokenIsTheLastParameterOfEveryTool()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var parameters = method.GetParameters();
            var last = parameters[^1];

            Assert.That(last.ParameterType, Is.EqualTo(typeof(CancellationToken)));
            Assert.That(last.HasDefaultValue, Is.True, $"{method.Name}'s cancellationToken must be optional.");

            Assert.That(
                parameters[..^1],
                Has.None.Matches<ParameterInfo>(parameter => parameter!.ParameterType == typeof(CancellationToken)));
        }
    }

    /// <summary>
    /// Both collaborators are plain parameters the container binds; every tool takes them, and takes
    /// them first, so the model-facing arguments are the tail of the signature.
    /// </summary>
    [Test]
    public void EveryToolTakesItsCollaboratorsAsTheFirstTwoParameters()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var parameters = method.GetParameters();

            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(SonarApiClient)));
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(SonarQubeMcpOptions)));
        }
    }

    /// <summary>
    /// Every tool method's name is its MCP name plus <c>Async</c>, so a stack trace, a log line and
    /// a <c>tools/list</c> entry all name the same thing.
    /// </summary>
    [Test]
    public void EveryToolMethodIsNamedAfterTheToolItDeclares()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name;

            Assert.That(method.Name, Is.EqualTo(char.ToUpperInvariant(name![0]) + name[1..] + "Async"));
        }
    }
}
