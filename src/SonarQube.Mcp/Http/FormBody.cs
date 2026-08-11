using System.Net.Http.Headers;
using System.Text;

namespace SonarQube.Mcp.Http;

/// <summary>
/// Builds an <c>application/x-www-form-urlencoded</c> request body.
/// </summary>
/// <remarks>
/// <para>
/// Every write in this server goes through here. SonarQube's write endpoints take form parameters —
/// not query parameters and not JSON — so there are no request-body DTOs anywhere in this codebase,
/// which is a genuine simplification over the template's five.
/// </para>
/// <para>
/// <b>Deliberately not <see cref="FormUrlEncodedContent"/>.</b> That type derives from
/// <see cref="ByteArrayContent"/> in the current runtime, but nothing in its contract says so, and
/// <see cref="RetryHandler.IsResendable"/> is a type test: the day it stops deriving from it, every
/// write silently becomes non-retryable and a 429 during a triage session starts failing calls that
/// used to succeed. Encoding to bytes here makes "a write survives a 429" a property of this file
/// rather than of an implementation detail. <see cref="FormUrlEncodedContent"/> also caps values at
/// the legacy <c>Uri.EscapeDataString</c> length limit, which a long issue comment can exceed.
/// </para>
/// </remarks>
internal sealed class FormBody
{
    private readonly StringBuilder _body = new();

    /// <summary>
    /// Appends <c>name=value</c>.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> omits the parameter; the <b>empty string is sent</b>. The difference
    /// is load-bearing exactly once, and it is not hypothetical: <c>issues/assign</c> with
    /// <c>assignee=</c> unassigns the issue, while omitting <c>assignee</c> leaves it alone.
    /// </remarks>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The value; <see langword="null"/> to omit the parameter entirely.</param>
    internal FormBody Add(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (value is null)
        {
            return this;
        }

        if (_body.Length > 0)
        {
            _body.Append('&');
        }

        _body.Append(Encode(name)).Append('=').Append(Encode(value));
        return this;
    }

    /// <summary>The encoded body, ready to send.</summary>
    /// <remarks>
    /// The <c>Content-Type</c> carries no <c>charset</c> parameter: form encoding percent-escapes
    /// every non-ASCII byte, so the payload is ASCII by construction and a charset would only be a
    /// second, potentially disagreeing, statement about it. UTF-8 rather than ASCII does the
    /// encoding anyway — for ASCII input the bytes are identical, and if anything ever did slip
    /// through unescaped, UTF-8 sends it while <see cref="Encoding.ASCII"/> would silently replace
    /// it with a question mark.
    /// </remarks>
    internal ByteArrayContent Build()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(_body.ToString()));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return content;
    }

    /// <inheritdoc />
    public override string ToString() => _body.ToString();

    /// <summary>
    /// Percent-encodes one name or value in the form-encoding dialect.
    /// </summary>
    /// <remarks>
    /// <see cref="Uri.EscapeDataString(string)"/> leaves only RFC 3986's unreserved set
    /// (<c>A-Za-z0-9-._~</c>) alone and percent-encodes everything else, including <c>+</c> as
    /// <c>%2B</c> — which is what makes the space-to-<c>+</c> substitution afterwards unambiguous.
    /// Doing it the other way round (replace spaces first, then escape) would encode the <c>+</c>
    /// signs it had just written.
    /// </remarks>
    private static string Encode(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
}
