using Microsoft.Extensions.Logging;

using SonarQube.Mcp.Configuration;

namespace SonarQube.Mcp.Cli;

/// <summary>
/// The few things the CLI commands share: a logger factory, and the labels they print.
/// </summary>
/// <remarks>
/// Logging still goes to stderr in CLI mode, exactly as in server mode. A command's own output —
/// what the user asked for — goes to stdout, which only this namespace may touch. Keeping the two
/// apart is what makes <c>sonarqube-mcp status</c> pipeable while a warning about a rejected
/// <c>SONARQUBE_URL</c> is still visible.
/// </remarks>
internal static class CliRuntime
{
    /// <summary>What an unset variable reads as. Never the value itself, set or unset.</summary>
    internal const string NotSet = "(not set)";

    /// <summary>Builds the stderr logger factory the CLI commands share.</summary>
    internal static ILoggerFactory CreateLoggerFactory(SonarQubeMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(options.LogLevel);
            logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });
    }

    /// <summary>Renders an optional configuration value without ever inventing one.</summary>
    internal static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? NotSet : value;
}
