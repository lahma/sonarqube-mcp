using System.Net;

using NUnit.Framework;

using SonarQube.Mcp.Cli;
using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Tests.Http;

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

    [Test]
    public async Task WithNoTokenTheCredentialProbeIsNotEvenAttempted()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = TestClient.CreateAnonymous(handler);
        var writer = new StringWriter();

        var exitCode = await StatusCommand.RunAsync(
            Options(token: null),
            writer,
            client,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(exitCode, Is.EqualTo(0));

        // The whole point: authentication/validate answers {"valid":true} anonymously, so calling it
        // here would print a reassurance that means nothing.
        Assert.That(handler.Requests, Is.Empty);

        var output = writer.ToString();

        Assert.That(output, Contains.Substring("No SONARQUBE_TOKEN set").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("Token:             not set").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("https://sonarcloud.io/account/security").Using(StringComparison.Ordinal));
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        Assert.That(exitCode, Is.EqualTo(0));

        Assert.That(handler.Requests, Has.Exactly(1).Items);
        var request = handler.Requests.Single();

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/api/authentication/validate"));

        var output = writer.ToString();

        Assert.That(output, Contains.Substring("Token:             set, from SONARQUBE_TOKEN").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("Token check:       accepted by https://sonarcloud.io").Using(StringComparison.Ordinal));

        // No organization configured, so the second probe is skipped and the reason is printed.
        Assert.That(output, Contains.Substring("Organization:      not set").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("SONARQUBE_ORG").Using(StringComparison.Ordinal));
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        var output = writer.ToString();

        Assert.That(output, Contains.Substring("Token check:       REJECTED").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("expired, revoked").Using(StringComparison.Ordinal));

        // A rejected token makes the organization probe pointless.
        Assert.That(handler.Requests, Has.Exactly(1).Items);
    }

    /// <summary>
    /// The one call that proves the token and the organization together, because
    /// <c>authentication/validate</c> proves neither on its own.
    /// </summary>
    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(2));

        var probe = handler.Requests[1];

        Assert.That(RequestUrl.Path(probe.Uri), Is.EqualTo("/api/components/search"));
        Assert.That(RequestUrl.QueryValue(probe.Uri, "organization"), Is.EqualTo(Organization));
        Assert.That(RequestUrl.QueryValue(probe.Uri, "ps"), Is.EqualTo("1"));

        Assert.That(
            writer.ToString(),
            Contains.Substring("Organization:      'quartznet' is readable - 1 project(s) visible.").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// A 401 on the organization probe is about the token; a 400 or 404 is about the organization.
    /// Distinguishing them in one command is the reason the second probe exists.
    /// </summary>
    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        var output = writer.ToString();

        Assert.That(output, Contains.Substring("the token was rejected (401)").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("not SONARQUBE_ORG").Using(StringComparison.Ordinal));
    }

    [TestCase(HttpStatusCode.NotFound, "404")]
    [TestCase(HttpStatusCode.BadRequest, "400")]
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
            TestContext.CurrentContext.CancellationToken);

        var output = writer.ToString();

        Assert.That(output, Contains.Substring($"'quartznet' was not found ({expected})").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("organization key").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("not its display name").Using(StringComparison.Ordinal));
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(
            writer.ToString(),
            Contains.Substring("Organization:      could not be read - SonarQube Cloud answered HTTP 500.").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// A status probe that fails the shell is a nuisance in exactly the scripts that would use one,
    /// so a dead network is a line of output rather than an exit code.
    /// </summary>
    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(writer.ToString(), Contains.Substring("Token check:       could not be completed").Using(StringComparison.Ordinal));
        Assert.That(writer.ToString(), Contains.Substring("no route to host").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// The configuration block is the half of the answer that needs no network, so it is printed
    /// before any probe and reports every knob the server reads.
    /// </summary>
    [Test]
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

        _ = await StatusCommand.RunAsync(options, writer, client, TestContext.CurrentContext.CancellationToken);

        var output = writer.ToString();

        Assert.That(output, Contains.Substring("Base URL:          https://sonarcloud.io").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("Organization:      (not set)").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("Default project:   (not set)").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("Read-only mode:    on - the five write tools are not registered").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring(ServerVersion.Name).Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring(ServerVersion.Value).Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// A rejected <c>SONARQUBE_URL</c> has already silently fallen back by the time anything can
    /// report it, so <c>status</c> is where it becomes visible.
    /// </summary>
    [Test]
    public async Task ARejectedBaseUrlIsReportedAlongsideTheOneThatWasUsedInstead()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = TestClient.CreateAnonymous(handler);
        var writer = new StringWriter();

        var options = SonarQubeMcpOptions.FromEnvironment(static name =>
            name == "SONARQUBE_URL" ? "https://evil.example.com" : null);

        _ = await StatusCommand.RunAsync(options, writer, client, TestContext.CurrentContext.CancellationToken);

        var output = writer.ToString();

        Assert.That(output, Contains.Substring("SONARQUBE_URL was set to 'https://evil.example.com'").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("it was ignored").Using(StringComparison.Ordinal));
        Assert.That(output, Contains.Substring("Base URL:          https://sonarcloud.io").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// "set" or "not set" is the entire useful content of the answer. Not a prefix, which narrows a
    /// brute-force search; not a length, which distinguishes token types.
    /// </summary>
    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        var output = writer.ToString();

        Assert.That(output, !Contains.Substring(Token).Using(StringComparison.Ordinal));
        Assert.That(output, !Contains.Substring(Token[..8]).Using(StringComparison.Ordinal));
        Assert.That(output, !Contains.Substring("squ_").Using(StringComparison.Ordinal));

        // Nor the length, which distinguishes a user token from an analysis token.
        Assert.That(output, !Contains.Substring(Token.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task StatusRefusesToRunWithoutTheThingsItReportsOn()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = TestClient.CreateAnonymous(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            StatusCommand.RunAsync(null!, new StringWriter(), client, TestContext.CurrentContext.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            StatusCommand.RunAsync(Options(), null!, client, TestContext.CurrentContext.CancellationToken));
    }

    private static SonarQubeMcpOptions Options(string? token = Token, string? organization = Organization) =>
        SonarQubeMcpOptions.FromEnvironment(name => name switch
        {
            "SONARQUBE_TOKEN" => token,
            "SONARQUBE_ORG" => organization,
            _ => null,
        });
}
