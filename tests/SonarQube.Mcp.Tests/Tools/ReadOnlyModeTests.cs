using System.Reflection;

using ModelContextProtocol.Server;

using SonarQube.Mcp.Tools;

using Xunit;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// <c>SONARQUBE_MCP_READ_ONLY</c>, asserted where it is actually decided.
/// </summary>
/// <remarks>
/// <para>
/// Read-only mode is the <em>absence</em> of a <c>WithTools&lt;IssueWriteTools&gt;</c> registration,
/// never a check inside a tool: the five write tools do not appear in <c>tools/list</c> at all, so a
/// model never proposes a call the server would refuse. That makes
/// <see cref="McpServerSetup.ToolTypesFor"/> the single place the mode exists, and
/// <c>RunStdioAsync</c> consults the same method these tests do, so the two cannot drift.
/// </para>
/// <para>
/// The last test is the one that stops the mode being faked: the write class must still be a
/// complete, constructible set of tools in read-only mode. A flag that worked by breaking the class
/// would pass every other assertion here.
/// </para>
/// </remarks>
public class ReadOnlyModeTests
{
    /// <summary>The five tools that are absent in read-only mode, in the order the SDK builds them.</summary>
    private static readonly string[] WriteToolNames =
        ["addIssueComment", "assignIssue", "bulkUpdateIssues", "setHotspotStatus", "transitionIssue"];

    [Fact]
    public void AllFourToolClassesAreRegisteredByDefault()
    {
        var types = McpServerSetup.ToolTypesFor(ToolTestHost.CreateOptions());

        Assert.Equal(
            [typeof(ProjectReadTools), typeof(IssueReadTools), typeof(MeasureReadTools), typeof(IssueWriteTools)],
            types);
    }

    [Fact]
    public void ReadOnlyModeLeavesTheWriteClassUnregistered()
    {
        var types = McpServerSetup.ToolTypesFor(ToolTestHost.CreateOptions(readOnly: true));

        Assert.Equal(
            [typeof(ProjectReadTools), typeof(IssueReadTools), typeof(MeasureReadTools)],
            types);

        Assert.DoesNotContain(typeof(IssueWriteTools), types);
    }

    /// <summary>
    /// The claim that matters to a client: the tool collection a read-only server publishes contains
    /// none of the five write names, and exactly the eighteen read ones.
    /// </summary>
    [Fact]
    public void TheReadOnlyToolCollectionContainsNoWriteTool()
    {
        var tools = ToolTestHost.BuildTools(McpServerSetup.ToolTypesFor(ToolTestHost.CreateOptions(readOnly: true)));
        var names = tools.Select(tool => tool.ProtocolTool.Name).ToArray();

        Assert.Equal(18, names.Length);

        foreach (var write in WriteToolNames)
        {
            Assert.DoesNotContain(write, names, StringComparer.Ordinal);
        }

        Assert.Contains("searchIssues", names, StringComparer.Ordinal);
    }

    [Fact]
    public void TheFullToolCollectionContainsEveryWriteTool()
    {
        var tools = ToolTestHost.BuildTools(McpServerSetup.ToolTypesFor(ToolTestHost.CreateOptions()));
        var names = tools.Select(tool => tool.ProtocolTool.Name).ToArray();

        Assert.Equal(23, names.Length);

        foreach (var write in WriteToolNames)
        {
            Assert.Contains(write, names, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The mode removes a registration and nothing else. <see cref="IssueWriteTools"/> is still
    /// compiled, still attributed, and still builds into five complete tools — which is what makes
    /// "the flag hid them" a different claim from "the flag broke them".
    /// </summary>
    [Fact]
    public void TheWriteClassIsStillWholeInReadOnlyMode()
    {
        _ = McpServerSetup.ToolTypesFor(ToolTestHost.CreateOptions(readOnly: true));

        Assert.NotNull(typeof(IssueWriteTools).GetCustomAttribute<McpServerToolTypeAttribute>());

        var tools = ToolTestHost.BuildTools([typeof(IssueWriteTools)]);

        Assert.Equal(
            WriteToolNames,
            tools.Select(tool => tool.ProtocolTool.Name).ToArray());
    }

    [Fact]
    public void ToolTypesForRefusesToGuessWithoutOptions() =>
        Assert.Throws<ArgumentNullException>(() => McpServerSetup.ToolTypesFor(null!));
}
