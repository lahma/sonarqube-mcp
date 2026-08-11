using SonarQube.Mcp.Authentication;

namespace SonarQube.Mcp.Http;

/// <summary>
/// The outermost handler: attaches the <c>Authorization</c> header to every request, and does
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The header goes on each request, never on <see cref="HttpClient.DefaultRequestHeaders"/>.</b>
/// A default header rides along on every request the chain issues — including any this server ever
/// grows that should not carry the credential — and it is invisible at the call site. Setting it
/// here keeps "which requests are authenticated" a property of one readable method.
/// </para>
/// <para>
/// <b>No 401 retry loop</b> (the template has one). A static token cannot be refreshed: a second
/// attempt with the same token is guaranteed to fail identically, so the loop would only turn a
/// clear <c>401</c> into a slow one. The client maps the status instead.
/// </para>
/// <para>
/// <b>No redirect following</b> (the template hand-rolls one). The transport sets
/// <c>AllowAutoRedirect = false</c> and a <c>3xx</c> becomes a <c>SonarApiException</c> naming the
/// <c>Location</c>. No <c>api/</c> endpoint on SonarQube Cloud is known to redirect, and if one
/// starts, a loud failure is a better answer than a silently unauthenticated second request — which
/// is what automatic redirect following would produce, because <see cref="SocketsHttpHandler"/>
/// strips <c>Authorization</c> on every redirect, same-origin ones included.
/// </para>
/// <para>
/// <b>It never throws for a missing token.</b> SonarQube Cloud serves public projects anonymously,
/// so a request with no header is a legitimate request with a good chance of a <c>200</c>. Failing
/// here would break that; instead the request goes out bare and the API's own <c>401</c> — mapped
/// by the client into the message that names <c>SONARQUBE_TOKEN</c> — is what tells the user the
/// project needed a credential.
/// </para>
/// </remarks>
internal sealed class AuthenticationHandler : DelegatingHandler
{
    private readonly StaticTokenCredential _credential;

    /// <param name="credential">Source of the header; may legitimately have none.</param>
    internal AuthenticationHandler(StaticTokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        _credential = credential;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Null when no token is configured, which leaves the request anonymous rather than sending
        // an empty or malformed Authorization header — SonarQube Cloud answers 401 to the latter
        // and 200-for-public-content to the former.
        request.Headers.Authorization = _credential.CreateHeader();

        return base.SendAsync(request, cancellationToken);
    }
}
