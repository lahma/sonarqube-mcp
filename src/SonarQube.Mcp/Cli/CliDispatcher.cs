using SonarQube.Mcp.Configuration;

namespace SonarQube.Mcp.Cli;

/// <summary>
/// Hand-rolled argv dispatch. No <c>System.CommandLine</c>: the surface is four verbs, and the
/// package budget is a hard constraint.
/// </summary>
/// <remarks>
/// This namespace is the only place in the server that may write to stdout — in server mode stdout
/// <em>is</em> the MCP protocol channel.
/// </remarks>
internal static class CliDispatcher
{
    /// <summary>Command completed successfully.</summary>
    internal const int ExitSuccess = 0;

    /// <summary>Command ran but failed.</summary>
    internal const int ExitFailure = 1;

    /// <summary>The command line could not be understood.</summary>
    internal const int ExitUsage = 2;

    internal static string UsageText { get; } =
        $"""
         {ServerVersion.Name} {ServerVersion.Value} - Model Context Protocol server for SonarQube Cloud.

         Usage:
           {ServerVersion.Name} [serve]     Run the MCP server over stdio (default when no arguments are given).
           {ServerVersion.Name} status      Show the resolved configuration and probe the credential.

         Options:
           -h, --help                     Show this help text.
           -v, --version                  Show the version.

         Configuration is environment variables only.
           SONARQUBE_TOKEN                User token, sent as a Bearer credential. Create one under
                                          Account > Security; a project analysis token does not work.
           SONARQUBE_ORG                  Organization key - the last segment of
                                          https://sonarcloud.io/organizations/<key>.
           SONARQUBE_URL                  Base URL (default https://sonarcloud.io). Only SonarQube
                                          Cloud hosts are accepted.
           SONARQUBE_MCP_DEFAULT_PROJECT  Makes the projectKey tool parameter optional.
           SONARQUBE_MCP_READ_ONLY        1 to register only the fifteen read tools.
           SONARQUBE_MCP_LOG_LEVEL        Trace|Debug|Information|Warning|Error|Critical|None
                                          (default Information). Logs go to stderr.
           SONARQUBE_MCP_MAX_PAGE_SIZE    Ceiling on a tool's pageSize, 1-500 (default 100).
           SONARQUBE_MCP_DEFAULT_PAGE_SIZE  pageSize when a tool call omits it (default 50).
           SONARQUBE_MCP_MAX_SOURCE_LINES   Cap on getFileCoverage's line span (default 2000).
           SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS  Whole-request timeout, 5-600 (default 100).

         Run `{ServerVersion.Name} status` to see which of these are in effect; the README documents
         the full set.
         """;

    /// <summary>Dispatches <paramref name="args"/> and returns the process exit code.</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        // No arguments is the common case: MCP clients launch the binary with none at all.
        var command = args.Length == 0 ? "serve" : args[0];

        switch (command)
        {
            case "serve":
                return await McpServerSetup.RunStdioAsync().ConfigureAwait(false);

            case "version":
            case "--version":
            case "-v":
                Console.Out.WriteLine(ServerVersion.Value);
                return ExitSuccess;

            case "help":
            case "--help":
            case "-h":
                Console.Out.WriteLine(UsageText);
                return ExitSuccess;

            // Reads the environment itself, exactly as the server does, so that what `status`
            // reports is what `serve` would use.
            case "status":
                return await StatusCommand.RunAsync(SonarQubeMcpOptions.FromEnvironment()).ConfigureAwait(false);

            default:
                Console.Error.WriteLine($"{ServerVersion.Name}: unknown argument '{command}'.");
                Console.Error.WriteLine();
                Console.Error.WriteLine(UsageText);
                return ExitUsage;
        }
    }
}
