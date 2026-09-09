# 1.1.0

- **Fixed: `getIssue` could not read a pull request's or a branch's issue at all.** SonarQube Cloud
  only honours the `issues=` key filter inside the scope named by `componentKeys`, so a lookup that
  passed `pullRequest` alone had the scope silently ignored, fell back to the main branch, and
  answered `200` with an empty list - which the tool reported as "no issue with that key". Verified
  live on 2026-09-09. `getIssue` now takes an optional `projectKey`, defaulted from
  `SONARQUBE_MCP_DEFAULT_PROJECT`, and sends it; a scoped lookup with no project to name is refused
  before the request leaves the process instead of coming back as a phantom 404. The not-found
  message now also names `getHotspot`, because a hotspot key is shaped exactly like an issue key.
- `getAnalysisStatus` reads SonarQube's Compute Engine queue: what is queued or running, and the
  last analysis that finished, each with the branch or pull request it was for. A scanner step going
  green in CI only means the report was uploaded - until SonarQube finishes processing it, the
  quality gate, the issue list and every measure answer from the previous analysis without saying
  so. It is also the only place a failed analysis and its error message are visible, which is the
  difference between numbers that are bad and numbers that are stale.
- `summarizeIssues` counts a project's issues grouped by rule, file, directory, severity, quality,
  status, tag, language, assignee or author in a single call, instead of paging toward the API's
  10000-result ceiling. Files are reported as paths rather than the component UUIDs the API returns,
  rule keys carry their titles, a grouping that hits SonarQube's hundred-value cap is flagged
  `truncated`, and the result says plainly that each grouping is computed with its own filter
  removed - so those counts describe the search without that one filter and do not add up to
  `matchingIssues`. `countBy: "effort"` counts estimated remediation minutes instead.
- `getIssueChangelog` returns who changed an issue's status, resolution or assignee, when, and from
  what - the history to read before re-deciding something a reviewer already decided. It takes the
  key alone and is not scope-sensitive. An empty history from a server with no token means the
  history could not be read rather than that nothing happened, and the result says which.
- `bulkUpdateIssues` applies one transition, assignment and/or comment to up to 500 issues in one
  call. It is annotated non-destructive and non-idempotent, and it is a write tool, so
  `SONARQUBE_MCP_READ_ONLY` removes it along with the other four. SonarQube answers with counts and
  names no issue, so an issue the transition was not legal from comes back as `ignored`; the result
  says so and points at `getIssue`.
- The server instructions gained the two conventions whose failure modes are silent: an issue key
  belongs to one analysis scope, and numbers are only as fresh as the last analysis. The shipped
  agent skill gained a pull-request-analysed-in-CI playbook covering the same ground.
- Dependencies: `ModelContextProtocol` 2.1.0 to 2.2.0 (the pin stays exact),
  `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.Logging.Console` 10.0.10 to
  10.0.12, `Microsoft.NET.Test.Sdk` 18.8.1 to 18.10.0. xunit stays on 3.x deliberately - version 4
  drops the VSTest mode the build's test reporting depends on - and Fallout stays on 10.4.0, which
  is the stable channel and a superset of the 11.0.x edge line.

# 1.0.0

Initial release.

- Fifteen read tools over MCP stdio: `listProjects`, `listComponents`, `listBranches`,
  `listPullRequests` and `getQualityGateStatus` for the project surface; `searchIssues`, `getIssue`,
  `getRule`, `searchHotspots` and `getHotspot` for triage; `getComponentMeasures`,
  `listComponentMeasures`, `getMeasuresHistory`, `listMetrics` and `getFileCoverage` for measures
  and coverage. Every tool carries explicit read-only / idempotent / open-world annotations and
  returns structured content.
- Four write tools for triage: `transitionIssue` (accept, confirm, falsepositive, reopen, resolve,
  unconfirm), `assignIssue`, `addIssueComment` and `setHotspotStatus`. All four are annotated
  non-destructive — every one of them is reversible or additive, and nothing is deleted — and all
  four state in their descriptions that the change is immediate and visible to the whole
  organization.
- `SONARQUBE_MCP_READ_ONLY` removes those four from the server entirely: the write tool class is
  never registered, so they do not appear in `tools/list` rather than failing when called. The smoke
  test drives a second stdio handshake with the flag set and asserts their absence.
