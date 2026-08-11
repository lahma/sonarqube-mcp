using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace SonarQube.Mcp.Http;

/// <summary>
/// Composes the relative URL of a SonarQube Cloud request: the fixed <c>api/{service}/{action}</c>
/// path and its query parameters, each one escaped exactly once.
/// </summary>
/// <remarks>
/// <para>
/// The URL is relative on purpose — it is resolved against the client's
/// <see cref="HttpClient.BaseAddress"/>, which the configuration layer has already pinned to an
/// allowlisted SonarQube Cloud origin. No code path here can be talked into naming a different
/// host, and unlike the template there is no cursor to validate: SonarQube's paging is a pair of
/// integers a model cannot turn into a URL.
/// </para>
/// <para>
/// Every path segment goes through <see cref="Uri.EscapeDataString(string)"/>, including the ones
/// that look like constants, and every segment goes through <see cref="RequireSegment"/>. Escaping
/// alone is not enough for a segment that is <em>entirely</em> dots: <c>.</c> is an unreserved
/// character, so <see cref="Uri.EscapeDataString(string)"/> leaves <c>..</c> untouched and RFC 3986
/// dot-segment removal then collapses the path when the relative URL is resolved against the base
/// address. The path here is built from constants, so the guard should never fire — which is the
/// point of keeping it: there is then no second, unguarded path method for a future endpoint to
/// reach for.
/// </para>
/// <para>
/// Component keys never appear in a path segment. They go in the query string, where <c>:</c> and
/// <c>/</c> are ordinary characters — see <see cref="RequireComponentKey"/>.
/// </para>
/// <para>
/// Path and query are accumulated separately, so the call order of the <c>Query</c> methods and the
/// factory does not matter.
/// </para>
/// </remarks>
internal sealed class SonarRequestBuilder
{
    /// <summary>What a rejected path segment tells the caller.</summary>
    private const string NotASegmentMessage =
        "a URL path segment must name something: a blank value, \".\" or \"..\" would collapse the " +
        "request path instead of naming an endpoint.";

    /// <summary>
    /// What a rejected component key tells the caller. Written for a model mid-task: it says what a
    /// good value looks like and names the tool that produces one.
    /// </summary>
    private const string NotAComponentKeyMessage =
        "a component key looks like \"myorg_myrepo\" or \"myorg_myrepo:src/Widget.cs\". A blank " +
        "value, or one containing a \".\" or \"..\" path segment, is not a component key — call " +
        "listComponents to get a real one.";

    private readonly StringBuilder _path;
    private StringBuilder? _query;

    private SonarRequestBuilder(string service, string action)
    {
        RequireSegment(service);
        RequireSegment(action);

        _path = new StringBuilder("api")
            .Append('/').Append(Uri.EscapeDataString(service))
            .Append('/').Append(Uri.EscapeDataString(action));
    }

    /// <summary>
    /// Starts a URL at <c>api/{service}/{action}</c>, which is the shape of every SonarQube Cloud
    /// v1 web service — there are no variable path segments anywhere in this API.
    /// </summary>
    /// <param name="service">The web service, for example <c>issues</c>.</param>
    /// <param name="action">The action, for example <c>search</c>.</param>
    internal static SonarRequestBuilder Api(string service, string action) => new(service, action);

    /// <summary>
    /// Appends <c>name=value</c>, or nothing at all when the value is absent — "unset" and "set to
    /// the empty string" are different requests, and query-string callers always mean the former.
    /// </summary>
    internal SonarRequestBuilder Query(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return this;
        }

