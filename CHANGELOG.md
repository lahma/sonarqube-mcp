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
  the skill **and** the server in one step: the plugin runs `dnx sonarqube-mcp@1.0.0` and prompts
  for the token, storing it as a sensitive value. The plugin's source is the repository root, which
  is what lets it point at the one canonical `SKILL.md` instead of carrying a copy, and the `dnx`
  pin, the plugin version and this changelog are asserted to be the same string.
- Native AOT single binary for win-x64, win-arm64, linux-x64, linux-arm64 and osx-arm64, published
  from a three-package runtime dependency tree — the MCP SDK, dependency injection and console
  logging, and nothing else.
- Also on nuget.org as the `sonarqube-mcp` .NET tool package, so `dnx sonarqube-mcp@1.0.0 --yes`
  runs the server without a download step. It is pushed by trusted publishing — a tag-triggered
  workflow exchanges its GitHub OIDC token for an API key that lives minutes — so no NuGet API key
  is stored anywhere. The Native AOT binaries remain the recommended way to run the server.
- SonarQube Cloud only, over the v1 `api/` web services. SonarQube Server is out of scope, and
  `SONARQUBE_URL` is allowlisted to the Cloud hosts rather than merely parsed.
