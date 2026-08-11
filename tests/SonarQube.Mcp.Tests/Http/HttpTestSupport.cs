using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using SonarQube.Mcp.Authentication;
using SonarQube.Mcp.Configuration;
using SonarQube.Mcp.Http;

using Xunit;

namespace SonarQube.Mcp.Tests.Http;

/// <summary>
/// Reads the golden SonarQube Cloud response fixtures embedded from <c>Fixtures/</c>.
/// </summary>
/// <remarks>
/// Resources are matched on the file-name suffix rather than the full manifest name, so the tests
/// do not encode MSBuild's resource-naming rules. <see cref="AllJson"/> enumerates them instead of
/// listing them by hand, which is what makes "every fixture deserialises" a claim about the
/// directory rather than about whichever files somebody remembered to add.
/// </remarks>
internal static class SonarFixtures
{
    /// <summary>Returns the fixture's text exactly as it is stored, newlines included.</summary>
    /// <param name="fileName">The file name under <c>Fixtures/</c>, for example <c>issues-search-page.json</c>.</param>
    internal static string Read(string fileName)
    {
        var assembly = typeof(SonarFixtures).Assembly;
        var suffix = "." + fileName;

        var name =
            Array.Find(assembly.GetManifestResourceNames(), candidate => candidate.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"No embedded fixture ends with '{suffix}'. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded fixture '{name}' could not be opened.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Every embedded <c>.json</c> fixture, as (file name, content). <c>MANIFEST.md</c> and
    /// <c>error-401-empty-body.txt</c> are excluded by the extension filter — neither is a response
    /// body, and one of them documents a response that has none.
    /// </summary>
    internal static IEnumerable<(string Name, string Json)> AllJson()
    {
        var assembly = typeof(SonarFixtures).Assembly;

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // The manifest name is "<assembly>.Fixtures.<file>"; the last two dot-separated pieces
            // are the file name.
            var parts = resource.Split('.');
            var name = string.Join('.', parts[^2..]);

            yield return (name, reader.ReadToEnd());
        }
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test moves it and whose timers fire
/// immediately, recording the delay that was asked for.
/// </summary>
/// <remarks>
/// <para>
/// This is how the retry schedule is asserted without spending real seconds: <c>Task.Delay(delay,
/// timeProvider, ct)</c> creates a timer through <see cref="CreateTimer"/>, so the requested
/// <c>dueTime</c> lands in <see cref="Delays"/> and the wait completes on the next thread-pool
/// turn. A <see cref="TimeSpan.Zero"/> delay never reaches here — <c>Task.Delay</c> short-circuits
/// it — which is itself observable as an empty <see cref="Delays"/> list.
/// </para>
/// <para>
/// No package provides this (AGENTS.md: no mocking libraries), and the delays are the point of the
/// retry tests, so it is ~40 lines of hand-rolled clock.
/// </para>
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _delays = [];
    private DateTimeOffset _utcNow = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every delay a timer was created for, in order.</summary>
    internal IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return [.. _delays];
            }
        }
    }

    /// <summary>Moves the clock, which is what an HTTP-date <c>Retry-After</c> is measured against.</summary>
    internal void SetUtcNow(DateTimeOffset value)
    {
        lock (_gate)
        {
            _utcNow = value;
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_gate)
        {
            _delays.Add(dueTime);
        }

        var timer = new ImmediateTimer(callback, state);

        // Queued rather than invoked inline: Task.Delay assigns its timer field after CreateTimer
        // returns, and firing on another thread keeps the test out of that ordering entirely.
        ThreadPool.UnsafeQueueUserWorkItem(static pending => pending.Fire(), timer, preferLocal: false);

        return timer;
    }

    private sealed class ImmediateTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private int _fired;

        internal ImmediateTimer(TimerCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
        }

        internal void Fire()
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                _callback(_state);
            }
        }

        /// <summary>Never rescheduled by <c>Task.Delay</c>; accepted so the contract is complete.</summary>
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        /// <summary>
        /// Deliberately a no-op: the pending callback must still run, or an awaited
        /// <c>Task.Delay</c> would never complete and the test would hang instead of fail.
        /// </summary>
        public void Dispose()
        {
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Reads a recorded request URL back apart.
/// </summary>
/// <remarks>
/// Assertions go through <see cref="Uri.AbsolutePath"/> and <see cref="Uri.Query"/> and never
/// through <see cref="Uri.ToString()"/>, which unescapes and would report a URL that was never
/// sent — in particular hiding whether a hostile component key was escaped.
/// </remarks>
internal static class RequestUrl
{
    /// <summary>The escaped path, exactly as it went on the wire.</summary>
    internal static string Path(Uri? uri) =>
        (uri ?? throw new InvalidOperationException("The recorded request had no URI.")).AbsolutePath;

    /// <summary>The escaped path and query, exactly as they went on the wire.</summary>
    internal static string PathAndQuery(Uri? uri) =>
        (uri ?? throw new InvalidOperationException("The recorded request had no URI.")).PathAndQuery;

    /// <summary>Query parameters in wire order, names and values unescaped, duplicates preserved.</summary>
    internal static List<KeyValuePair<string, string>> Query(Uri? uri)
    {
        var query = (uri ?? throw new InvalidOperationException("The recorded request had no URI.")).Query;
        var parameters = new List<KeyValuePair<string, string>>();

        if (query.Length <= 1)
        {
            return parameters;
        }

        foreach (var pair in query[1..].Split('&'))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            parameters.Add(separator < 0
                ? new KeyValuePair<string, string>(Uri.UnescapeDataString(pair), string.Empty)
                : new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(pair[..separator]),
                    Uri.UnescapeDataString(pair[(separator + 1)..])));
        }

        return parameters;
    }

    /// <summary>The single value of a query parameter, or <see langword="null"/> when it is absent.</summary>
    internal static string? QueryValue(Uri? uri, string name)
    {
        string? found = null;

        foreach (var parameter in Query(uri))
        {
            if (string.Equals(parameter.Key, name, StringComparison.Ordinal))
            {
                Assert.Null(found);
                found = parameter.Value;
            }
        }

        return found;
    }

    /// <summary>The query parameter names in wire order.</summary>
    internal static List<string> QueryNames(Uri? uri)
    {
        var names = new List<string>();

        foreach (var parameter in Query(uri))
        {
            names.Add(parameter.Key);
        }

        return names;
    }
}

