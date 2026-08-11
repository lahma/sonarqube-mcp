using System.Net.Http.Headers;

using SonarQube.Mcp.Configuration;

namespace SonarQube.Mcp.Authentication;

/// <summary>
/// The whole of this server's credential handling: one static token, read from
/// <c>SONARQUBE_TOKEN</c> at startup.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the thirteen-file OAuth subsystem the template carries. SonarQube Cloud user
/// tokens do not expire on a schedule, cannot be refreshed, and are not issued by an interactive
/// flow this server could drive — so there is no token store to cache, no refresh state machine to
/// get wrong, and no browser to launch. A token that stops working is replaced by the user in the
/// environment, which is a restart, not a code path.
/// </para>
/// <para>
/// <b>No token is a legitimate state, not an error.</b> SonarQube Cloud serves public projects
/// anonymously, and reading a public project is a real use of this server (every golden fixture in
/// the test suite was captured that way). So this type reports whether a token exists and hands out
/// a header when it does; it never throws. The token-less failure is raised where it can be
/// explained — when the API answers <c>401</c> — rather than pre-emptively here, where it would
/// break the anonymous case.
/// </para>
/// </remarks>
internal sealed class StaticTokenCredential
{
    private readonly string? _token;

    /// <param name="options">The resolved configuration; only <see cref="SonarQubeMcpOptions.Token"/> is read.</param>
    internal StaticTokenCredential(SonarQubeMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _token = options.Token;
    }

    /// <summary>
    /// Whether a token is configured. This is the only question anything outside this type may ask
    /// about the token's value.
    /// </summary>
    internal bool HasToken => _token is not null;

    /// <summary>
    /// The <c>Authorization</c> header to attach, or <see langword="null"/> when no token is
    /// configured and the request therefore goes out anonymously.
    /// </summary>
    /// <remarks>
    /// A new instance per call rather than a cached one: <see cref="AuthenticationHeaderValue"/> is
    /// immutable, but caching it would invite storing it on
    /// <see cref="HttpClient.DefaultRequestHeaders"/>, which is exactly the mistake the handler
    /// exists to prevent.
    /// </remarks>
    internal AuthenticationHeaderValue? CreateHeader() =>
        _token is null ? null : new AuthenticationHeaderValue("Bearer", _token);

    /// <summary>
    /// A description safe to print, log, or return from a tool.
    /// </summary>
    /// <remarks>
    /// It deliberately reveals nothing about the token — not its length, not a prefix, not a masked
    /// form. A prefix narrows a brute-force search and a length distinguishes token types, and
    /// neither buys the user anything they cannot get from the SonarQube Cloud UI; "set" or "not
    /// set" is the entire useful content of the answer.
    /// </remarks>
    internal string Describe() => HasToken
        ? "a user token from SONARQUBE_TOKEN"
        : "no token (requests are sent anonymously)";
}
