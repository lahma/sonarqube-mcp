using System.Globalization;

using Microsoft.Extensions.Logging;

namespace SonarQube.Mcp.Configuration;

/// <summary>
/// Every knob the server has, read once from environment variables. There are deliberately no
/// configuration files and no <c>Microsoft.Extensions.Configuration</c> providers (D3): an MCP
/// client launches the binary with an environment block and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FromEnvironment()"/> never throws. A malformed number, log level or base URL falls
/// back to the documented default, because failing startup over a typo would leave the MCP client
/// with a dead server and no way to see why — stdout is the protocol channel, so there is nowhere
/// to complain. Anything genuinely unusable (a missing token) surfaces on first use as an
/// actionable error instead.
/// </para>
/// <para>
/// The one rejection worth telling the user about — a <c>SONARQUBE_URL</c> that is not an allowed
/// SonarQube Cloud origin — is recorded in <see cref="RejectedBaseUrl"/> rather than logged here,
/// because this type is constructed before the logging pipeline exists. <c>McpServerSetup</c> logs
/// it to stderr at the first moment a logger is available, which also makes the rejection
/// observable in a test without capturing console output.
/// </para>
/// </remarks>
internal sealed record SonarQubeMcpOptions
{
    /// <summary>
    /// The default SonarQube Cloud origin. Ends with <c>/</c> because it is used as an
    /// <see cref="HttpClient.BaseAddress"/>, against which every request URL is resolved as a
    /// relative one — a base address without the trailing slash would drop its last segment.
    /// </summary>
    internal static readonly Uri DefaultBaseUrl = new("https://sonarcloud.io/");

    /// <summary>Default minimum log level.</summary>
    internal const LogLevel DefaultLogLevel = LogLevel.Information;

    /// <summary>Default ceiling on a tool's <c>pageSize</c>.</summary>
    internal const int DefaultMaxPageSize = 100;

    /// <summary>Hard ceiling on <see cref="MaxPageSize"/> — SonarQube Cloud's own <c>ps</c> limit.</summary>
    internal const int PageSizeLimit = 500;

    /// <summary>Default page size a tool sends when the caller omits one.</summary>
    internal const int DefaultDefaultPageSize = 50;

    /// <summary>Default cap on the line span <c>getFileCoverage</c> will ask for.</summary>
    internal const int DefaultMaxSourceLines = 2000;

    /// <summary>Default whole-request timeout, in seconds.</summary>
    internal const int DefaultHttpTimeoutSeconds = 100;

    /// <summary>
    /// The only hosts <c>SONARQUBE_URL</c> may name. SonarQube Cloud runs on exactly these; a URL
    /// anywhere else is either a SonarQube Server instance (out of scope — the API surface differs)
    /// or an attempt to point a configured token at a host that is not SonarSource's.
    /// </summary>
    /// <remarks>
    /// <c>api.sonarcloud.io</c>, the v2 API host, is deliberately absent: nothing here speaks v2.
    /// </remarks>
    internal static readonly IReadOnlyList<string> AllowedHosts =
    [
        "sonarcloud.io",
        "www.sonarcloud.io",
        "sonarqube.us",
    ];

    /// <summary>
    /// <c>SONARQUBE_TOKEN</c> — the user token sent as <c>Authorization: Bearer</c>. Absent is legal
    /// at startup: the handshake completes and the first tool call fails with an actionable message
    /// naming this variable.
    /// </summary>
    internal string? Token { get; init; }

    /// <summary>
    /// <c>SONARQUBE_ORG</c> — the organization key, the <c>/organizations/{key}</c> segment of a
    /// sonarcloud.io URL. Required only by the endpoints that take an <c>organization</c> parameter.
    /// </summary>
    internal string? Organization { get; init; }

