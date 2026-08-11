using Microsoft.Extensions.Logging;

using SonarQube.Mcp.Configuration;

using Xunit;

namespace SonarQube.Mcp.Tests;

/// <summary>
/// Covers <see cref="SonarQubeMcpOptions.FromEnvironment(Func{string, string?})"/>: every
/// variable's default, its clamp, and what a bad value falls back to.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes through the <see cref="Func{T, TResult}"/> overload, so no test mutates the real
/// process environment — which is global, shared with every parallel test, and would make these
/// results depend on execution order.
/// </para>
/// <para>
/// The rule under test throughout is that <b>reading configuration never throws</b>. A typo in an
/// MCP client's environment block must not produce a server that dies at startup, because stdout is
/// the protocol channel and a dead server has nowhere to explain itself.
/// </para>
/// </remarks>
public class ConfigurationTests
{
    /// <summary>Values that mean true, and the ones that mean false.</summary>
    public static TheoryData<string, bool> BooleanValues => new()
    {
        { "1", true },
        { "true", true },
        { "TRUE", true },
        { "yes", true },
        { "on", true },
        { "0", false },
        { "false", false },
        { "no", false },
        { "off", false },

        // Anything else is not a considered opinion, so it falls back to the default (false).
        { "maybe", false },
        { "2", false },
        { "", false },
    };

    /// <summary>URLs the allowlist accepts, and the normalised base address each produces.</summary>
    public static TheoryData<string, string> AcceptedUrls => new()
    {
        { "https://sonarcloud.io", "https://sonarcloud.io/" },
        { "https://sonarcloud.io/", "https://sonarcloud.io/" },
        { "https://www.sonarcloud.io", "https://www.sonarcloud.io/" },
        { "https://sonarqube.us", "https://sonarqube.us/" },
        { "https://sonarqube.us/", "https://sonarqube.us/" },
        { "  https://sonarcloud.io  ", "https://sonarcloud.io/" },
        { "https://SonarCloud.IO", "https://sonarcloud.io/" },
    };

    /// <summary>URLs the allowlist rejects, each for a different reason.</summary>
    public static TheoryData<string> RejectedUrls =>
    [
        "https://evil.example.com",
        "http://sonarcloud.io",
        "https://api.sonarcloud.io",
        "https://sonarcloud.io.evil.example.com",
        "https://sonarcloud.io:8443",
        "https://user:pass@sonarcloud.io",
        "https://sonarcloud.io/some/path",
        "https://sonarcloud.io/?a=b",
        "sonarcloud.io",
        "not a url at all",
        "ftp://sonarcloud.io",
    ];

    [Fact]
    public void AnEmptyEnvironmentProducesEveryDocumentedDefault()
    {
        var options = Read();

        Assert.Null(options.Token);
        Assert.Null(options.Organization);
        Assert.Equal(SonarQubeMcpOptions.DefaultBaseUrl, options.BaseUrl);
        Assert.Null(options.RejectedBaseUrl);
        Assert.Null(options.DefaultProject);
        Assert.False(options.ReadOnly);
        Assert.Equal(LogLevel.Information, options.LogLevel);
        Assert.Equal(100, options.MaxPageSize);
        Assert.Equal(50, options.DefaultPageSize);
        Assert.Equal(2000, options.MaxSourceLines);
        Assert.Equal(100, options.HttpTimeoutSeconds);
    }

    [Fact]
    public void FromEnvironmentReadsTheRealProcessEnvironmentWithoutThrowing()
    {
        // The no-argument overload is what production calls; it must be exercised at least once,
        // whatever the machine's environment happens to hold.
        var options = SonarQubeMcpOptions.FromEnvironment();

        Assert.NotNull(options.BaseUrl);
        Assert.InRange(options.MaxPageSize, 1, SonarQubeMcpOptions.PageSizeLimit);
        Assert.InRange(options.DefaultPageSize, 1, options.MaxPageSize);
    }

    // ------------------------------------------------------------------ strings

