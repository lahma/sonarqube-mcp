using System.Runtime.InteropServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SonarQube.Mcp.Authentication;
using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;

namespace SonarQube.Mcp;

/// <summary>
/// Builds and runs the stdio MCP server.
/// </summary>
/// <remarks>
/// Nothing in this file — or anything it starts — may write to stdout: stdout is the JSON-RPC
/// channel and a stray write corrupts the protocol stream. Logging goes to stderr.
/// <para>
/// TODO(PhaseC): the graph below builds the options and the API client, but no tool is registered
/// yet. Phase C adds the tool-facing serializer options, the server instructions and one
/// <c>WithTools&lt;T&gt;(jsonOptions)</c> call per tool class, with read-only mode expressed as the
/// <em>absence</em> of the write-tool call.
/// </para>
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

        services
            .AddMcpServer(serverOptions =>
            {
                serverOptions.ServerInfo = new Implementation
                {
                    Name = ServerVersion.Name,
                    Version = ServerVersion.Value,
                };

                // A server with no tool registered at all advertises no `tools` capability and has
                // nothing to answer `tools/list` with, which would make the SmokeTest handshake fail
                // for a reason that has nothing to do with the transport. An empty collection is the
                // honest answer - "tools are supported, there are none yet" - and `??=` means the
                // line stays correct once Phase C's WithTools<T> calls fill the collection in.
                serverOptions.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
            })
            .WithStdioServerTransport();

        await using var provider = services.BuildServiceProvider();

        // SonarQubeMcpOptions.FromEnvironment never throws, so a rejected SONARQUBE_URL has already
        // silently fallen back by the time we get here. This is the first moment a logger exists to
        // say so - and it must be a log line rather than a startup failure, because stdout is the
        // protocol channel and a dead server has no way to explain itself.
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

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
