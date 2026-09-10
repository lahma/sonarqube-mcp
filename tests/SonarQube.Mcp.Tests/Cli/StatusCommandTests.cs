using System.Net;

using SonarQube.Mcp.Cli;
using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Tests.Http;

using Xunit;

namespace SonarQube.Mcp.Tests.Cli;

/// <summary>
/// <c>sonarqube-mcp status</c> — what the server would do if it started right now, and whether the
/// credential it would use actually works.
/// </summary>
/// <remarks>
/// <para>
/// Two properties carry most of the weight. The command never prints any part of the token — not a
/// prefix, not a length, not a masked form — because a prefix narrows a brute-force search and a
/// length distinguishes token types, and neither buys the user anything. And it never calls
/// <c>authentication/validate</c> without a token, because that endpoint answers
/// <c>{"valid":true}</c> to an anonymous request too (C4) and printing "valid" there would be a lie.
/// The second is asserted on the stub's request count, which is the only way to prove a call was
/// <em>not</em> made.
/// </para>
/// <para>
/// Everything runs through the <c>RunAsync(options, TextWriter, client, ct)</c> seam, so the
/// console is never touched and the output is a string a test can read.
/// </para>
/// </remarks>
public class StatusCommandTests
{
    private const string Organization = "quartznet";
    private const string Token = "squ_0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task WithNoTokenTheCredentialProbeIsNotEvenAttempted()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = TestClient.CreateAnonymous(handler);
        var writer = new StringWriter();

        var exitCode = await StatusCommand.RunAsync(
            Options(token: null),
            writer,
            client,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);

        // The whole point: authentication/validate answers {"valid":true} anonymously, so calling it
        // here would print a reassurance that means nothing.
        Assert.Empty(handler.Requests);

        var output = writer.ToString();

