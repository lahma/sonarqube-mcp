using System.Globalization;
using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using SonarQube.Mcp.Authentication;
using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;

namespace SonarQube.Mcp.Cli;

/// <summary>
/// <c>sonarqube-mcp status</c> — what the server would do if it started right now, and whether the
/// credential it would use actually works.
/// </summary>
/// <remarks>
/// <para>
/// This is the first thing to run when the server says it cannot authenticate, so it is deliberately
/// exhaustive about configuration and deliberately silent about values. It prints whether
/// <c>SONARQUBE_TOKEN</c> is set and never any part of it — not a prefix, not a length, not a masked
/// form. A prefix narrows a brute-force search and a length distinguishes token types, and neither
/// buys the user anything; "set" or "not set" is the whole useful content of the answer.
/// </para>
/// <para>
/// The probe is two calls, and which of them run depends on what is configured.
/// <c>authentication/validate</c> is <b>not</b> called without a token, because it answers
/// <c>{"valid":true}</c> to an anonymous request as well — printing "valid" there would be a lie.
/// With a token and an organization it also reads one project, which is the only way to tell a bad
/// token (401) from a bad organization (400/404) in one command.
/// </para>
/// <para>
/// It always exits 0. "Nothing is configured" is a fact this command reports, not a failure of it,
/// and a status probe that fails the shell is a nuisance in exactly the scripts that would use one.
/// </para>
/// </remarks>
internal static class StatusCommand
{
    /// <summary>Runs against the real environment and the real API.</summary>
    internal static Task<int> RunAsync(SonarQubeMcpOptions options, CancellationToken cancellationToken = default) =>
        RunAsync(options, Console.Out, client: null, cancellationToken);

    /// <summary>
    /// Runs against a supplied writer and, optionally, a supplied client.
    /// </summary>
    /// <param name="options">The resolved configuration to report on.</param>
    /// <param name="output">Where the report goes. Console.Out in production, a buffer in tests.</param>
    /// <param name="client">
    /// The client to probe with. <see langword="null"/> builds one from <paramref name="options"/> and
    /// disposes it; a supplied client is left alone, because its owner may want to inspect it
    /// afterwards.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<int> RunAsync(
        SonarQubeMcpOptions options,
        TextWriter output,
        SonarApiClient? client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine($"{ServerVersion.Name} {ServerVersion.Value}");
        output.WriteLine();
        output.WriteLine("Configuration");
        output.WriteLine($"  Base URL:          {options.BaseUrlText}");
        output.WriteLine($"  SONARQUBE_TOKEN:   {(options.Token is null ? "not set" : "set")}");
        output.WriteLine($"  Organization:      {CliRuntime.Describe(options.Organization)}");
        output.WriteLine($"  Default project:   {CliRuntime.Describe(options.DefaultProject)}");
        output.WriteLine($"  Read-only mode:    {(options.ReadOnly ? "on - the four write tools are not registered" : "off")}");

        if (options.RejectedBaseUrl is { } rejected)
        {
            output.WriteLine();
            output.WriteLine($"  SONARQUBE_URL was set to '{rejected}', which is not an https URL on a SonarQube Cloud");
            output.WriteLine($"  host ({string.Join(", ", SonarQubeMcpOptions.AllowedHosts)}); it was ignored.");
        }

        output.WriteLine();
        output.WriteLine("Credentials");

        // No token: the server still works against public projects, and authentication/validate is
        // not consulted because it cannot tell an anonymous request from a valid one.
        if (options.Token is null)
        {
            output.WriteLine("  No SONARQUBE_TOKEN set. Requests go out anonymously, which reads public projects");
            output.WriteLine("  only. Create a User Token - not a project analysis token - at");
            output.WriteLine($"  {options.BaseUrlText}/account/security and set SONARQUBE_TOKEN in the environment");
            output.WriteLine("  the MCP client launches this server with, then restart it.");

            return CliDispatcher.ExitSuccess;
        }

        var owned = client is null;
        var probe = client ?? CreateClient(options);

        try
        {
            await ProbeAsync(options, output, probe, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owned)
            {
                probe.Dispose();
            }
        }

        return CliDispatcher.ExitSuccess;
    }

    private static SonarApiClient CreateClient(SonarQubeMcpOptions options) =>
        new(options, new StaticTokenCredential(options), NullLoggerFactory.Instance);

    /// <summary>Runs the credential probe and reports it, never letting a network failure fail the command.</summary>
    private static async Task ProbeAsync(
        SonarQubeMcpOptions options,
        TextWriter output,
        SonarApiClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var validation = await client.ValidateAuthenticationAsync(cancellationToken).ConfigureAwait(false);

            if (validation.Valid == false)
            {
                output.WriteLine("  Token check:       REJECTED - the token was refused. It is expired, revoked, or");
                output.WriteLine($"                     issued by a different SonarQube instance than {options.BaseUrlText}.");
                output.WriteLine($"                     Create a new User Token at {options.BaseUrlText}/account/security.");
                return;
            }

            output.WriteLine($"  Token check:       accepted by {options.BaseUrlText}");
        }
        catch (Exception exception) when (exception is SonarApiException or AuthenticationRequiredException
                                              or HttpRequestException or TaskCanceledException)
        {
            output.WriteLine($"  Token check:       could not be completed - {Summarize(exception)}");
            return;
        }

        if (options.Organization is null)
        {
            output.WriteLine("  Organization:      not set. listProjects and getRule need SONARQUBE_ORG - it is the");
            output.WriteLine("                     {key} segment of https://sonarcloud.io/organizations/{key}.");
            return;
        }

        // The one call that proves the token and the organization together: authentication/validate
        // says only that the token is not rejected, and it says that anonymously too.
        try
        {
            var projects = await client
                .SearchComponentsAsync(options.Organization, query: null, page: 1, pageSize: 1, cancellationToken)
                .ConfigureAwait(false);

            var total = projects.Paging?.Total ?? projects.Total;

            output.WriteLine(total is { } count
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"  Organization:      '{options.Organization}' is readable - {count} project(s) visible.")
                : $"  Organization:      '{options.Organization}' is readable.");
        }
        catch (SonarApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            output.WriteLine("  Organization:      could not be read - the token was rejected (401). The token is the");
            output.WriteLine("                     problem here, not SONARQUBE_ORG.");
        }
        catch (SonarApiException exception) when (
            exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            output.WriteLine($"  Organization:      '{options.Organization}' was not found ({(int) exception.StatusCode}).");
            output.WriteLine("                     SONARQUBE_ORG is the organization key - the {key} segment of");
            output.WriteLine("                     https://sonarcloud.io/organizations/{key} - not its display name.");
        }
        catch (Exception exception) when (exception is SonarApiException or AuthenticationRequiredException
                                              or HttpRequestException or TaskCanceledException)
        {
            output.WriteLine($"  Organization:      could not be read - {Summarize(exception)}");
        }
    }

    /// <summary>One line about a failed probe, without the stack trace a status readout has no use for.</summary>
    private static string Summarize(Exception exception) => exception switch
    {
        SonarApiException api => string.Create(
            CultureInfo.InvariantCulture,
            $"SonarQube Cloud answered HTTP {(int) api.StatusCode}."),
        TaskCanceledException => "the request timed out.",
        _ => exception.Message,
    };
}
