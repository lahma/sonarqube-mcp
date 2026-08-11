using SonarQube.Mcp.Http.Models;

namespace SonarQube.Mcp.Http;

/// <summary>
/// One page of results with its paging numbers already resolved, whichever of SonarQube's three
/// paging shapes the endpoint happened to use.
/// </summary>
/// <param name="Items">The page's items; empty, never <see langword="null"/>.</param>
/// <param name="Page">The 1-based page number this page represents.</param>
/// <param name="PageSize">The page size this page was served at.</param>
/// <param name="Total">
/// Total items across all pages. Nullable because an endpoint may report none, and because it is
/// the number a caller must not silently invent — <c>hasMore</c> is derived from it.
/// </param>
/// <typeparam name="T">The item type.</typeparam>
internal sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int? Total);

/// <summary>
/// Collapses SonarQube's three paging shapes into one.
/// </summary>
/// <remarks>
/// <para>
/// Verified live on 2026-08-11, all three from the same API version:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Nested only</b> — <c>{"paging":{"pageIndex":1,"pageSize":3,"total":1}}</c>, as
/// <c>components/search</c>, <c>components/tree</c>, <c>hotspots/search</c> and
/// <c>measures/*</c> send it.
/// </description></item>
/// <item><description>
/// <b>Flat only</b> — <c>{"total":1,"p":1,"ps":1}</c>, as <c>rules/search</c> and
/// <c>metrics/search</c> send it.
/// </description></item>
/// <item><description>
/// <b>Both</b> — <c>issues/search</c> sends the flat trio <em>and</em> a <c>paging</c> object with
/// the same numbers.
/// </description></item>
/// </list>
/// <para>
/// Each field is resolved independently rather than picking one shape wholesale, so a response that
/// carries a partial <c>paging</c> object still contributes what it has instead of being discarded.
/// The requested values are the last resort: an endpoint that reports nothing paged the way we
/// asked it to, so echoing the request is the only honest answer — and it is never used for
/// <see cref="PagedResult{T}.Total"/>, which stays <see langword="null"/> rather than being guessed
/// at from the item count (a full page would then look like the last one).
/// </para>
/// </remarks>
internal static class PagedResult
{
    /// <summary>Builds a page from an envelope and its already-extracted items.</summary>
    /// <param name="envelope">The response, or <see langword="null"/> if there was none.</param>
    /// <param name="items">The items lifted out of the envelope; <see langword="null"/> becomes empty.</param>
    /// <param name="requestedPage">The 1-based page the client asked for.</param>
    /// <param name="requestedPageSize">The page size the client asked for.</param>
    /// <typeparam name="T">The item type.</typeparam>
    internal static PagedResult<T> From<T>(
        PagedEnvelopeDto? envelope,
        IReadOnlyList<T>? items,
        int requestedPage,
        int requestedPageSize)
    {
        var paging = envelope?.Paging;

        return new PagedResult<T>(
            items ?? [],
            paging?.PageIndex ?? envelope?.P ?? requestedPage,
            paging?.PageSize ?? envelope?.Ps ?? requestedPageSize,
            paging?.Total ?? envelope?.Total);
    }
}