- `searchIssues` reports each issue with its project-relative **file path** rather than the
  component key the API returns, resolved through the response's own components list, and defaults
  to the still-open work (`OPEN`, `CONFIRMED`). Legacy vocabulary — `MAJOR`, `CODE_SMELL`,
  `RESOLVED` and the rest — is rejected with the modern equivalent named in the message rather than
  translated silently.
- `getIssue` returns `availableTransitions`, which is what makes `transitionIssue` usable without
  guessing; `transitionIssue` returns the transitions that are legal afterwards.
- Measures are reported the way they have to be read: an absent metric is listed in
  `missingMetrics` instead of looking like a zero, new-code numbers are lifted out of the period
  into `newCodeValue`, and rating metrics come back as `A`–`E` rather than as the `1.0`–`5.0` the
  API sends. `listMetrics` is the catalogue, filtered client-side, with the multi-kilobyte per-line
  data metrics left out unless asked for.
- `getFileCoverage` returns one file's uncovered and partially covered lines, plus new-code and
  duplicated lines — the one thing SonarQube knows that a checkout does not. The syntax-highlighted
  source the API sends is deliberately dropped; the file is on disk.
- `getRule` converts a rule's HTML description sections to text with code fenced, capped, and
  truncation marked rather than silent. Sections come back in the order they were asked for, and a
  rule with per-framework fix guidance returns `how_to_fix` once per context. The descriptions
  themselves need a token — SonarQube Cloud sends them only to an authenticated request — so a
  tokenless server returns the rule's name, impacts and clean-code attribute with a note explaining
  the gap instead of failing.
- Errors are translated into instructions: a missing token names `SONARQUBE_TOKEN` and where to
  create one, a 404 explains that a project key is not a repository name, a 404 from the sources
  endpoints explains that it can also mean a missing permission, the 10,000-result cap becomes
  advice to narrow the search, and a 403 on a write names the permission that is missing.
- Configuration is environment variables only, read once at startup, and never fatal: a malformed or
  out-of-range value falls back to the documented default and logs to stderr, because a server that
  refuses to start has no channel to explain itself. `sonarqube-mcp status` reports what the server
  would use and probes the credential without printing any part of it.
- An Agent Skill ships with the repository at
  `.claude/skills/sonarqube-code-quality/SKILL.md`, in the open `SKILL.md` format: the triage,
  coverage and hotspot playbooks — the call *order* no single tool schema can describe — plus the
  recovery moves and when the local checkout already has the answer. `AgentSkillTests` cross-checks
  every tool it names against the reflected inventory in both directions, so it can neither invent a
  tool nor silently omit one.
- The repository is also its own Claude Code plugin marketplace, so
  `/plugin marketplace add lahma/sonarqube-mcp` followed by `/plugin install sonarqube-mcp` wires up
  the skill **and** the server in one step: the plugin runs `dnx mcp-sonarqube@1.0.0` and prompts
  for the token, storing it as a sensitive value. The plugin's source is the repository root, which
  is what lets it point at the one canonical `SKILL.md` instead of carrying a copy, and the `dnx`
  pin, the plugin version and this changelog are asserted to be the same string.
- Native AOT single binary for win-x64, win-arm64, linux-x64, linux-arm64 and osx-arm64, published
  from a three-package runtime dependency tree — the MCP SDK, dependency injection and console
  logging, and nothing else.
- Also on nuget.org as the `mcp-sonarqube` .NET tool package, so `dnx mcp-sonarqube@1.0.0 --yes`
  runs the server without a download step. The package id is the one name that is not
  `sonarqube-mcp`: SonarSource holds both the `SonarQube*` and the `SonarCloud*` reserved id
  prefixes on nuget.org, so the package leads with the unreserved `mcp-` prefix and installs a
  command still called `sonarqube-mcp`. It
  is pushed by trusted publishing — a tag-triggered workflow exchanges its GitHub OIDC token for an
  API key that lives minutes — so no NuGet API key is stored anywhere. The Native AOT binaries
  remain the recommended way to run the server.
- SonarQube Cloud only, over the v1 `api/` web services. SonarQube Server is out of scope, and
  `SONARQUBE_URL` is allowlisted to the Cloud hosts rather than merely parsed.
