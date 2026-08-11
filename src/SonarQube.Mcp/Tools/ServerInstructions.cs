namespace SonarQube.Mcp.Tools;

/// <summary>
/// The instructions sent to the client during <c>initialize</c>.
/// </summary>
/// <remarks>
/// This text is the only chance to teach the conventions no per-tool description can enforce: which
/// identifiers SonarQube accepts, that absent is not zero, and that ratings are letters. It is read
/// once per session and every line costs context in every session, so it stays about ten lines —
/// anything longer belongs in a parameter description, where it is read only when relevant.
/// </remarks>
internal static class ServerInstructions
{
    /// <summary>The instruction text.</summary>
    internal const string Text = """
        Tools for SonarQube Cloud: projects, quality gates, issues, security hotspots, measures and coverage.

        - Set SONARQUBE_TOKEN and SONARQUBE_ORG in this server's environment on the first authentication failure, then restart it. `sonarqube-mcp status` reports what the server currently sees.
        - projectKey is the `id` in a sonarcloud.io project URL, not the repository name. Call listProjects to find it; SONARQUBE_MCP_DEFAULT_PROJECT makes the parameter optional.
        - branch and pullRequest are mutually exclusive on every tool; omitting both reads the main branch. pullRequest is the SCM number as a string, which is the `key` listPullRequests returns.
        - Issues use the modern vocabulary: issueStatuses (OPEN, CONFIRMED, FALSE_POSITIVE, ACCEPTED, FIXED) and impactSeverities (INFO, LOW, MEDIUM, HIGH, BLOCKER). MINOR/MAJOR/CRITICAL and BUG/CODE_SMELL/VULNERABILITY are rejected with their modern equivalents.
        - searchIssues defaults to OPEN,CONFIRMED. Only the first 10000 results of any search are reachable, so narrow with component, rules or dates instead of paging.
        - Security hotspots are a separate list from issues and never appear in searchIssues.
        - A measure that is absent was not measured; it does not mean zero. missingMetrics names them. Rating metrics are returned as letters A-E, where A is best.
        - getRule explains a rule once; do not fetch it per issue. getIssue's availableTransitions is what transitionIssue accepts for that issue.
        - transitionIssue, assignIssue, addIssueComment and setHotspotStatus take effect immediately on the real organization and are visible to everyone in it. Read the issue or hotspot first. With SONARQUBE_MCP_READ_ONLY set, they are not registered at all.
        """;
}