    [Fact]
    public void TheTokenIsTrimmed()
    {
        Assert.Equal("squ_abc", Read(("SONARQUBE_TOKEN", "  squ_abc  ")).Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ABlankTokenIsTheSameAsNoToken(string value)
    {
        // A variable set to the empty string is how a client config expresses "not configured";
        // treating it as a token would send "Bearer " and get a 401 nobody could explain.
        Assert.Null(Read(("SONARQUBE_TOKEN", value)).Token);
    }

    [Fact]
    public void TheOrganizationAndDefaultProjectAreTrimmed()
    {
        var options = Read(
            ("SONARQUBE_ORG", " quartznet "),
            ("SONARQUBE_MCP_DEFAULT_PROJECT", " quartznet_quartznet "));

        Assert.Equal("quartznet", options.Organization);
        Assert.Equal("quartznet_quartznet", options.DefaultProject);
    }

    // ------------------------------------------------------------------ base URL allowlist

    [Theory]
    [MemberData(nameof(AcceptedUrls))]
    public void AnAllowlistedOriginIsAcceptedAndNormalisedWithATrailingSlash(string value, string expected)
    {
        var options = Read(("SONARQUBE_URL", value));

        // The trailing slash is not cosmetic: HttpClient drops the last path segment of a base
        // address that lacks one, so "https://sonarcloud.io" would resolve "api/x" against the root.
        Assert.Equal(new Uri(expected), options.BaseUrl);
        Assert.Null(options.RejectedBaseUrl);
    }

    [Theory]
    [MemberData(nameof(RejectedUrls))]
    public void AnythingElseFallsBackToTheDefaultAndIsRecordedForLogging(string value)
    {
        var options = Read(("SONARQUBE_URL", value));

        Assert.Equal(SonarQubeMcpOptions.DefaultBaseUrl, options.BaseUrl);

        // Recorded rather than logged here, because this type is built before the logging pipeline
        // exists; McpServerSetup emits the warning at the first moment a logger does.
        Assert.Equal(value.Trim(), options.RejectedBaseUrl);
    }

    [Fact]
    public void AnUnsetUrlIsNotARejection()
    {
        Assert.Null(Read().RejectedBaseUrl);
    }

    [Fact]
    public void TheBaseUrlTextDropsTheTrailingSlashForMessages()
    {
        // What the token-missing message interpolates: "{base}/account/security", which must not
        // come out as "https://sonarcloud.io//account/security".
        Assert.Equal("https://sonarcloud.io", Read().BaseUrlText);
    }

    // ------------------------------------------------------------------ booleans and enums

    [Theory]
    [MemberData(nameof(BooleanValues))]
    public void ReadOnlyModeAcceptsTheDocumentedSpellings(string value, bool expected)
    {
        Assert.Equal(expected, Read(("SONARQUBE_MCP_READ_ONLY", value)).ReadOnly);
    }

    [Theory]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("TRACE", LogLevel.Trace)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("None", LogLevel.None)]
    public void TheLogLevelIsParsedCaseInsensitively(string value, LogLevel expected)
    {
        Assert.Equal(expected, Read(("SONARQUBE_MCP_LOG_LEVEL", value)).LogLevel);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("-1")]
    [InlineData("Verbose")]
    [InlineData("")]
    public void AnUnrecognisedLogLevelFallsBackToInformation(string value)
    {
        // Enum.TryParse accepts bare numbers, so "42" would otherwise become a LogLevel of 42 and
        // silently suppress every log line.
        Assert.Equal(LogLevel.Information, Read(("SONARQUBE_MCP_LOG_LEVEL", value)).LogLevel);
    }

    // ------------------------------------------------------------------ numbers

    [Theory]
    [InlineData("1", 1)]
    [InlineData("250", 250)]
    [InlineData("500", 500)]
    public void MaxPageSizeAcceptsItsWholeRange(string value, int expected)
    {
        Assert.Equal(expected, Read(("SONARQUBE_MCP_MAX_PAGE_SIZE", value)).MaxPageSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("501")]
    [InlineData("banana")]
    [InlineData("1e3")]
    [InlineData("")]
    public void AnOutOfRangeMaxPageSizeFallsBackToTheDefault(string value)
    {
        // 500 is SonarQube Cloud's own ceiling; a configured value above it could only ever produce
        // a 400 on every call.
        Assert.Equal(100, Read(("SONARQUBE_MCP_MAX_PAGE_SIZE", value)).MaxPageSize);
    }

    [Fact]
    public void DefaultPageSizeIsAcceptedWithinTheMaximum()
    {
        var options = Read(
            ("SONARQUBE_MCP_MAX_PAGE_SIZE", "200"),
            ("SONARQUBE_MCP_DEFAULT_PAGE_SIZE", "150"));

        Assert.Equal(200, options.MaxPageSize);
        Assert.Equal(150, options.DefaultPageSize);
    }

    [Fact]
    public void DefaultPageSizeIsReclampedWhenTheMaximumIsLoweredBelowIt()
    {
        var options = Read(("SONARQUBE_MCP_MAX_PAGE_SIZE", "10"));

        // Lowering only the ceiling must not leave every omitted pageSize silently above it. The
        // two variables are read independently, so this is a second pass, not a parse rule.
        Assert.Equal(10, options.MaxPageSize);
        Assert.Equal(10, options.DefaultPageSize);
    }

    [Fact]
    public void DefaultPageSizeIsReclampedEvenWhenBothAreSetInconsistently()
    {
        var options = Read(
            ("SONARQUBE_MCP_MAX_PAGE_SIZE", "20"),
            ("SONARQUBE_MCP_DEFAULT_PAGE_SIZE", "400"));

        Assert.Equal(20, options.MaxPageSize);
        Assert.Equal(20, options.DefaultPageSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("501")]
    [InlineData("nope")]
    public void AnOutOfRangeDefaultPageSizeFallsBackToFifty(string value)
    {
        Assert.Equal(50, Read(("SONARQUBE_MCP_DEFAULT_PAGE_SIZE", value)).DefaultPageSize);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("500", 500)]
    [InlineData("20000", 20_000)]
    public void MaxSourceLinesAcceptsItsRange(string value, int expected)
    {
        Assert.Equal(expected, Read(("SONARQUBE_MCP_MAX_SOURCE_LINES", value)).MaxSourceLines);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("20001")]
    [InlineData("lots")]
    public void AnOutOfRangeMaxSourceLinesFallsBackToTwoThousand(string value)
    {
        Assert.Equal(2000, Read(("SONARQUBE_MCP_MAX_SOURCE_LINES", value)).MaxSourceLines);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("30", 30)]
    [InlineData("600", 600)]
    public void TheHttpTimeoutAcceptsItsRange(string value, int expected)
    {
        Assert.Equal(expected, Read(("SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS", value)).HttpTimeoutSeconds);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("601")]
    [InlineData("0")]
    [InlineData("soon")]
    public void AnOutOfRangeHttpTimeoutFallsBackToOneHundredSeconds(string value)
    {
        // The lower bound matters: a one-second timeout would fail every real call, and a server
        // that starts and then times out everything is harder to diagnose than one that ignores a
        // silly value.
        Assert.Equal(100, Read(("SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS", value)).HttpTimeoutSeconds);
    }

    [Fact]
    public void EveryVariableCanBeSetAtOnce()
    {
        var options = Read(
            ("SONARQUBE_TOKEN", "squ_abc"),
            ("SONARQUBE_ORG", "quartznet"),
            ("SONARQUBE_URL", "https://sonarqube.us/"),
            ("SONARQUBE_MCP_DEFAULT_PROJECT", "quartznet_quartznet"),
            ("SONARQUBE_MCP_READ_ONLY", "1"),
            ("SONARQUBE_MCP_LOG_LEVEL", "Debug"),
            ("SONARQUBE_MCP_MAX_PAGE_SIZE", "200"),
            ("SONARQUBE_MCP_DEFAULT_PAGE_SIZE", "25"),
            ("SONARQUBE_MCP_MAX_SOURCE_LINES", "500"),
            ("SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS", "30"));

        Assert.Equal("squ_abc", options.Token);
        Assert.Equal("quartznet", options.Organization);
        Assert.Equal(new Uri("https://sonarqube.us/"), options.BaseUrl);
        Assert.Equal("quartznet_quartznet", options.DefaultProject);
        Assert.True(options.ReadOnly);
        Assert.Equal(LogLevel.Debug, options.LogLevel);
        Assert.Equal(200, options.MaxPageSize);
        Assert.Equal(25, options.DefaultPageSize);
        Assert.Equal(500, options.MaxSourceLines);
        Assert.Equal(30, options.HttpTimeoutSeconds);
    }

    [Fact]
    public void AVariableSourceThatThrowsIsNotSomethingThisTypePromisesToSurvive()
    {
        // The contract is "a bad *value* never throws", not "a broken *source* never throws" — the
        // real source is Environment.GetEnvironmentVariable, which does not fail. Stated as a test
        // so the boundary is deliberate rather than assumed.
        Assert.Throws<InvalidOperationException>(
            () => SonarQubeMcpOptions.FromEnvironment(_ => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void ANullVariableSourceIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => SonarQubeMcpOptions.FromEnvironment(null!));
    }

    /// <summary>Builds a variable source from the pairs a test cares about; everything else is unset.</summary>
    private static SonarQubeMcpOptions Read(params (string Name, string Value)[] variables)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in variables)
        {
            map[name] = value;
        }

        return SonarQubeMcpOptions.FromEnvironment(name => map.TryGetValue(name, out var value) ? value : null);
    }
}
