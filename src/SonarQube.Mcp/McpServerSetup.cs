using System.Runtime.InteropServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SonarQube.Mcp;

/// <summary>
/// Builds and runs the stdio MCP server.
/// </summary>
/// <remarks>
/// Nothing in this file — or anything it starts — may write to stdout: stdout is the JSON-RPC
/// channel and a stray write corrupts the protocol stream. Logging goes to stderr.
/// <para>
/// TODO(PhaseB/PhaseC): this is the Phase A skeleton — it completes a handshake and nothing else.
/// Phase B adds the options read and the API client to the graph; Phase C adds the serializer
/// options, the server instructions and one <c>WithTools&lt;T&gt;(jsonOptions)</c> call per tool
/// class, with read-only mode expressed as the <em>absence</em> of the write-tool call.
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

        // D3: a bare ServiceCollection, not Host.CreateApplicationBuilder - no configuration
        // providers, metrics or lifetime machinery in the cold-start path.
        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);

            // The one line that keeps the protocol stream clean: every log record, at every level,
            // goes to stderr. stdout belongs to JSON-RPC alone.
            logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });

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
