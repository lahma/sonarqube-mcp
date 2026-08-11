namespace SonarQube.Mcp.Authentication;

/// <summary>
/// Signals that SonarQube Cloud refused the request and this server has no token to blame for it —
/// the user has not configured one.
/// </summary>
/// <remarks>
/// <para>
/// The template's equivalent carries a six-value <c>Reason</c> enum, because an OAuth flow can fail
/// in six distinguishable ways. Here there is exactly one: <c>SONARQUBE_TOKEN</c> is not set. A
/// token that <em>is</em> set and is rejected is a different story with different advice, and it
/// travels as a <c>SonarApiException</c> with status <c>401</c>, so there is nothing for a caller
/// to switch on and no enum to carry.
/// </para>
/// <para>
/// Unlike the template's, this exception composes its own user-facing text. There is only one
/// message, it needs only the base URL to compose, and putting it here keeps the actionable half of
/// the token story next to the type that represents it rather than in a status-code table where it
/// would read as one row among ten.
/// </para>
/// <para>
/// It is raised by <c>SonarApiClient</c> when a <c>401</c> arrives and no token is configured —
/// never pre-emptively before sending. Sending anonymously first is what lets a public project be
/// read without a token at all; the exception is what turns the resulting <c>401</c> on a private
/// one into an instruction instead of a status code.
/// </para>
/// </remarks>
internal sealed class AuthenticationRequiredException : Exception
{
    /// <param name="baseUrl">
    /// The configured SonarQube Cloud origin, so the message can name the exact page the token is
    /// created on rather than assuming sonarcloud.io.
    /// </param>
    /// <param name="innerException">The underlying failure, if any.</param>
    internal AuthenticationRequiredException(string baseUrl, Exception? innerException = null)
        : base(BuildMessage(baseUrl), innerException)
    {
    }

    /// <summary>
    /// The full text, composed once so that the tool layer, the CLI and any future caller all say
    /// the same thing.
    /// </summary>
    /// <param name="baseUrl">The configured origin, without a trailing slash.</param>
    internal static string BuildMessage(string baseUrl) =>
        "No SonarQube token is configured, so this server cannot call the API.\n" +
        "Set SONARQUBE_TOKEN in the environment the MCP client launches this server with, then " +
        $"restart it. Create one at {baseUrl}/account/security — a User Token, not a project " +
        "analysis token.\n" +
        "Also set SONARQUBE_ORG to your organization key (the /organizations/{key} segment of the " +
        "sonarcloud.io URL).\n" +
        "Run `sonarqube-mcp status` to check what this server currently sees.";
}