    /// <summary>
    /// <c>SONARQUBE_URL</c> — the SonarQube Cloud origin, always with a trailing slash so it can be
    /// used directly as an <see cref="HttpClient.BaseAddress"/>.
    /// </summary>
    internal Uri BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>
    /// The <c>SONARQUBE_URL</c> value that was rejected, or <see langword="null"/> when the variable
    /// was unset or accepted. Purely diagnostic: <see cref="BaseUrl"/> already fell back to
    /// <see cref="DefaultBaseUrl"/>.
    /// </summary>
    internal string? RejectedBaseUrl { get; init; }

    /// <summary>
    /// <c>SONARQUBE_MCP_DEFAULT_PROJECT</c> — makes the <c>projectKey</c> tool parameter optional.
    /// </summary>
    internal string? DefaultProject { get; init; }

    /// <summary>
    /// <c>SONARQUBE_MCP_READ_ONLY</c> — when true the write-tool class is never registered, so the
    /// four write tools do not appear in <c>tools/list</c> at all.
    /// </summary>
    internal bool ReadOnly { get; init; }

    /// <summary><c>SONARQUBE_MCP_LOG_LEVEL</c> — minimum level for the stderr logger.</summary>
    internal LogLevel LogLevel { get; init; } = DefaultLogLevel;

    /// <summary>
    /// <c>SONARQUBE_MCP_MAX_PAGE_SIZE</c> — the ceiling a tool clamps <c>pageSize</c> to. Bounded by
    /// <see cref="PageSizeLimit"/>, which is the API's own hard limit.
    /// </summary>
    internal int MaxPageSize { get; init; } = DefaultMaxPageSize;

    /// <summary>
    /// <c>SONARQUBE_MCP_DEFAULT_PAGE_SIZE</c> — what a tool sends when <c>pageSize</c> is omitted.
    /// Never greater than <see cref="MaxPageSize"/>: it is re-clamped after both are read, so
    /// lowering the maximum alone cannot leave the default above it.
    /// </summary>
    internal int DefaultPageSize { get; init; } = DefaultDefaultPageSize;

    /// <summary><c>SONARQUBE_MCP_MAX_SOURCE_LINES</c> — cap on a requested <c>from</c>–<c>to</c> span.</summary>
    internal int MaxSourceLines { get; init; } = DefaultMaxSourceLines;

    /// <summary><c>SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS</c> — whole-request timeout.</summary>
    internal int HttpTimeoutSeconds { get; init; } = DefaultHttpTimeoutSeconds;

    /// <summary>
    /// The base URL without its trailing slash, which is the form a human-readable message wants —
    /// <c>https://sonarcloud.io/account/security</c>, not <c>https://sonarcloud.io//account/…</c>.
    /// </summary>
    internal string BaseUrlText => BaseUrl.AbsoluteUri.TrimEnd('/');

    /// <summary>Reads the options from the process environment.</summary>
    internal static SonarQubeMcpOptions FromEnvironment() =>
        FromEnvironment(static name => Environment.GetEnvironmentVariable(name));

    /// <summary>
    /// Reads the options from an arbitrary variable source. Tests use this overload so they never
    /// have to mutate the (process-wide, test-parallelism-hostile) real environment.
    /// </summary>
    /// <param name="read">Returns the raw value of a variable, or <see langword="null"/> if unset.</param>
    internal static SonarQubeMcpOptions FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var maxPageSize = ReadInt32(read, "SONARQUBE_MCP_MAX_PAGE_SIZE", DefaultMaxPageSize, 1, PageSizeLimit);
        var defaultPageSize = ReadInt32(read, "SONARQUBE_MCP_DEFAULT_PAGE_SIZE", DefaultDefaultPageSize, 1, PageSizeLimit);

        var rawBaseUrl = ReadString(read, "SONARQUBE_URL");
        var baseUrl = TryParseBaseUrl(rawBaseUrl);

