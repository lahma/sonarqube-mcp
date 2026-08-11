using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;
using SonarQube.Mcp.Tests.Http;
using SonarQube.Mcp.Tools;

using Xunit;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// What a failing tool call tells the model — one test per row of the error-funnel table in AGENTS.md.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="McpException"/> may escape a tool method; anything else reaches the client as the
/// SDK's generic "An error occurred", which throws away the single chance to tell a model mid-task
/// what to do differently. These tests assert the funnel's translations rather than its prose: each
/// looks for the actionable substring — the next call to make, the parameter to change, the
/// environment variable to set.
/// </para>
/// <para>
/// <b>Static state.</b> <see cref="ToolErrors.UseOptions"/> and
/// <see cref="ToolErrors.UseLoggerFactory"/> are process-wide, because the funnel is reached from
/// nineteen tools that must not each carry a parameter the schema would then have to exclude. Every
/// test that changes them restores the defaults in a <c>finally</c>, and the class restores them
/// again on dispose, so a failure part-way through cannot leak a token-bearing configuration into
/// another test.
/// </para>
/// </remarks>
public sealed class ToolErrorMappingTests : IDisposable
{
    private const string IssueKey = "AZ_xePOumT_q4T_1FWf8";
    private const string FileKey = ToolTestHost.FileComponent;

    /// <summary>What SonarQube answers when a transition is not legal from the current state.</summary>
    private const string TransitionRefused =
        """{"errors":[{"msg":"Transition action 'resolve' does not exist for issue"}]}""";

    /// <inheritdoc />
    public void Dispose() => ResetFunnel();

