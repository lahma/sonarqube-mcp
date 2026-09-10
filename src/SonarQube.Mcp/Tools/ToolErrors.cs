using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

using SonarQube.Mcp.Authentication;
using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;

namespace SonarQube.Mcp.Tools;

/// <summary>
/// What the tool call was doing, so a failure can name the thing that failed.
/// </summary>
/// <param name="Tool">The tool's MCP name, for logs and for the two tool-specific error branches.</param>
/// <param name="Project">The resolved project key.</param>
/// <param name="Component">The resolved component key, when the call was scoped to one.</param>
/// <param name="EntityKey">The issue, hotspot or rule key the call addressed.</param>
internal readonly record struct ToolCallContext(
    string Tool,
    string? Project = null,
    string? Component = null,
    string? EntityKey = null);

/// <summary>
/// The single place an exception becomes something the model can act on.
/// </summary>
/// <remarks>
/// <para>
/// Every tool body runs inside <see cref="ExecuteAsync"/>, so <b>only <see cref="McpException"/> ever
/// escapes a tool method</b> — anything else would reach the client as the SDK's generic "An error
/// occurred" and throw away the one chance to tell the caller what to do differently.
/// </para>
/// <para>
/// The messages are written for a model mid-task, not for a bug report: each says what happened,
/// names the parameter or environment variable to change, and where a retry is plausible says which
/// call to make next. SonarQube's own 400 text is the one exception to "translate, do not quote" —
/// <c>'ps' value (501) must be less than 500</c> names the parameter, the value and the rule, and
/// paraphrasing it would lose all three.
/// </para>
/// </remarks>
internal static class ToolErrors
{
    /// <summary>
    /// The tools whose 404 is a <c>sources/*</c> 404, which means something different from every
    /// other 404 in this server: SonarQube answers 404 rather than 403 when the credential may not
    /// read a file's source, so "no such component" and "no permission" are indistinguishable.
    /// </summary>
    private const string SourcesTool = "getFileCoverage";

    /// <summary>The tool whose 400 is usually a transition that is not legal from the current state.</summary>
    private const string TransitionTool = "transitionIssue";

    /// <summary>SonarQube's own words when a request asks past the 10,000-result window.</summary>
    private const string ResultCapMarker = "only the first 10000 results";

    /// <summary>
    /// Where the unexpected-exception branch logs its stack trace. Assigned once from
    /// <c>McpServerSetup</c>; a null logger keeps the funnel usable from tests, where nothing has
    /// wired up logging.
    /// </summary>
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// The resolved configuration, for the two messages that depend on it: a 401 reads differently
    /// depending on whether a token was even sent, and the token-creation URL belongs to the
    /// configured host rather than to sonarcloud.io by assumption.
    /// </summary>
    /// <remarks>
    /// Defaults to "nothing is configured" — which <see cref="SonarQubeMcpOptions.FromEnvironment()"/>
    /// produces without touching the real environment — so the funnel works before
    /// <see cref="UseOptions"/> is called and in tests that never call it.
    /// </remarks>
    private static SonarQubeMcpOptions _options = SonarQubeMcpOptions.FromEnvironment(static _ => null);