/// <summary>
/// Builds a <see cref="SonarApiClient"/> over a stub transport, with the options and credential
/// every test would otherwise repeat.
/// </summary>
internal static class TestClient
{
    /// <summary>The base address every test resolves relative URLs against. Not the real host.</summary>
    internal static readonly Uri BaseAddress = new("https://sonarcloud.io/");

    /// <summary>A client with a token configured, so requests carry <c>Authorization</c>.</summary>
    /// <param name="transport">The stub.</param>
    /// <param name="timeProvider">A clock, when the test asserts retry delays.</param>
    /// <param name="options">Options overriding the defaults.</param>
    internal static SonarApiClient Create(
        HttpMessageHandler transport,
        TimeProvider? timeProvider = null,
        SonarQubeMcpOptions? options = null)
    {
        var resolved = options ?? SonarQubeMcpOptions.FromEnvironment(static name =>
            name == "SONARQUBE_TOKEN" ? "test-token" : null);

        return new SonarApiClient(
            resolved,
            new StaticTokenCredential(resolved),
            NullLoggerFactory.Instance,
            transport,
            BaseAddress,
            timeProvider);
    }

    /// <summary>
    /// A client with no token, which is a supported configuration rather than a broken one — public
    /// projects are readable anonymously and every golden fixture was captured that way.
    /// </summary>
    internal static SonarApiClient CreateAnonymous(HttpMessageHandler transport, TimeProvider? timeProvider = null) =>
        Create(transport, timeProvider, SonarQubeMcpOptions.FromEnvironment(static _ => null));
}