        AppendQuery(name, value);
        return this;
    }

    /// <summary>Appends a numeric query parameter, or nothing when it is <see langword="null"/>.</summary>
    internal SonarRequestBuilder Query(string name, int? value) =>
        value is null ? this : Query(name, value.GetValueOrDefault().ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Appends a boolean query parameter as <c>true</c>/<c>false</c>, or nothing when it is
    /// <see langword="null"/>. SonarQube also accepts <c>yes</c>/<c>no</c>; one spelling is enough.
    /// </summary>
    internal SonarRequestBuilder Query(string name, bool? value) =>
        value is null ? this : Query(name, value.GetValueOrDefault() ? "true" : "false");

    /// <summary>
    /// Appends a list parameter as a single <b>comma-joined</b> value —
    /// <c>issueStatuses=OPEN,CONFIRMED</c>, which is what SonarQube's multi-value parameters take.
    /// This is the exact inverse of the template's repeated-parameter form, and getting it wrong is
    /// silent: a repeated parameter is not an error, the API simply keeps one of the values.
    /// </summary>
    /// <remarks>
    /// A value containing a comma is refused rather than sent, because the API would split it and
    /// filter on two values the caller never asked for. Blank entries are dropped; an empty or
    /// all-blank list appends nothing at all.
    /// </remarks>
    /// <exception cref="ArgumentException">An element contains a comma.</exception>
    internal SonarRequestBuilder QueryList(string name, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return this;
        }

        var joined = new StringBuilder();

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();

            if (trimmed.Contains(',', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"the value \"{trimmed}\" cannot be sent as part of the '{name}' list because it " +
                    "contains a comma, which SonarQube uses to separate list values — it would be " +
                    "split into two filters. Pass the values as separate list elements instead.",
                    nameof(values));
            }

            if (joined.Length > 0)
            {
                joined.Append(',');
            }

            joined.Append(trimmed);
        }

        return joined.Length == 0 ? this : Query(name, joined.ToString());
    }

    /// <summary>The composed relative URL.</summary>
    internal string Build() => _query is null
        ? _path.ToString()
        : string.Concat(_path.ToString(), "?", _query.ToString());

    /// <inheritdoc />
    public override string ToString() => Build();

    /// <summary>
    /// Validates a component key and returns it trimmed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <c>RequireSlug</c>'s counterpart, and it is deliberately more permissive: a component
    /// key legitimately contains <c>:</c> (<c>project:path</c>) and <c>/</c> (the path inside the
    /// project), so neither can be rejected. What is rejected is a key that is blank, or whose
    /// <c>/</c>-separated parts include a <c>.</c> or <c>..</c> segment.
    /// </para>
    /// <para>
    /// The dot-segment rule does not defend the request path — component keys are query-string
    /// values, where dot segments mean nothing. It defends everything downstream that treats the
    /// part after <c>:</c> as a repository-relative file path: a key of
    /// <c>proj:../../etc/passwd</c> is not a component that exists, and refusing it here means no
    /// later consumer has to remember to.
    /// </para>
    /// </remarks>
    /// <param name="value">The candidate key.</param>
    /// <param name="paramName">Filled in by the compiler; names the caller's own argument.</param>
    /// <exception cref="ArgumentException">The value is blank or contains a dot segment.</exception>
    internal static string RequireComponentKey(
        string value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            throw new ArgumentException(NotAComponentKeyMessage, paramName);
        }

        foreach (var part in trimmed.Split('/'))
        {
            if (part is "." or "..")
            {
                throw new ArgumentException(NotAComponentKeyMessage, paramName);
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Rejects the path segments that are not names of anything: blank, <c>.</c> and <c>..</c>.
    /// Trimmed first, because <c> .. </c> is escaped to <c>%20..%20</c> but is still a caller who
    /// meant <c>..</c>.
    /// </summary>
    private static void RequireSegment(
        string value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        var trimmed = value.AsSpan().Trim();

        if (trimmed.Length == 0 || trimmed is "." or "..")
        {
            throw new ArgumentException(NotASegmentMessage, paramName);
        }
    }

    private void AppendQuery(string name, string value)
    {
        if (_query is null)
        {
            _query = new StringBuilder();
        }
        else
        {
            _query.Append('&');
        }

        _query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
    }
}
