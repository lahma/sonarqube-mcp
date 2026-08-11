using System.Globalization;
using System.Net;
using System.Text;

using SonarQube.Mcp.Http.Models;

namespace SonarQube.Mcp.Http;

/// <summary>
/// A non-2xx response from SonarQube Cloud, carrying everything the tool-layer error funnel needs
/// to turn a status code into advice: the code itself, the parsed error envelope when the body was
/// one, the raw body when it was not, and how many times the request was already retried.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Error"/> is nullable and callers must treat it that way.</b> Verified live on
/// 2026-08-11: a <c>401</c> caused by an <em>invalid</em> bearer token comes back with
/// <c>Content-Length: 0</c> and no <c>Content-Type</c> at all, while a <c>401</c> caused by
/// <em>no</em> credential comes back with <c>{"errors":[{"msg":"Authentication is required"}]}</c>.
/// Composing a message that dereferences the envelope would produce a <c>NullReferenceException</c>
/// on precisely the failure a user is most likely to hit.
/// </para>
/// <para>
/// The raw body is kept for the same reason the template keeps it: the interesting failures are the
/// ones that do not parse — an HTML error page from a proxy, a truncated body, a status this server
/// has never seen.
/// </para>
/// </remarks>
internal sealed class SonarApiException : Exception
{
    /// <summary>
    /// Hard cap on the retained body, in characters. Bounded because this string ends up in log
    /// lines and in an <c>McpException</c> message that goes back into the model's context.
    /// </summary>
    internal const int MaxRawBodyLength = 16 * 1024;

    /// <summary>Appended in place of the tail of a body that hit <see cref="MaxRawBodyLength"/>.</summary>
    private const string TruncationMarker = "… [truncated]";

    /// <summary>Longest body excerpt quoted in <see cref="Exception.Message"/>.</summary>
    private const int MessageSnippetLength = 200;

    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="error">SonarQube's error envelope, when the body parsed as one.</param>
    /// <param name="rawBody">The response body as read; truncated to <see cref="MaxRawBodyLength"/>.</param>
    /// <param name="retryAttempts">How many retries the pipeline had already spent on this request.</param>
    /// <param name="retryAfterSeconds">
    /// The <c>Retry-After</c> the failing response carried, in whole seconds, or
    /// <see langword="null"/> when it carried none.
    /// </param>
    /// <param name="innerException">The underlying failure, if any.</param>
    internal SonarApiException(
        HttpStatusCode statusCode,
        ErrorEnvelopeDto? error,
        string? rawBody,
        int retryAttempts,
        int? retryAfterSeconds = null,
        Exception? innerException = null)
        : base(BuildMessage(statusCode, error, rawBody, retryAttempts), innerException)
    {
        StatusCode = statusCode;
        Error = error;
        RawBody = Truncate(rawBody);
        RetryAttempts = retryAttempts;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>The HTTP status code the request failed with.</summary>
    internal HttpStatusCode StatusCode { get; }

    /// <summary>
    /// SonarQube's parsed error envelope, or <see langword="null"/> when the body was empty or not
    /// JSON — which a real <c>401</c> is. Never dereference it unconditionally.
    /// </summary>
    internal ErrorEnvelopeDto? Error { get; }

    /// <summary>The response body, truncated to <see cref="MaxRawBodyLength"/>. Empty if there was none.</summary>
    internal string RawBody { get; }

    /// <summary>Retries already spent, so a 5xx message can say the request was not simply unlucky once.</summary>
    internal int RetryAttempts { get; }

    /// <summary>
    /// What <c>Retry-After</c> asked for on the response that finally failed, in whole seconds, or
    /// <see langword="null"/> when there was no usable header. This is how a 429 can name the wait
    /// instead of guessing at one.
    /// </summary>
    internal int? RetryAfterSeconds { get; }

    /// <summary>
    /// Every message SonarQube sent, joined with <c>"; "</c>, or <see langword="null"/> when it sent
    /// none.
    /// </summary>
    /// <remarks>
    /// The envelope is an <em>array</em> of messages and a 400 routinely carries more than one, so
    /// reading only the first would drop half the reason the request was rejected. Null rather than
    /// an empty string, so a composer can omit the sentence entirely instead of emitting
    /// <c>"SonarQube said: "</c> with nothing after it.
    /// </remarks>
    /// <param name="exception">The failure to read; <see langword="null"/> is answered with <see langword="null"/>.</param>
    internal static string? Detail(SonarApiException? exception) => JoinMessages(exception?.Error);

    private static string Truncate(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        return body.Length <= MaxRawBodyLength
            ? body
            : string.Concat(body.AsSpan(0, MaxRawBodyLength - TruncationMarker.Length), TruncationMarker);
    }

    private static string BuildMessage(HttpStatusCode statusCode, ErrorEnvelopeDto? error, string? rawBody, int retryAttempts)
    {
        var numeric = ((int) statusCode).ToString(CultureInfo.InvariantCulture);
        var name = statusCode.ToString();

        var message = new StringBuilder("SonarQube Cloud API returned HTTP ").Append(numeric);

        // ToString() on a status outside the enum just repeats the number, in which case appending
        // it again would read as "HTTP 599 (599)".
        if (!string.Equals(name, numeric, StringComparison.Ordinal))
        {
            message.Append(" (").Append(name).Append(')');
        }

        message.Append('.');

        // Snippet() is the fallback for a body that was not an error envelope; an *empty* body — the
        // invalid-token 401 — leaves both null, and the message is then the status alone, which is
        // still a complete sentence.
        var detail = JoinMessages(error) ?? Snippet(rawBody);

        if (detail is not null)
        {
            message.Append(' ').Append(detail);
        }

        if (retryAttempts > 0)
        {
            message
                .Append(" (after ")
                .Append(retryAttempts.ToString(CultureInfo.InvariantCulture))
                .Append(retryAttempts == 1 ? " retry)" : " retries)");
        }

        return message.ToString();
    }

    private static string? JoinMessages(ErrorEnvelopeDto? error)
    {
        if (error?.Errors is not { Count: > 0 } errors)
        {
            return null;
        }

        var joined = new StringBuilder();

        foreach (var entry in errors)
        {
            if (string.IsNullOrWhiteSpace(entry?.Msg))
            {
                continue;
            }

            if (joined.Length > 0)
            {
                joined.Append("; ");
            }

            joined.Append(entry.Msg.Trim());
        }

        return joined.Length == 0 ? null : joined.ToString();
    }

    /// <summary>Collapses a body to a single short line, so an HTML page does not become the message.</summary>
    private static string? Snippet(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        var collapsed = new StringBuilder(Math.Min(rawBody.Length, MessageSnippetLength));
        var lastWasSpace = false;

        foreach (var c in rawBody)
        {
            var isSpace = char.IsWhiteSpace(c);

            if (isSpace && lastWasSpace)
            {
                continue;
            }

            collapsed.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;

            if (collapsed.Length >= MessageSnippetLength)
            {
                collapsed.Append(TruncationMarker);
                break;
            }
        }

        var text = collapsed.ToString().Trim();
        return text.Length == 0 ? null : text;
    }
}
