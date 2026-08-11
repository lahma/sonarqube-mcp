using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace SonarQube.Mcp.Tests;

/// <summary>
/// Hand-rolled <see cref="HttpMessageHandler"/> stub (AGENTS.md: no mocking libraries).
/// Responses are served from a FIFO queue of responders, falling back to
/// <see cref="Fallback"/>; every request is recorded with its body captured eagerly,
/// because <see cref="HttpClient"/> disposes request content after the send completes.
/// </summary>
/// <remarks>
/// It replaces the innermost handler, so <c>RetryHandler</c> and <c>AuthenticationHandler</c> are
/// both exercised by every test that goes through it — a queued 429 followed by a queued 200 runs
/// the real retry path, and each recorded request carries the <c>Authorization</c> header the real
/// handler attached (or, when no token is configured, does not).
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();

    /// <summary>Responder used when the queue is empty; null means an unexpected request throws.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responders.Enqueue(responder);

    public void Enqueue(HttpStatusCode statusCode, string? body = null, string mediaType = "application/json")
        => Enqueue(_ => CreateResponse(statusCode, body, mediaType));

    public void EnqueueJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => Enqueue(_ => CreateResponse(statusCode, json, "application/json"));

    /// <summary>
    /// A response with no content at all — not an empty string, which would still carry a
    /// <c>Content-Type</c>. This is what a <c>204</c> from <c>hotspots/change_status</c> and a
    /// <c>401</c> from a bad token both look like on the wire.
    /// </summary>
    public void EnqueueNoBody(HttpStatusCode statusCode) => Enqueue(_ => new HttpResponseMessage(statusCode));

    public static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string? body = null, string mediaType = "application/json")
    {
        var response = new HttpResponseMessage(statusCode);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }

        return response;
    }

    /// <summary>
    /// A redirect response. <paramref name="location"/> may be relative, which is the form a real
    /// redirect would most likely take.
    /// </summary>
    public static HttpResponseMessage CreateRedirect(HttpStatusCode statusCode, string location)
    {
        var response = new HttpResponseMessage(statusCode);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read as bytes as well as text: the form-encoding tests assert the exact octets, and a
        // string comparison would quietly forgive an encoding difference.
        byte[]? bytes = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var body = bytes is null ? null : Encoding.UTF8.GetString(bytes);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri, body, bytes, headers));
        }

        if (_responders.TryDequeue(out var responder))
        {
            return responder(request);
        }

        return Fallback?.Invoke(request)
            ?? throw new InvalidOperationException($"No stubbed response for {request.Method} {request.RequestUri}.");
    }
}

/// <summary>A captured request: method, URI, eagerly-read body (as text and bytes), and flattened headers.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? Body,
    byte[]? BodyBytes,
    IReadOnlyDictionary<string, string> Headers);
