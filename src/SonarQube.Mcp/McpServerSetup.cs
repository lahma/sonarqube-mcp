using System.Runtime.InteropServices;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SonarQube.Mcp.Authentication;
using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;
using SonarQube.Mcp.Tools;
using SonarQube.Mcp.Tools.Models;

namespace SonarQube.Mcp;

/// <summary>
/// Builds and runs the stdio MCP server.
/// </summary>
/// <remarks>
/// Nothing in this file — or anything it starts — may write to stdout: stdout is the JSON-RPC
/// channel and a stray write corrupts the protocol stream. Logging goes to stderr.
/// </remarks>
internal static class McpServerSetup
{
    /// <summary>
    /// Runs the server until stdin closes or the process is asked to shut down, then returns the
    /// process exit code.
    /// </summary>
    internal static async Task<int> RunStdioAsync()
    {
        using var shutdown = new CancellationTokenSource();

        // Cooperative shutdown: cancel the server loop rather than letting the runtime tear the
        // process down mid-write (D3 - there is no generic host to own lifetime for us).
        using var sigInt = RegisterShutdownSignal(PosixSignal.SIGINT, shutdown);
        using var sigTerm = RegisterShutdownSignal(PosixSignal.SIGTERM, shutdown);

        // Environment variables only (D3). This is the one and only read; everything downstream
        // takes the resulting options object.
        var options = SonarQubeMcpOptions.FromEnvironment();

        // D3: a bare ServiceCollection, not Host.CreateApplicationBuilder - no configuration
        // providers, metrics or lifetime machinery in the cold-start path.
        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(options.LogLevel);

            // The one line that keeps the protocol stream clean: every log record, at every level,
            // goes to stderr. stdout belongs to JSON-RPC alone.
            logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });

        // Every registration below is an explicit factory rather than AddSingleton<T>(): the
        // constructors are internal (which the container's reflection-based selection does not
        // see), and writing the graph out by hand keeps it reflection-free for AOT and readable as
        // the wiring diagram it is.
        //
        // None of these run at startup. They are constructed on first use, and constructing them
        // touches neither disk nor network - there is nothing to authenticate against until a
        // request is actually made.
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new StaticTokenCredential(sp.GetRequiredService<SonarQubeMcpOptions>()));
        services.AddSingleton(sp => new SonarApiClient(
            sp.GetRequiredService<SonarQubeMcpOptions>(),
            sp.GetRequiredService<StaticTokenCredential>(),
            sp.GetRequiredService<ILoggerFactory>()));

        var jsonOptions = CreateToolSerializerOptions();
        var toolTypes = ToolTypesFor(options);

        var builder = services
            .AddMcpServer(serverOptions =>
            {
                serverOptions.ServerInfo = new Implementation
                {
                    Name = ServerVersion.Name,
                    Version = ServerVersion.Value,
                };

                // Sent to the client at initialize: the conventions no single tool description can
                // carry (project keys, branch/pullRequest exclusivity, absent-is-not-zero, ratings).
                serverOptions.ServerInstructions = ServerInstructions.Text;
            })
            .WithStdioServerTransport();

        // One WithTools<T>(jsonOptions) per tool class - never WithToolsFromAssembly, which is not
        // AOT-safe (IL2026). These also populate the tool collection, which is what makes the server
        // advertise the `tools` capability and answer `tools/list`; the Phase A placeholder
        // collection is therefore gone, and read-only mode still registers three classes.
        builder.WithTools<ProjectReadTools>(jsonOptions);
        builder.WithTools<IssueReadTools>(jsonOptions);
        builder.WithTools<MeasureReadTools>(jsonOptions);

        // Read-only mode is the *absence* of this registration, not a check inside the tools: the
        // four write tools do not appear in tools/list at all, so a model never proposes a call the
        // server would refuse. ToolTypesFor is the single source of truth, shared with the tests.
        if (toolTypes.Contains(typeof(IssueWriteTools)))
        {
            builder.WithTools<IssueWriteTools>(jsonOptions);
        }

        await using var provider = services.BuildServiceProvider();

        // SonarQubeMcpOptions.FromEnvironment never throws, so a rejected SONARQUBE_URL has already
        // silently fallen back by the time we get here. This is the first moment a logger exists to
        // say so - and it must be a log line rather than a startup failure, because stdout is the
        // protocol channel and a dead server has no way to explain itself.
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        // The error funnel's only two dependencies. Resolved here rather than passed into every tool
        // method, so that no tool signature carries a parameter the schema then has to exclude.
        ToolErrors.UseLoggerFactory(loggerFactory);
        ToolErrors.UseOptions(options);

        if (options.RejectedBaseUrl is { } rejected)
        {
            loggerFactory.CreateLogger(typeof(McpServerSetup)).LogWarning(
                "SONARQUBE_URL is set to {Rejected}, which is not an https URL on a SonarQube Cloud host " +
                "({AllowedHosts}); falling back to {BaseUrl}.",
                rejected,
                string.Join(", ", SonarQubeMcpOptions.AllowedHosts),
                options.BaseUrl);
        }

        // The SDK registers McpServer as a singleton; running it directly is the SDK's own AOT
        // test-app shape (D3).
        var server = provider.GetRequiredService<McpServer>();

        try
        {
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Signalled shutdown is a normal exit.
        }

        return Cli.CliDispatcher.ExitSuccess;
    }

    /// <summary>
    /// The tool classes this configuration registers: all four normally, three when
    /// <c>SONARQUBE_MCP_READ_ONLY</c> is set.
    /// </summary>
    /// <remarks>
    /// The read-only mode is expressed as a registration this method leaves out, never as a runtime
    /// check inside a tool. <c>IssueWriteTools</c> is still compiled and still constructible in that
    /// mode — the flag removes it from the advertised surface, and nothing else.
    /// <para>
    /// Factored out so the tests can assert the selection against the production rule rather than a
    /// copy of it, and so <see cref="RunStdioAsync"/> has exactly one place to consult.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<Type> ToolTypesFor(SonarQubeMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.ReadOnly
            ? [typeof(ProjectReadTools), typeof(IssueReadTools), typeof(MeasureReadTools)]
            : [typeof(ProjectReadTools), typeof(IssueReadTools), typeof(MeasureReadTools), typeof(IssueWriteTools)];
    }

    /// <summary>
    /// Builds the tool-facing serializer options that every <c>WithTools&lt;T&gt;</c> registration —
    /// and therefore every generated tool schema — is created with.
    /// </summary>
    /// <remarks>
    /// Ours goes FIRST in the chain so that JIT and AOT resolve identically; the SDK resolver stays
    /// second for MCP protocol types, which our context returns null for. The chain is cleared first
    /// because copying the SDK's options copies its chain too, and a duplicate entry ahead of ours
    /// would decide the tie.
    /// <para>
    /// Factored out of <see cref="RunStdioAsync"/> so the schema tests can generate schemas with the
    /// exact options the server ships, rather than a hand-rolled copy that could drift out of step.
    /// </para>
    /// </remarks>
    internal static JsonSerializerOptions CreateToolSerializerOptions()
    {
        var jsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        jsonOptions.TypeInfoResolverChain.Clear();
        jsonOptions.TypeInfoResolverChain.Add(SonarToolJsonContext.Default);
        jsonOptions.TypeInfoResolverChain.Add(McpJsonUtilities.DefaultOptions.TypeInfoResolver!);
        jsonOptions.MakeReadOnly();

        return jsonOptions;
    }

    private static PosixSignalRegistration? RegisterShutdownSignal(PosixSignal signal, CancellationTokenSource shutdown)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, context =>
            {
                // Suppress the default action (immediate termination) and unwind the server loop.
                context.Cancel = true;

                try
                {
                    shutdown.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already shutting down.
                }
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
