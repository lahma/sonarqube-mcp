using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using NUnit.Framework;

using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Tools;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// D33: a call whose argument names are not the ones the tool advertised is answered before the tool
/// runs, with the name it most likely meant - instead of the SDK binding what it can and either
/// failing with no explanation (a required argument) or answering about the wrong scope (an
/// optional one).
/// </summary>
public class ToolArgumentNamesTests
{
    private static IEnumerable<string> ToolNames => ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name);

    /// <summary>The check reads the published schema, so every name a tool advertises must pass it.</summary>
    [TestCaseSource(nameof(ToolNames))]
    public void EveryAdvertisedArgumentNameIsAccepted(string name)
    {
        var tool = ToolTestHost.Find(name).ProtocolTool;
        var declared = tool.InputSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name);

        Assert.That(ToolArgumentNames.Check(tool, declared), Is.Null);
    }

    [Test]
    public void AMisCasedRequiredArgumentIsNamedRatherThanReportedMissing()
    {
        var problem = ToolArgumentNames.Check(ToolTestHost.Find("getRule").ProtocolTool, ["RuleKey"]);

        Assert.That(problem, Does.StartWith("getRule was not run: ").Using(StringComparison.Ordinal));
        Assert.That(problem, Contains.Substring("'RuleKey' is spelled 'ruleKey': argument names are case-sensitive.").Using(StringComparison.Ordinal));
        Assert.That(problem, Does.EndWith("Its arguments are ruleKey, sections, context.").Using(StringComparison.Ordinal));
        Assert.That(problem, Does.Not.Contain("required"));
    }

    /// <summary>The silent case: without the check this call would read the default project.</summary>
    [Test]
    public void AMisCasedOptionalArgumentIsRefusedRatherThanDropped()
    {
        var problem = ToolArgumentNames.Check(
            ToolTestHost.Find("searchIssues").ProtocolTool, ["ProjectKey", "pullRequest"]);

        Assert.That(problem, Contains.Substring("'ProjectKey' is spelled 'projectKey'").Using(StringComparison.Ordinal));
        Assert.That(problem, Does.Not.Contain("'pullRequest'"));
    }

    [TestCase("getIssue", "issueKeys", "issueKey")]
    [TestCase("searchIssues", "authour", "authors")]
    [TestCase("searchIssues", "project", "projectKey")]
    [TestCase("listComponentMeasures", "sortBy", "sortByMetric")]
    public void ANearMissIsAnsweredWithWhatItMostLikelyMeant(string tool, string sent, string meant)
    {
        var problem = ToolArgumentNames.Check(ToolTestHost.Find(tool).ProtocolTool, [sent]);

        Assert.That(problem, Contains.Substring($"'{sent}' is not one of its arguments; did you mean '{meant}'?").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// D19: the organization is configuration, never an argument. A name that is close to nothing gets
    /// the accepted list and no guess.
    /// </summary>
    [Test]
    public void AnUnrelatedNameGetsTheAcceptedNamesAndNoGuess()
    {
        var problem = ToolArgumentNames.Check(ToolTestHost.Find("getRule").ProtocolTool, ["ruleKey", "organization"]);

        Assert.That(problem, Contains.Substring("'organization' is not one of its arguments.").Using(StringComparison.Ordinal));
        Assert.That(problem, Does.Not.Contain("did you mean"));
        Assert.That(problem, Does.EndWith("Its arguments are ruleKey, sections, context.").Using(StringComparison.Ordinal));
    }

    [Test]
    public void AMissingRequiredArgumentIsNamed()
    {
        var problem = ToolArgumentNames.Check(ToolTestHost.Find("getIssue").ProtocolTool, []);

        Assert.That(problem, Contains.Substring("'issueKey' is required and was not passed.").Using(StringComparison.Ordinal));
    }

    [Test]
    public void ACallWithNoArgumentsToAToolWithoutRequiredOnesPasses() =>
        Assert.That(ToolArgumentNames.Check(ToolTestHost.Find("listProjects").ProtocolTool, []), Is.Null);

    // ------------------------------------------------------------------ over the protocol

    /// <summary>
    /// The same refusal end to end: the production registration (McpServerSetup.AddServer) served over
    /// in-memory streams to a real client, so the filter is proven to be registered and to answer as
    /// a tool error the model can read and retry from.
    /// </summary>
    [Test]
    public async Task OverTheProtocolAMisCasedArgumentIsAToolErrorNamingTheRightSpelling()
    {
        await using var session = await InMemorySession.StartAsync(TestContext.CurrentContext.CancellationToken);

        var result = await session.Client.CallToolAsync(
            new CallToolRequestParams { Name = "getRule", Arguments = Arguments(("RuleKey", "csharpsquid:S2259")) },
            TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.True);
        Assert.That(Text(result), Contains.Substring("'RuleKey' is spelled 'ruleKey'").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// A correctly spelled call goes through to the tool: here getRule's own refusal, because the
    /// session has no organization configured - which also keeps the test off the network.
    /// </summary>
    [Test]
    public async Task OverTheProtocolACorrectlySpelledCallReachesTheTool()
    {
        await using var session = await InMemorySession.StartAsync(TestContext.CurrentContext.CancellationToken);

        var result = await session.Client.CallToolAsync(
            new CallToolRequestParams { Name = "getRule", Arguments = Arguments(("ruleKey", "csharpsquid:S2259")) },
            TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.True);
        Assert.That(Text(result), Contains.Substring("SONARQUBE_ORG").Using(StringComparison.Ordinal));
        Assert.That(Text(result), Does.Not.Contain("was not run"));
    }

    private static Dictionary<string, JsonElement> Arguments(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Name, pair => JsonSerializer.SerializeToElement(pair.Value, ToolTestHost.SerializerOptions));

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    /// <summary>The production server and a client, connected by two in-memory pipes.</summary>
    private sealed class InMemorySession : IAsyncDisposable
    {
        private readonly Pipe _toServer;
        private readonly Pipe _toClient;
        private readonly ServiceProvider _provider;
        private readonly CancellationTokenSource _stop;
        private readonly Task _server;

        private InMemorySession(Pipe toServer, Pipe toClient, ServiceProvider provider, CancellationTokenSource stop, Task server, McpClient client)
        {
            _toServer = toServer;
            _toClient = toClient;
            _provider = provider;
            _stop = stop;
            _server = server;
            Client = client;
        }

        internal McpClient Client { get; }

        internal static async Task<InMemorySession> StartAsync(CancellationToken cancellationToken)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();

            // No organization, so a call that does reach getRule stops at its own check, offline.
            SonarQubeMcpOptions options = ToolTestHost.CreateOptions(organization: null, logLevel: "Warning");

            var services = new ServiceCollection();
            McpServerSetup.AddServer(services, options)
                .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());

            var provider = services.BuildServiceProvider();
            var stop = new CancellationTokenSource();
            var server = provider.GetRequiredService<McpServer>().RunAsync(stop.Token);

            var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
                cancellationToken: cancellationToken);

            return new InMemorySession(toServer, toClient, provider, stop, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();

            try
            {
                await _server;
            }
            catch (OperationCanceledException)
            {
                // The cancellation above is how the server is stopped.
            }

            await _toServer.Writer.CompleteAsync();
            await _toClient.Writer.CompleteAsync();
            await _provider.DisposeAsync();
            _stop.Dispose();
        }
    }
}
