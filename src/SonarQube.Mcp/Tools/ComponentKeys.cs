using System.Text;

using SonarQube.Mcp.Http.Models;

namespace SonarQube.Mcp.Tools;

/// <summary>
/// Component keys, the paths behind them, and the web links SonarQube does not return.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, both of which exist because the API answers with keys where a caller wants paths and
/// links. The <c>components[]</c> sidecar join turns <c>quartznet_quartznet:src/Quartz/Foo.cs</c>
/// into <c>src/Quartz/Foo.cs</c> — the single biggest context saving in the tool layer, because
/// otherwise every issue in every list repeats the project key.
/// </para>
/// <para>
/// The second job is deep links. SonarQube returns no web URL for an issue, hotspot, project or
/// file, but every one of them is derivable from the base URL, so they are composed once here and
/// every result record carries <c>url</c>. That is the direct inverse of an API that hands out
/// links: here we do the deriving so the model does not have to.
/// </para>
/// </remarks>
internal static class ComponentKeys
{
    /// <summary>An empty map, for the responses that carry no sidecar at all.</summary>
    internal static readonly IReadOnlyDictionary<string, string> NoComponents =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Builds the component-key-to-path map from a response's <c>components[]</c> sidecar.
    /// </summary>
    /// <remarks>
    /// A duplicate key must not throw. The sidecar is SonarQube's, not ours, and a response that
    /// listed the same component twice would take down a whole issue search over a detail no caller
    /// can see — the last entry wins, because both carry the same path anyway.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> BuildPathMap(IReadOnlyList<ComponentDto>? components)
    {
        if (components is null || components.Count == 0)
        {
            return NoComponents;
        }

        var map = new Dictionary<string, string>(components.Count, StringComparer.Ordinal);

        foreach (var component in components)
        {
            if (string.IsNullOrEmpty(component.Key))
            {
                continue;
            }

            // A project has no path of its own; its "path" is its name, and falling back to the key
            // is what keeps a project-level issue from reporting an empty file.
            var path = component.Path ?? component.LongName ?? component.Name;

            if (!string.IsNullOrEmpty(path))
            {
                map[component.Key] = path;
            }
        }

        return map;
    }

    /// <summary>
    /// Resolves a component key to a project-relative path.
    /// </summary>
    /// <remarks>
    /// The fallback is the substring after the first <c>:</c>, which is what a component key is made
    /// of. It is used when the sidecar is missing — a request that did not ask for it, or an endpoint
    /// that does not send one — and it produces the right answer for every file component; only a
    /// directory or project component, whose key has no path part, falls through to the key itself.
    /// </remarks>
    internal static string PathFor(string? componentKey, IReadOnlyDictionary<string, string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (string.IsNullOrEmpty(componentKey))
        {
            return string.Empty;
        }

        if (paths.TryGetValue(componentKey, out var path))
        {
            return path;
        }

        var separator = componentKey.IndexOf(':', StringComparison.Ordinal);

        return separator >= 0 && separator + 1 < componentKey.Length
            ? componentKey[(separator + 1)..]
            : componentKey;
    }

    /// <summary>An issue's page: the issue list filtered to it, with it open.</summary>
    internal static string IssueUrl(string baseUrl, string? project, string? issueKey, AnalysisScope scope)
    {
        var url = new StringBuilder(baseUrl).Append("/project/issues?id=").Append(Escape(project));

        if (!string.IsNullOrEmpty(issueKey))
        {
            url.Append("&issues=").Append(Escape(issueKey)).Append("&open=").Append(Escape(issueKey));
        }

        return AppendScope(url, scope).ToString();
    }

    /// <summary>A security hotspot's page.</summary>
    internal static string HotspotUrl(string baseUrl, string? project, string? hotspotKey, AnalysisScope scope)
    {
        var url = new StringBuilder(baseUrl).Append("/security_hotspots?id=").Append(Escape(project));

        if (!string.IsNullOrEmpty(hotspotKey))
        {
            url.Append("&hotspots=").Append(Escape(hotspotKey));
        }

        return AppendScope(url, scope).ToString();
    }

    /// <summary>A project's overview page — where the quality gate is shown.</summary>
    internal static string ProjectUrl(string baseUrl, string? project, AnalysisScope scope) =>
        AppendScope(
            new StringBuilder(baseUrl).Append("/project/overview?id=").Append(Escape(project)),
            scope).ToString();

    /// <summary>A file or directory's page in the code browser.</summary>
    internal static string ComponentUrl(string baseUrl, string? project, string? component, AnalysisScope scope)
    {
        var url = new StringBuilder(baseUrl).Append("/code?id=").Append(Escape(project));

        if (!string.IsNullOrEmpty(component))
        {
            url.Append("&selected=").Append(Escape(component));
        }

        return AppendScope(url, scope).ToString();
    }

    /// <summary>
    /// Appends the branch or pull request the call was scoped to. Without it the link opens the main
    /// branch, where the issue being linked to may not exist at all.
    /// </summary>
    private static StringBuilder AppendScope(StringBuilder url, AnalysisScope scope)
    {
        if (!string.IsNullOrEmpty(scope.Branch))
        {
            url.Append("&branch=").Append(Escape(scope.Branch));
        }
        else if (!string.IsNullOrEmpty(scope.PullRequest))
        {
            url.Append("&pullRequest=").Append(Escape(scope.PullRequest));
        }

        return url;
    }

    /// <summary>
    /// Escapes a query-string value. Component keys carry <c>:</c> and <c>/</c> and branch names
    /// carry <c>/</c>, none of which may travel raw in a query string.
    /// </summary>
    private static string Escape(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : Uri.EscapeDataString(value);
}