        return new SonarQubeMcpOptions
        {
            Token = ReadString(read, "SONARQUBE_TOKEN"),
            Organization = ReadString(read, "SONARQUBE_ORG"),
            BaseUrl = baseUrl ?? DefaultBaseUrl,
            RejectedBaseUrl = baseUrl is null ? rawBaseUrl : null,
            DefaultProject = ReadString(read, "SONARQUBE_MCP_DEFAULT_PROJECT"),
            ReadOnly = ReadBoolean(read, "SONARQUBE_MCP_READ_ONLY", defaultValue: false),
            LogLevel = ReadLogLevel(read, "SONARQUBE_MCP_LOG_LEVEL", DefaultLogLevel),
            MaxPageSize = maxPageSize,

            // Re-clamped rather than read against a bound that is not known yet: the two variables
            // are independent, and SONARQUBE_MCP_MAX_PAGE_SIZE=10 alone must not leave the default
            // at 50 and every omitted pageSize silently above the configured ceiling.
            DefaultPageSize = Math.Min(defaultPageSize, maxPageSize),
            MaxSourceLines = ReadInt32(read, "SONARQUBE_MCP_MAX_SOURCE_LINES", DefaultMaxSourceLines, 1, 20_000),
            HttpTimeoutSeconds = ReadInt32(read, "SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS", DefaultHttpTimeoutSeconds, 5, 600),
        };
    }

    /// <summary>
    /// Parses <c>SONARQUBE_URL</c>, or returns <see langword="null"/> for anything that is not a
    /// bare <c>https</c> origin on an allowed SonarQube Cloud host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host allowlist is the load-bearing check: the base address is what every relative request
    /// URL is resolved against, so it is the one value that decides which host a configured token is
    /// sent to. An allowlist — rather than "any https URL" — means a mistyped or hostile value
    /// cannot redirect the credential to somebody else's server.
    /// </para>
    /// <para>
    /// A path, query, fragment, user-info or non-default port is rejected as well. All three allowed
    /// hosts serve the API from the origin root on 443, so any of those means the value was meant
    /// for something else — most likely a SonarQube Server instance, whose API this server does not
    /// speak.
    /// </para>
    /// </remarks>
    private static Uri? TryParseBaseUrl(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return null;
        }

        var allowed = false;

        foreach (var host in AllowedHosts)
        {
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
                break;
            }
        }

        if (!allowed || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
        {
            return null;
        }

        // "https://sonarcloud.io" and "https://sonarcloud.io/" both arrive with AbsolutePath "/",
        // which is the trailing-slash normalisation the design asks for; anything longer is a path.
        if (uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return null;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    /// <summary>Trims and normalises an unset or all-whitespace variable to <see langword="null"/>.</summary>
    private static string? ReadString(Func<string, string?> read, string name)
    {
        var raw = read(name);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>Parses an integer, falling back to <paramref name="defaultValue"/> when unparsable or out of range.</summary>
    private static int ReadInt32(Func<string, string?> read, string name, int defaultValue, int min, int max)
    {
        var raw = ReadString(read, name);

        if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return value < min || value > max ? defaultValue : value;
    }

    /// <summary>
    /// Parses a boolean flag. <c>1/true/yes/on</c> and <c>0/false/no/off</c> are accepted (either
    /// case); anything else falls back to <paramref name="defaultValue"/>.
    /// </summary>
    private static bool ReadBoolean(Func<string, string?> read, string name, bool defaultValue)
    {
        var raw = ReadString(read, name);

        return raw?.ToUpperInvariant() switch
        {
            "1" or "TRUE" or "YES" or "ON" => true,
            "0" or "FALSE" or "NO" or "OFF" => false,
            _ => defaultValue,
        };
    }

    /// <summary>Parses a <see cref="Microsoft.Extensions.Logging.LogLevel"/> name, falling back to the default.</summary>
    private static LogLevel ReadLogLevel(Func<string, string?> read, string name, LogLevel defaultValue)
    {
        var raw = ReadString(read, name);

        // Enum.TryParse also accepts raw numbers, so IsDefined is what actually rejects "42".
        if (raw is null || !Enum.TryParse<LogLevel>(raw, ignoreCase: true, out var level) || !Enum.IsDefined(level))
        {
            return defaultValue;
        }

        return level;
    }
}
