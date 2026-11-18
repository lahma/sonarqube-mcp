# 1.3.0

- **`dnx` now runs the native binary.** The NuGet package is now a set of RID-specific tool
  packages. `mcp-sonarqube` names one package per release platform (Windows, Linux and macOS, x64
  and Arm64 as released), each holding the Native AOT binary from the matching release archive,
  plus a framework-dependent `mcp-sonarqube.any` for every other platform; `dnx` picks the right one
  by itself. Measured on Windows x64, a client's first answer through `dnx` went from 715 ms to
  357 ms, and on those five platforms the server needs no .NET runtime of its own. Every binary in
  those packages answered a handshake on its own architecture before it was published, as the
  archives' always have, and the packages no longer carry debug symbols (99 MB on Windows).
- **A mis-cased or unknown argument is now refused, with the right spelling.** `getRule` called with
  `RuleKey` used to fail with only "An error occurred invoking 'getRule'.", and an optional argument
  in the wrong case, such as `ProjectKey` or `PullRequest`, was silently dropped, so the call
  answered about the default project or the main branch instead. Every call is now checked against
  the argument names the tool advertises before it runs. A wrong name is answered with the one it
  most likely meant and the accepted list, and a missing required argument is named. Argument names
  are case-sensitive.
- **Listed in the MCP Registry** as `io.github.lahma/sonarqube-mcp`, published by the release
  workflow from this version on.
- Build and release: each release leg packs and handshake-tests its platform's tool package, and the
  publish job collects all five and pushes them ahead of the package that names them. Test runs opt
  out of the test platform's telemetry.

# 1.2.0

- **`getQualityGateStatus` no longer depends on a deprecated endpoint.** SonarQube Cloud deprecated
  `api/qualitygates/project_status` on 16 September 2025, with no successor in its v2 API. The gate
  now comes from the `alert_status` and `quality_gate_details` measures, which carry the same verdict
  and the same conditions (compared live on a pull request). The result is unchanged, and a scope
  without a gate still reads `NONE`.
- **Security hotspots are being retired, and the server now says so.** SonarQube Cloud deprecated
  hotspots on 16 June 2026 and is migrating them into issues tagged `former-hotspot`, after which
  `searchHotspots` returns nothing. An empty result now points at `searchIssues` with
  `tags=former-hotspot` instead of reading as "no security findings", and the server instructions
  and the skill no longer say that hotspots never appear among issues.
- **New: `authors` on `searchIssues` and `summarizeIssues`** - the issues a commit identity introduced,
  usually your `git config user.email`. It needs a token, because SonarQube does not tell anonymous
  callers who wrote a line. SonarQube wants this one parameter repeated rather than comma-joined; joined,
  it silently matches nothing. `getIssue` now reports the issue's SCM `author`.
- **New: per-framework fix guidance.** Issues now carry `ruleContext` (`spring`, `servlet`, `jsp`...),
  and `getRule`'s new `context` argument returns only that framework's variant of the fix. The key
  existed all along; SonarQube sends it only when asked, and the server never asked.
- **New: `fixableByAgent`** on `searchIssues` and `summarizeIssues`: SonarQube's own, undocumented
  classification of the issues an AI agent can fix.
- **New: `summarizeIssues` groups by security standard** - `owaspTop10-2021`, `cwe` and
  `sonarsourceSecurity`.
- **Fixed: dependency-risk (SCA) ratings came back as numbers.** `sca_rating_*` measures are letters
  now, like every other rating.
- **Fixed: `transitionIssue` could lose a pull-request issue's next transitions.** When SonarQube's
  answer did not include them, the follow-up read searched the main branch, where a pull request's
  issue does not exist. It now reads the issue in its own scope.
- **Listable in the MCP Registry.** `.mcp/server.json` moves to the registry's current schema and its
  only accepted NuGet base URL, and the README carries the ownership marker for
  `io.github.lahma/sonarqube-mcp`.
- **The Linux binaries need glibc 2.34 or newer.** That is unchanged from 1.1.1, but it is now stated in
  the README and enforced by the release build, which fails if a newer runner image would raise it.
- Dependencies: `ModelContextProtocol` 2.2.0 to 2.4.0 (the pin stays exact), `Microsoft.Extensions.*`
  10.0.12 to 10.0.15. The build moves to Fallout 10.5.0, which clears two high-severity advisories in
  the build's own dependencies (nothing shipped was affected) and makes its `NuGet.Frameworks` pin
  unnecessary. The tests move from xunit to NUnit 5 on Microsoft.Testing.Platform.
- Still .NET 10, deliberately: measured against .NET 11, the binary came out 2% larger and no faster,
  and both releases reach end of support on the same day.

# 1.1.1

- **Fixed: the Claude Code plugin blanked a token the user already had.** The manifest mapped every
  credential onto its own environment variable through a `${user_config.X}` placeholder, and an
  option the user never filled in substitutes as the **empty string** rather than being omitted - so
  installing the plugin set `SONARQUBE_TOKEN=""` in the server's environment and shadowed a
  perfectly good ambient value. The server then ran anonymously, and because SonarQube reports a
  private project to an anonymous caller as `404 Project doesn't exist`, the failure read as a wrong
  project key; `listProjects` came back empty for the same reason and appeared to confirm it. There
  was no workaround: a value in `pluginConfigs.<plugin>.options` is treated literally, so the only
  way to run the plugin was to copy the token in plaintext into `settings.json`. The manifest now
  writes to `CLAUDE_PLUGIN_OPTION_*` names, which the server reads **first** and treats as absent
  when blank, falling through to the plain variable - so a filled-in option still wins and an empty
  one no longer destroys anything. Reported as issue #1.
- A `404` and an empty `listProjects` now say when the request went out without a credential, rather
  than leaving "wrong key" as the only hypothesis on offer.
- `sonarqube-mcp status` reports **which** variable supplied the token (`set, from SONARQUBE_TOKEN`
  or `set, from CLAUDE_PLUGIN_OPTION_SONARQUBE_TOKEN`) instead of a bare `set`. Two sources can
  supply it, and which one won is the difference between "my token is being ignored" and "my token
  is wrong". No part of the value is ever printed.

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