    // ---------------------------------------------------------------------------------------
    // 401 — the two stories one status code tells
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// No token configured. This is the one failure a status-code table cannot explain, so the
    /// client raises it as its own exception and the message is the setup instruction.
    /// </summary>
    [Fact]
    public async Task WithNoTokenA401ExplainsHowToCreateOne()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, SonarFixtures.Read("error-401-authentication-required.json"));

        using var client = ToolTestHost.CreateAnonymousClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListProjectsAsync(
                client,
                ToolTestHost.CreateOptions(token: null),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("SONARQUBE_TOKEN", exception.Message, StringComparison.Ordinal);
        Assert.Contains("/account/security", exception.Message, StringComparison.Ordinal);
        Assert.Contains("User Token", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token that <em>is</em> configured and is rejected: verified live, this 401 arrives with
    /// <c>Content-Length: 0</c> and no body at all — so a composer that assumed an error envelope
    /// would throw a <see cref="NullReferenceException"/> on the most likely 401 there is.
    /// </summary>
    [Fact]
    public async Task WithATokenA401WithNoBodyAtAllSaysTheCredentialsWereRejected()
    {
        var options = ToolTestHost.CreateOptions();

        try
        {
            ToolErrors.UseOptions(options);

            using var handler = new StubHttpMessageHandler();
            handler.EnqueueNoBody(HttpStatusCode.Unauthorized);

            using var client = ToolTestHost.CreateClient(handler);

            var exception = await Assert.ThrowsAsync<McpException>(() =>
                ProjectReadTools.ListProjectsAsync(
                    client,
                    options,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("rejected the credentials (401 Unauthorized)", exception.Message, StringComparison.Ordinal);
            Assert.Contains("expired, revoked", exception.Message, StringComparison.Ordinal);
            Assert.Contains("https://sonarcloud.io/account/security", exception.Message, StringComparison.Ordinal);

            // Nothing was quoted, because there was nothing to quote.
            Assert.DoesNotContain("SonarQube said:", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            ResetFunnel();
        }
    }

    /// <summary>
    /// The default, token-less funnel configuration must not send a user hunting for a token they
    /// never set — even when the 401 arrives from a client that does have one.
    /// </summary>
    [Fact]
    public void WithNoConfiguredTokenTheFunnelTellsTheSetupStoryInstead()
    {
        ResetFunnel();

        var exception = ToolErrors.ToMcpException(
            new SonarApiException(HttpStatusCode.Unauthorized, error: null, rawBody: string.Empty, retryAttempts: 0),
            new ToolCallContext("listProjects"));

        Assert.Contains("No SonarQube token is configured", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // 400 — quoted verbatim, with two special cases
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// SonarQube's own 400 text names the parameter, the value and the rule, so it is quoted rather
    /// than paraphrased — paraphrasing would lose all three.
    /// </summary>
    [Fact]
    public async Task ABadRequestQuotesSonarQubesOwnWords()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, SonarFixtures.Read("error-400-page-size.json"));

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListProjectsAsync(
                client,
                ToolTestHost.CreateOptions(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("rejected the request as invalid (400)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'ps' value (501) must be less than 500", exception.Message, StringComparison.Ordinal);
        Assert.Contains("retrying unchanged will fail the same way", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The result-window 400 gets the one sentence the API cannot give: there is no page to ask for,
    /// so the answer is to narrow the filter rather than to page on.
    /// </summary>
    [Fact]
    public async Task TheResultWindowBadRequestAppendsTheAdviceToNarrowTheSearch()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, SonarFixtures.Read("error-400-result-cap.json"));

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("10100th result asked", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Paging deeper than the first 10000 results is not possible", exception.Message, StringComparison.Ordinal);
        Assert.Contains("createdAfter/createdInLast", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejected transition almost always means the issue is not in the state the model believed,
    /// so the advice is the call that answers that question.
    /// </summary>
    [Fact]
    public async Task ARejectedTransitionPointsAtAvailableTransitions()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, TransitionRefused);

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.TransitionIssueAsync(
                client,
                ToolTestHost.CreateOptions(),
                IssueKey,
                "resolve",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Transition action 'resolve' does not exist", exception.Message, StringComparison.Ordinal);
        Assert.Contains("availableTransitions", exception.Message, StringComparison.Ordinal);
        Assert.Contains("getIssue", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same words from a different tool must not get the transition advice: the branch is keyed
    /// on the tool, and advice about <c>getIssue</c> in front of a search failure is noise.
    /// </summary>
    [Fact]
    public async Task TheTransitionAdviceIsOnlyGivenToTransitionIssue()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, TransitionRefused);

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Transition action 'resolve' does not exist", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("availableTransitions", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // 403, 404
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Three permissions, because which one is missing depends on what was attempted and the caller
    /// has to know which to ask an administrator for.
    /// </summary>
    [Fact]
    public async Task ForbiddenNamesTheThreePermissionsThatMatter()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"errors":[{"msg":"Insufficient privileges"}]}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueWriteTools.AddIssueCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                IssueKey,
                "please look at this",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("refused this operation (403 Forbidden)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Insufficient privileges", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'Browse'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'Administer Issues'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'Administer Security Hotspots'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"issue {IssueKey}", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>sources/*</c> answers 404 — not 403 — when the token may not read a file, so "wrong key"
    /// and "no permission" are indistinguishable from the status alone and the message has to say so.
    /// </summary>
    [Fact]
    public async Task ASourcesNotFoundExplainsThatItMayBeAPermissionRatherThanAKey()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, SonarFixtures.Read("error-404-component-not-found.json"));

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetFileCoverageAsync(
                client,
                ToolTestHost.CreateOptions(),
                FileKey,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("has no such source (404)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("See Source Code", exception.Message, StringComparison.Ordinal);
        Assert.Contains("listComponents", exception.Message, StringComparison.Ordinal);
        Assert.Contains("projectKey:path/from/the/project/root", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every other 404 is a key problem, and the three calls that answer "which keys exist" are the
    /// advice.
    /// </summary>
    [Fact]
    public async Task AnyOtherNotFoundPointsAtTheThreeListingTools()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"errors":[{"msg":"Project 'nope' not found"}]}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListBranchesAsync(
                client,
                ToolTestHost.CreateOptions(),
                projectKey: "nope",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("has no such resource (404)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Project 'nope' not found", exception.Message, StringComparison.Ordinal);
        Assert.Contains("project nope", exception.Message, StringComparison.Ordinal);
        Assert.Contains("listProjects", exception.Message, StringComparison.Ordinal);
        Assert.Contains("listBranches and listPullRequests", exception.Message, StringComparison.Ordinal);

        // The sources-specific advice belongs to getFileCoverage alone.
        Assert.DoesNotContain("See Source Code", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A 404 on a component-scoped call names the component, not just the project.</summary>
    [Fact]
    public async Task ANotFoundOnAComponentScopedCallNamesTheComponent()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"errors":[{"msg":"Component not found"}]}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetComponentMeasuresAsync(
                client,
                ToolTestHost.CreateOptions(),
                ["ncloc"],
                component: "src/Missing.cs",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("component quartznet_quartznet:src/Missing.cs in project quartznet_quartznet",
            exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // 429, 5xx, 3xx and the long tail
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// SonarQube Cloud publishes no rate limits and sends no <c>X-RateLimit-*</c> headers, so there
    /// is no number to read — the message says that, and says how many retries were already spent.
    /// </summary>
    [Fact]
    public async Task RateLimitingReportsTheRetriesAlreadySpentAndThatThereIsNoPublishedLimit()
    {
        using var handler = new StubHttpMessageHandler();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            handler.Enqueue(HttpStatusCode.TooManyRequests, """{"errors":[{"msg":"Too many requests"}]}""");
        }

        var time = new ManualTimeProvider();
        using var client = ToolTestHost.CreateClient(handler, time);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("rate-limited this request (429 Too Many Requests)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("retried 3 time(s)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("publishes no rate limits", exception.Message, StringComparison.Ordinal);
        Assert.Contains("smaller pageSize", exception.Message, StringComparison.Ordinal);

        // The backoff was waited for on the fake clock, so the test spent no real time.
        Assert.Equal(3, time.Delays.Count);
        Assert.Equal(4, handler.Requests.Count);
    }

    /// <summary>A <c>Retry-After</c> is quoted in preference to the guess.</summary>
    [Fact]
    public async Task ARetryAfterHeaderIsQuotedInsteadOfTheGuess()
    {
        using var handler = new StubHttpMessageHandler();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            handler.Enqueue(_ =>
            {
                var response = StubHttpMessageHandler.CreateResponse(HttpStatusCode.TooManyRequests, "{}");
                response.Headers.Add("Retry-After", "30");
                return response;
            });
        }

        var time = new ManualTimeProvider();
        using var client = ToolTestHost.CreateClient(handler, time);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Retry-After asks for another ~30s", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("publishes no rate limits", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing in the request needs changing, so the advice is where to look rather than what to fix.
    /// </summary>
    [Fact]
    public async Task AServerSideFailurePointsAtTheStatusPage()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "<html>500</html>", "text/html");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListProjectsAsync(
                client,
                ToolTestHost.CreateOptions(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("failed on its own side (HTTP 500)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing in the request needs changing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("https://status.sonarcloud.io/", exception.Message, StringComparison.Ordinal);

        // 500 is not in the retry set, so it arrived on the first attempt and says so.
        Assert.Contains("arrived on the first attempt", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// Nothing follows a redirect — the transport would strip the Authorization header — so a 3xx is
    /// an error that names the <c>Location</c>, which is the only place that header survives.
    /// </summary>
    [Fact]
    public async Task ARedirectIsAnErrorThatNamesWhereItWasSent()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(
            HttpStatusCode.Found, "https://sonarcloud.io/sessions/new"));

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListProjectsAsync(
                client,
                ToolTestHost.CreateOptions(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("https://sonarcloud.io/sessions/new", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not follow", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SONARQUBE_URL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStatusWithNoRowOfItsOwnIsStillReportedWithItsNumber()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue((HttpStatusCode) 418, """{"errors":[{"msg":"I'm a teapot"}]}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListProjectsAsync(
                client,
                ToolTestHost.CreateOptions(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("unexpected HTTP 418", exception.Message, StringComparison.Ordinal);
        Assert.Contains("I'm a teapot", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // The non-HTTP branches
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// An argument this server refused before it reached SonarQube reads like the other argument
    /// errors rather than like a bug, and it costs no round trip.
    /// </summary>
    [Fact]
    public async Task AnArgumentRejectedInsideTheServerIsReportedAsTheCallersToFix()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            MeasureReadTools.GetComponentMeasuresAsync(
                client,
                ToolTestHost.CreateOptions(),
                ["ncloc"],
                component: "src/../../etc/passwd",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.StartsWith("Invalid argument.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("listComponents", exception.Message, StringComparison.Ordinal);
        Assert.Contains("retrying unchanged will fail the same way", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// A list value containing a comma would be split into two filters the caller never asked for,
    /// so it is refused with the fix.
    /// </summary>
    [Fact]
    public async Task AListValueContainingACommaIsRefusedRatherThanSilentlySplit()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            IssueReadTools.SearchIssuesAsync(
                client,
                ToolTestHost.CreateOptions(),
                tags: ["cwe,performance"],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.StartsWith("Invalid argument.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("separate list elements", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>A success status with a body that does not parse is still a failure the caller can read.</summary>
    [Fact]
    public async Task AnUnparsableSuccessBodyBecomesAnErrorRatherThanAnEmptyResult()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "<html>maintenance</html>", "text/html");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ProjectReadTools.ListProjectsAsync(
                client,
                ToolTestHost.CreateOptions(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("could not parse", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The client cancelled: the SDK's cancellation path, not an error result, is the correct answer,
    /// so the exception passes through untouched.
    /// </summary>
    [Fact]
    public async Task CancellationIsRethrownRatherThanTranslated()
    {
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ToolErrors.ExecuteAsync<string>(
                new ToolCallContext("listProjects"),
                () => throw new OperationCanceledException()));
    }

    /// <summary>Argument validation has already produced the finished product; the funnel leaves it alone.</summary>
    [Fact]
    public async Task AnMcpExceptionFromValidationPassesThroughUnchanged()
    {
        var original = new McpException("already actionable");

        var thrown = await Assert.ThrowsAsync<McpException>(() =>
            ToolErrors.ExecuteAsync<string>(new ToolCallContext("listProjects"), () => throw original));

        Assert.Same(original, thrown);
    }

    /// <summary>
    /// The catch-all names the exception type, because that is the one detail that makes an
    /// unanticipated failure reportable.
    /// </summary>
    [Fact]
    public async Task AnUnanticipatedFailureIsNamedByItsType()
    {
        var exception = await Assert.ThrowsAsync<McpException>(() =>
            ToolErrors.ExecuteAsync<string>(
                new ToolCallContext("listProjects"),
                () => throw new InvalidTimeZoneException("the clock exploded")));

        Assert.Contains("Unexpected error: InvalidTimeZoneException", exception.Message, StringComparison.Ordinal);
        Assert.Contains("the clock exploded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFunnelRefusesToBeConfiguredWithNothing()
    {
        Assert.Throws<ArgumentNullException>(() => ToolErrors.UseOptions(null!));
        Assert.Throws<ArgumentNullException>(() => ToolErrors.UseLoggerFactory(null!));
    }

    /// <summary>
    /// The diagnostic logger is a side channel: pointing it somewhere must not change what the
    /// caller is told.
    /// </summary>
    [Fact]
    public async Task PointingTheFunnelAtALoggerDoesNotChangeTheMessage()
    {
        try
        {
            ToolErrors.UseLoggerFactory(NullLoggerFactory.Instance);

            var exception = await Assert.ThrowsAsync<McpException>(() =>
                ToolErrors.ExecuteAsync<string>(
                    new ToolCallContext("listProjects", "quartznet_quartznet"),
                    () => throw new InvalidOperationException("boom")));

            Assert.Contains("Unexpected error: InvalidOperationException: boom", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            ResetFunnel();
        }
    }

    /// <summary>
    /// Restores the process-wide funnel configuration: no options (which is what
    /// <c>FromEnvironment</c> produces with nothing set) and the null logger.
    /// </summary>
    private static void ResetFunnel()
    {
        ToolErrors.UseOptions(SonarQubeMcpOptions.FromEnvironment(static _ => null));
        ToolErrors.UseLoggerFactory(NullLoggerFactory.Instance);
    }
}