        Assert.Contains("No SONARQUBE_TOKEN set", output, StringComparison.Ordinal);
        Assert.Contains("Token:             not set", output, StringComparison.Ordinal);
        Assert.Contains("https://sonarcloud.io/account/security", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithATokenTheProbeIsMadeAgainstAuthenticationValidate()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"valid":true}""");

        using var client = TestClient.Create(handler);
        var writer = new StringWriter();

        var exitCode = await StatusCommand.RunAsync(
            Options(organization: null),
            writer,
            client,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);

        var request = Assert.Single(handler.Requests);

        Assert.Equal("/api/authentication/validate", RequestUrl.Path(request.Uri));

        var output = writer.ToString();

        Assert.Contains("Token:             set, from SONARQUBE_TOKEN", output, StringComparison.Ordinal);
        Assert.Contains("Token check:       accepted by https://sonarcloud.io", output, StringComparison.Ordinal);

        // No organization configured, so the second probe is skipped and the reason is printed.
        Assert.Contains("Organization:      not set", output, StringComparison.Ordinal);
        Assert.Contains("SONARQUBE_ORG", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedTokenIsReportedAsRejectedAndStopsThere()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"valid":false}""");

        using var client = TestClient.Create(handler);
        var writer = new StringWriter();

        _ = await StatusCommand.RunAsync(
            Options(),
            writer,
            client,
            TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Contains("Token check:       REJECTED", output, StringComparison.Ordinal);
        Assert.Contains("expired, revoked", output, StringComparison.Ordinal);

        // A rejected token makes the organization probe pointless.
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// The one call that proves the token and the organization together, because
    /// <c>authentication/validate</c> proves neither on its own.
    /// </summary>
    [Fact]
    public async Task WithATokenAndAnOrganizationOneProjectIsReadToProveBoth()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"valid":true}""");
        handler.EnqueueJson(SonarFixtures.Read("components-search.json"));

        using var client = TestClient.Create(handler);
        var writer = new StringWriter();

        _ = await StatusCommand.RunAsync(
            Options(),
            writer,
            client,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);

        var probe = handler.Requests[1];

        Assert.Equal("/api/components/search", RequestUrl.Path(probe.Uri));
        Assert.Equal(Organization, RequestUrl.QueryValue(probe.Uri, "organization"));
        Assert.Equal("1", RequestUrl.QueryValue(probe.Uri, "ps"));

        Assert.Contains(
            "Organization:      'quartznet' is readable - 1 project(s) visible.",
            writer.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A 401 on the organization probe is about the token; a 400 or 404 is about the organization.
    /// Distinguishing them in one command is the reason the second probe exists.
    /// </summary>
    [Fact]
    public async Task A401OnTheOrganizationProbeBlamesTheTokenRatherThanTheOrganization()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"valid":true}""");
        handler.EnqueueNoBody(HttpStatusCode.Unauthorized);

        using var client = TestClient.Create(handler);
        var writer = new StringWriter();

        _ = await StatusCommand.RunAsync(
            Options(),
            writer,
            client,
            TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Contains("the token was rejected (401)", output, StringComparison.Ordinal);
        Assert.Contains("not SONARQUBE_ORG", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.BadRequest, "400")]
    public async Task ARejectedOrganizationExplainsWhatTheKeyActuallyIs(HttpStatusCode status, string expected)
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"valid":true}""");
        handler.Enqueue(status, """{"errors":[{"msg":"No organization with key 'quartznet'"}]}""");

        using var client = TestClient.Create(handler);
        var writer = new StringWriter();

        _ = await StatusCommand.RunAsync(
            Options(),
            writer,
            client,
            TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Contains($"'quartznet' was not found ({expected})", output, StringComparison.Ordinal);
        Assert.Contains("organization key", output, StringComparison.Ordinal);
        Assert.Contains("not its display name", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerSideFailureOnTheOrganizationProbeIsSummarisedRatherThanThrown()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"valid":true}""");
        handler.Enqueue(HttpStatusCode.InternalServerError, "<html>oops</html>", "text/html");

        using var client = TestClient.Create(handler);
        var writer = new StringWriter();

        var exitCode = await StatusCommand.RunAsync(
            Options(),
            writer,
            client,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "Organization:      could not be read - SonarQube Cloud answered HTTP 500.",
            writer.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A status probe that fails the shell is a nuisance in exactly the scripts that would use one,
    /// so a dead network is a line of output rather than an exit code.
    /// </summary>
    [Fact]
    public async Task ANetworkFailureIsReportedWithoutFailingTheCommand()
    {
        using var handler = new StubHttpMessageHandler
        {
            Fallback = _ => throw new HttpRequestException("no route to host"),
        };

        using var client = TestClient.Create(handler, new ManualTimeProvider());
        var writer = new StringWriter();

        var exitCode = await StatusCommand.RunAsync(
            Options(),
            writer,
            client,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Token check:       could not be completed", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("no route to host", writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The configuration block is the half of the answer that needs no network, so it is printed
    /// before any probe and reports every knob the server reads.
    /// </summary>
    [Fact]
    public async Task TheConfigurationBlockReportsEveryKnobWithoutInventingAValue()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = TestClient.CreateAnonymous(handler);
        var writer = new StringWriter();

        var options = SonarQubeMcpOptions.FromEnvironment(static name => name switch
        {
            "SONARQUBE_MCP_READ_ONLY" => "true",
            _ => null,
        });

        _ = await StatusCommand.RunAsync(options, writer, client, TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Contains("Base URL:          https://sonarcloud.io", output, StringComparison.Ordinal);
        Assert.Contains("Organization:      (not set)", output, StringComparison.Ordinal);
        Assert.Contains("Default project:   (not set)", output, StringComparison.Ordinal);
        Assert.Contains("Read-only mode:    on - the five write tools are not registered", output, StringComparison.Ordinal);
        Assert.Contains(ServerVersion.Name, output, StringComparison.Ordinal);
        Assert.Contains(ServerVersion.Value, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejected <c>SONARQUBE_URL</c> has already silently fallen back by the time anything can
    /// report it, so <c>status</c> is where it becomes visible.
    /// </summary>
    [Fact]
    public async Task ARejectedBaseUrlIsReportedAlongsideTheOneThatWasUsedInstead()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = TestClient.CreateAnonymous(handler);
        var writer = new StringWriter();

        var options = SonarQubeMcpOptions.FromEnvironment(static name =>
            name == "SONARQUBE_URL" ? "https://evil.example.com" : null);

        _ = await StatusCommand.RunAsync(options, writer, client, TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Contains("SONARQUBE_URL was set to 'https://evil.example.com'", output, StringComparison.Ordinal);
        Assert.Contains("it was ignored", output, StringComparison.Ordinal);
        Assert.Contains("Base URL:          https://sonarcloud.io", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// "set" or "not set" is the entire useful content of the answer. Not a prefix, which narrows a
    /// brute-force search; not a length, which distinguishes token types.
    /// </summary>
    [Fact]
    public async Task NoLineOfOutputEverContainsAnyPartOfTheToken()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"valid":true}""");
        handler.EnqueueJson(SonarFixtures.Read("components-search.json"));

        using var client = TestClient.Create(handler);
        var writer = new StringWriter();

        _ = await StatusCommand.RunAsync(
            Options(),
            writer,
            client,
            TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.DoesNotContain(Token, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Token[..8], output, StringComparison.Ordinal);
        Assert.DoesNotContain("squ_", output, StringComparison.Ordinal);

        // Nor the length, which distinguishes a user token from an analysis token.
        Assert.DoesNotContain(Token.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusRefusesToRunWithoutTheThingsItReportsOn()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = TestClient.CreateAnonymous(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            StatusCommand.RunAsync(null!, new StringWriter(), client, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            StatusCommand.RunAsync(Options(), null!, client, TestContext.Current.CancellationToken));
    }

    private static SonarQubeMcpOptions Options(string? token = Token, string? organization = Organization) =>
        SonarQubeMcpOptions.FromEnvironment(name => name switch
        {
            "SONARQUBE_TOKEN" => token,
            "SONARQUBE_ORG" => organization,
            _ => null,
        });
}
