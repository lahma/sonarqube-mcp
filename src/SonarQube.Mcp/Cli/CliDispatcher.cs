namespace SonarQube.Mcp.Cli;

/// <summary>
/// Hand-rolled argv dispatch (D15). No <c>System.CommandLine</c>: the surface is four verbs, and
/// the package budget is a hard constraint.
/// </summary>
/// <remarks>
/// This namespace is the only place in the server that may write to stdout — in server mode
/// stdout <em>is</em> the MCP protocol channel.
/// <para>
/// TODO(PhaseC): <c>status</c> is a placeholder here. Its real shape is the credential probe in
/// its own <c>Cli/StatusCommand.cs</c>: version, resolved base URL, whether SONARQUBE_TOKEN is set
/// (never any part of its value), organization, default project, read-only mode, then
/// <c>authentication/validate</c> — but only when a token is actually set, because that endpoint
/// answers <c>{"valid":true}</c> to an anonymous request. It always exits 0.
/// </para>
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

         Configuration is environment variables only. The ones that identify you and your data:
           SONARQUBE_TOKEN                User token, sent as a Bearer credential.
           SONARQUBE_ORG                  Organization key.
           SONARQUBE_URL                  Base URL (default https://sonarcloud.io).

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

            case "status":
                // TODO(PhaseC): replace with StatusCommand.RunAsync(SonarQubeMcpOptions.FromEnvironment()).
                // "nothing is configured" is a fact status reports, not a failure of it, so the real
                // command exits 0 in every credential state - as this placeholder already does.
                Console.Out.WriteLine($"{ServerVersion.Name} {ServerVersion.Value}");
                Console.Out.WriteLine("status: not implemented yet - the configuration surface and the API client " +
                                      "landed in Phase B; the credential probe and the reporting come in Phase C.");
                return ExitSuccess;

            default:
                Console.Error.WriteLine($"{ServerVersion.Name}: unknown argument '{command}'.");
                Console.Error.WriteLine();
                Console.Error.WriteLine(UsageText);
                return ExitUsage;
        }
    }
}