    /// <summary>Points the funnel's diagnostic logging at the server's stderr logger.</summary>
    internal static void UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger(typeof(ToolErrors).Namespace!);
    }

    /// <summary>Tells the funnel which configuration the server is running with.</summary>
    internal static void UseOptions(SonarQubeMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Runs a tool body, converting anything it throws into an <see cref="McpException"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="OperationCanceledException"/> is rethrown untouched: the client cancelled, and the
    /// SDK's cancellation path — not an error result — is the correct answer. An
    /// <see cref="McpException"/> from argument validation is already the finished product and passes
    /// through unchanged.
    /// </remarks>
    internal static async Task<T> ExecuteAsync<T>(ToolCallContext context, Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ToMcpException(exception, context);
        }
    }

    /// <summary>Maps one exception to the error the caller sees.</summary>
    internal static McpException ToMcpException(Exception exception, ToolCallContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            AuthenticationRequiredException authentication => new McpException(
                authentication.Message, authentication),

            SonarApiException api => new McpException(Api(api, context), api),

            // An argument this server refused before it reached SonarQube — the request builder's
            // component-key guard, or a client-side paging limit. It is the caller's mistake, so it
            // reads like the other argument errors instead of like a bug.
            ArgumentException argument => new McpException(InvalidArgument(argument), argument),

            _ => Unexpected(exception, context),
        };
    }

    /// <summary>
    /// An argument rejected inside the server. The exception's own message already names the
    /// parameter (<see cref="ArgumentException"/> appends it), so the funnel only has to say that
    /// this is the caller's to fix rather than something to retry.
    /// </summary>
    private static string InvalidArgument(ArgumentException exception) =>
        "Invalid argument. " + exception.Message +
        "\nFix the named argument and call again; retrying unchanged will fail the same way.";

    // -----------------------------------------------------------------------------------------
    // SonarQube API failures
    // -----------------------------------------------------------------------------------------

    private static string Api(SonarApiException exception, ToolCallContext context)
    {
        var status = (int) exception.StatusCode;
        var detail = SonarApiException.Detail(exception);

        return status switch
        {
            400 => BadRequest(detail, context),
            401 => Unauthorized(detail),
            403 => Forbidden(detail, context),
            404 => NotFound(detail, context),
            429 => RateLimited(exception, detail),
            >= 300 and < 400 => Redirected(status, detail),
            >= 500 => ServerError(exception, status, detail),
            _ => Other(status, detail),
        };
    }

    /// <summary>
    /// A rejected request. SonarQube's own text is quoted verbatim because it is unusually good, and
    /// two cases get an extra sentence the API cannot give: the result window, and a transition that
    /// is not legal from the issue's current state.
    /// </summary>
    private static string BadRequest(string? detail, ToolCallContext context)
    {
        var message = new StringBuilder("SonarQube Cloud rejected the request as invalid (400).");
        Append(message, detail);

        if (detail is not null && detail.Contains(ResultCapMarker, StringComparison.OrdinalIgnoreCase))
        {
            message.Append("\nPaging deeper than the first 10000 results is not possible. Narrow the search ")
                .Append("instead: scope it with component, add issueStatuses or impactSeverities, or set ")
                .Append("createdAfter/createdInLast, then start again from page 1.");
        }

        if (string.Equals(context.Tool, TransitionTool, StringComparison.Ordinal)
            && detail is not null
            && detail.Contains("transition", StringComparison.OrdinalIgnoreCase))
        {
            message.Append("\nCall getIssue and use availableTransitions — a transition is only legal from ")
                .Append("certain states, so this usually means the issue is not in the state it was assumed ")
                .Append("to be in.");
        }

        message.Append("\nFix the named argument and call again; retrying unchanged will fail the same way.");
        return message.ToString();
    }

    /// <summary>
    /// Rejected credentials.
    /// </summary>
    /// <remarks>
    /// The detail is optional on purpose: a 401 caused by an <em>invalid</em> bearer token arrives
    /// with <c>Content-Length: 0</c> and no body at all (verified live), so a composer that assumed
    /// an error envelope would fail on the most likely 401 there is.
    /// </remarks>
    private static string Unauthorized(string? detail)
    {
        // No token configured at all is a different story with different advice, and the client
        // normally raises it as AuthenticationRequiredException before this branch is reached. It is
        // repeated here because a 401 from any other path must not send a user hunting for a token
        // they never set.
        if (_options.Token is null)
        {
            return AuthenticationRequiredException.BuildMessage(_options.BaseUrlText);
        }

        var message = new StringBuilder("SonarQube Cloud rejected the credentials (401 Unauthorized).");
        Append(message, detail);

        message
            .Append("\nThe token in SONARQUBE_TOKEN is expired, revoked, or belongs to a different SonarQube ")
            .Append("instance than SONARQUBE_URL (currently ").Append(_options.BaseUrlText).Append("). Create a ")
            .Append("new User Token at ").Append(_options.BaseUrlText).Append("/account/security, replace the ")
            .Append("variable in the environment the MCP client launches this server with, and restart the ")
            .Append("server. `sonarqube-mcp status` reports what this server currently sees.");

        return message.ToString();
    }

    private static string Forbidden(string? detail, ToolCallContext context)
    {
        var message = new StringBuilder("SonarQube Cloud refused this operation (403 Forbidden).");
        Append(message, detail);

        message
            .Append("\nThe token is valid but the account lacks permission on ").Append(Target(context))
            .Append(". Browsing a private project needs 'Browse'; transitioning, assigning or commenting on an ")
            .Append("issue needs 'Administer Issues'; changing a hotspot's status needs 'Administer Security ")
            .Append("Hotspots'. getHotspot reports canChangeStatus before you try. Ask a project administrator, ")
            .Append("or use a token from an account that already has the permission.");

        return message.ToString();
    }

    /// <summary>
    /// Says that the request went out without a credential, when it did.
    /// </summary>
    /// <remarks>
    /// This is the difference between a 404 that means "wrong key" and one that means "you cannot
    /// see it". SonarQube reports a private project to an anonymous caller as
    /// <c>Project doesn't exist</c>, which sends a reader to <c>listProjects</c> — where the answer
    /// is an empty list, for the same reason, confirming the wrong hypothesis. Naming the missing
    /// token at the point of failure is the only thing that breaks that loop.
    /// </remarks>
    /// <param name="message">The message being composed.</param>
    private static void AppendAnonymousWarning(StringBuilder message)
    {
        if (_options.Token is not null)
        {
            return;
        }

        message
            .Append("\nNote that no SONARQUBE_TOKEN is configured, so this request went out anonymously and ")
            .Append("could only see public projects — a private project is indistinguishable from a missing ")
            .Append("one in that state, and listProjects will be empty for the same reason rather than as ")
            .Append("confirmation. `sonarqube-mcp status` reports what the server is actually reading.");
    }

    private static string NotFound(string? detail, ToolCallContext context)
    {
        if (string.Equals(context.Tool, SourcesTool, StringComparison.Ordinal))
        {
            var sources = new StringBuilder("SonarQube Cloud has no such source (404).");
            Append(sources, detail);

            sources
                .Append("\nSonarQube answers 404 — not 403 — both for a component that does not exist and for ")
                .Append("one this token may not read, and api/sources/* additionally requires the 'See Source ")
                .Append("Code' permission. So this means either 'wrong component key' or 'the file is there ")
                .Append("and the token cannot read it'. Confirm the key with listComponents (it is ")
                .Append("projectKey:path/from/the/project/root); if the key is right, the permission is the ")
                .Append("problem.");

            return sources.ToString();
        }

        var message = new StringBuilder("SonarQube Cloud has no such resource (404).");
        Append(message, detail);

        message
            .Append("\nThis call looked for ").Append(Target(context))
            .Append(". The project key is the `id` in a sonarcloud.io project URL, not the repository name — ")
            .Append("confirm it with listProjects. A branch or pull request that has never been analysed is ")
            .Append("also a 404; listBranches and listPullRequests say which ones exist.");

        AppendAnonymousWarning(message);

        return message.ToString();
    }

    /// <summary>
    /// Throttling. SonarQube Cloud publishes no rate limits and sends no <c>X-RateLimit-*</c>
    /// headers, so there is no number to read and the advice has to be about the request rate rather
    /// than about a quota.
    /// </summary>
    private static string RateLimited(SonarApiException exception, string? detail)
    {
        var message = new StringBuilder("SonarQube Cloud rate-limited this request (429 Too Many Requests).");
        Append(message, detail);

        if (exception.RetryAttempts > 0)
        {
            message.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"\nIt was already retried {exception.RetryAttempts} time(s) with exponential backoff and was still throttled."));
        }

        if (exception.RetryAfterSeconds is { } seconds && seconds > 0)
        {
            message.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"\nSonarQube's Retry-After asks for another ~{seconds}s."));
        }
        else
        {
            message
                .Append("\nSonarQube Cloud publishes no rate limits and sends no X-RateLimit-* headers, so ")
                .Append("there is no number to read — wait about a minute before calling again.");
        }

        message
            .Append("\nCut the request rate as well: a smaller pageSize, fewer metricKeys, and a narrower ")
            .Append("searchIssues filter instead of paging through everything.");

        return message.ToString();
    }

    /// <summary>
    /// A redirect. Nothing follows one — the transport has <c>AllowAutoRedirect</c> off — so this is
    /// an error rather than a hop. The client already composed a synthetic detail naming the
    /// <c>Location</c>, which is the only place that header survives.
    /// </summary>
    private static string Redirected(int status, string? detail)
    {
        if (detail is not null)
        {
            return detail;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"SonarQube Cloud redirected this request (HTTP {status}) to a location this server does not " +
            $"follow — the API is not expected to redirect. Check SONARQUBE_URL.");
    }

    private static string ServerError(SonarApiException exception, int status, string? detail)
    {
        var message = new StringBuilder();
        message.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"SonarQube Cloud failed on its own side (HTTP {status})."));
        Append(message, detail);

        message.Append(exception.RetryAttempts > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"\nThe request was already retried {exception.RetryAttempts} time(s) with backoff, so this is not a single unlucky call.")
            : "\nThis status is retried automatically when it is transient, so it arrived on the first attempt.");

        message
            .Append("\nNothing in the request needs changing. Try again in a few minutes, and check ")
            .Append("https://status.sonarcloud.io/ if it persists.");

        return message.ToString();
    }

    private static string Other(int status, string? detail)
    {
        var message = new StringBuilder();
        message.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"SonarQube Cloud returned an unexpected HTTP {status}."));
        Append(message, detail);
        return message.ToString();
    }

    private static void Append(StringBuilder message, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail))
        {
            message.Append("\nSonarQube said: ").Append(detail);
        }
    }

    /// <summary>Names the thing the call was addressing, for the 403 and 404 messages.</summary>
    private static string Target(ToolCallContext context)
    {
        var project = string.IsNullOrWhiteSpace(context.Project) ? "?" : context.Project;

        if (!string.IsNullOrWhiteSpace(context.EntityKey))
        {
            return $"{context.EntityKey} in project {project}";
        }

        return string.IsNullOrWhiteSpace(context.Component)
            ? $"project {project}"
            : $"component {context.Component} in project {project}";
    }

    // -----------------------------------------------------------------------------------------
    // Everything else
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The catch-all. The type name goes in the message because it is the one detail that makes an
    /// unanticipated failure reportable; the stack trace goes to stderr at Debug, where it can be
    /// turned on with <c>SONARQUBE_MCP_LOG_LEVEL=Debug</c> without polluting the model's context.
    /// </summary>
    private static McpException Unexpected(Exception exception, ToolCallContext context)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                exception,
                "Tool {Tool} failed unexpectedly for project {Project}, component {Component}, key {EntityKey}",
                context.Tool,
                context.Project,
                context.Component,
                context.EntityKey);
        }

        return new McpException(
            $"Unexpected error: {exception.GetType().Name}: {exception.Message}", exception);
    }
}
