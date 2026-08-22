# sonarqube-mcp

A [Model Context Protocol](https://modelcontextprotocol.io) server for **SonarQube Cloud**, covering
the surface a coding agent actually uses in nineteen tools: quality gates, issue triage, security
hotspots, measures, per-line coverage and rule explanations, plus four gated write tools for
transitioning, assigning, commenting on and reviewing findings. It is written in C# on .NET 10 and
ships as a Native AOT binary per platform — one self-contained executable, nothing to install,
starting in roughly ten milliseconds. The point of the project is a supply chain one person can
actually audit: the whole runtime dependency tree is three packages, all from Microsoft or the
official MCP organisation —
[`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol) (pinned exactly to
2.1.0), `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.Logging.Console`. Set
`SONARQUBE_MCP_READ_ONLY=1` and the four write tools are not registered at all. MIT licensed.

SonarQube Server — the self-hosted and Data Center editions — is explicitly out of scope.

## Tools

| Tool | What it does | Annotations |
|---|---|---|
| `listProjects` | Lists the projects in the configured organization with the key every other tool takes as `projectKey`. Start here when the key is unknown: it is not the repository name. | read-only, idempotent |
| `listComponents` | Lists the files and directories SonarQube analysed, each with its component key and project-relative path. Turns a file on disk into a key, and confirms a file was analysed at all. | read-only, idempotent |
| `listBranches` | Lists the analysed branches with each one's last analysis date, issue counts and gate status, and which is the main branch. | read-only, idempotent |
| `listPullRequests` | Lists the analysed pull requests with their gate status and new-code issue counts. Each entry's `key` is what the other tools take as `pullRequest`. | read-only, idempotent |
| `getQualityGateStatus` | Reads the gate verdict for a project, branch or pull request. `failingConditions` is the pre-filtered list of what is actually wrong, with thresholds and measured values. | read-only, idempotent |
| `searchIssues` | Searches issues, newest first, each with its **file path**, line, message, rule and clean-code impacts. Defaults to the outstanding work (`OPEN`, `CONFIRMED`). The main triage tool. | read-only, idempotent |
| `getIssue` | One issue in full: comments, the secondary locations that explain a data-flow finding, and `availableTransitions` — what `transitionIssue` will accept for it. | read-only, idempotent |
| `getRule` | Explains one rule: what it flags, why it matters and how to fix it, with the HTML converted to text and code fenced. Call it once per rule, not once per issue. | read-only, idempotent |
| `searchHotspots` | Searches security hotspots — security-sensitive code awaiting a human decision, a separate list from issues — with each one's vulnerability probability. | read-only, idempotent |
| `getHotspot` | One hotspot in full: the risk, what an attacker could do, how to make it safe, the review history, and `canChangeStatus`. | read-only, idempotent |
| `getComponentMeasures` | Reads named metrics for a project, directory or file. `missingMetrics` says what was not measured, `newCodeValue` carries the new-code number, and ratings come back as `A`–`E`. | read-only, idempotent |
| `listComponentMeasures` | The same metrics for every component under a project or directory, one row per file, sortable by a metric — this is how to answer "which files are worst?". | read-only, idempotent |
| `getMeasuresHistory` | One time series per metric over the analysis history: whether something is getting better or worse, rather than what it is now. | read-only, idempotent |
| `listMetrics` | The metric catalogue with what each key measures and whether higher is better. Call it instead of guessing a key — a key that does not exist is a silently missing measure, not an error. | read-only, idempotent |
| `getFileCoverage` | One file's per-line coverage: the lines tests never executed, the partially covered ones, the new-code lines and the duplicated ones. The source text is deliberately not returned. | read-only, idempotent |
| `transitionIssue` | Applies a triage transition — `accept`, `confirm`, `falsepositive`, `reopen`, `resolve`, `unconfirm` — with an optional comment. Reversible and recorded in the issue's changelog. | write, **not** destructive |
| `assignIssue` | Assigns an issue to a login, or clears the assignee. `__me__` assigns to the token's own account. | write, **not** destructive, idempotent |
| `addIssueComment` | Posts a comment on an issue. Two calls make two comments. | write, **not** destructive |
| `setHotspotStatus` | Records a hotspot review: `REVIEWED` with `SAFE` or `FIXED`, or `TO_REVIEW` to put it back on the list, with a justification comment. | write, **not** destructive, idempotent |

Every tool is annotated open-world (it talks to a live SonarQube organization) and returns
structured content. `Destructive` defaults to *true* in the MCP SDK, so all four write tools say
otherwise explicitly — none of them deletes anything, and every one is reversible or additive. The
full annotation table, and the four rows that are judgement calls rather than readings of the API,
are in [AGENTS.md](AGENTS.md#tool-table).

## Install

Two channels. The **Native AOT binary is the recommended one** — self-contained, nothing to
install, roughly ten milliseconds to start. The
[NuGet package](https://www.nuget.org/packages/sonarqube-mcp) is the convenience one: no download
step, at the cost of needing the .NET 10 SDK and a JIT startup.

### Native AOT binary (recommended)

Download the archive for your platform from
[GitHub Releases](https://github.com/lahma/sonarqube-mcp/releases) and extract it. Each archive is
named `sonarqube-mcp-{version}-{rid}` and contains the executable, `LICENSE` and this `README.md`.

| Platform | RID | Archive |
|---|---|---|
| Windows x64 | `win-x64` | `sonarqube-mcp-{version}-win-x64.zip` |
| Windows ARM64 | `win-arm64` | `sonarqube-mcp-{version}-win-arm64.zip` |
| Linux x64 | `linux-x64` | `sonarqube-mcp-{version}-linux-x64.tar.gz` |
| Linux ARM64 | `linux-arm64` | `sonarqube-mcp-{version}-linux-arm64.tar.gz` |
| macOS Apple silicon | `osx-arm64` | `sonarqube-mcp-{version}-osx-arm64.tar.gz` |

```bash
tar -xzf sonarqube-mcp-1.0.0-linux-x64.tar.gz
chmod +x sonarqube-mcp
./sonarqube-mcp --version
```

There is no runtime to install: the binary is self-contained. Put it wherever you like and note
the absolute path — that is what the MCP client configuration needs.

With no arguments the binary speaks MCP over stdio. It has one other mode worth knowing:

```
sonarqube-mcp status     Show the resolved configuration and probe the credential.
```

### NuGet package (`dnx`)

The same server is published to nuget.org as
[`sonarqube-mcp`](https://www.nuget.org/packages/sonarqube-mcp), a .NET tool package carrying the
`McpServer` package type. `dnx` — part of the .NET 10 SDK — downloads and runs it in one step, so
there is nothing to install and nothing to keep up to date by hand:

```bash
dnx sonarqube-mcp@1.0.0 --yes status
```

`--yes` accepts the download prompt and is consumed by `dnx` itself; everything after it is passed
to the server, so `status` works exactly as it does on the binary. With no trailing verb the server
speaks MCP over stdio, which is how a client should launch it:

```json
{
  "servers": {
    "sonarqube": {
      "type": "stdio",
      "command": "dnx",
      "args": ["sonarqube-mcp@1.0.0", "--yes"],
      "env": {
        "SONARQUBE_TOKEN": "...",
        "SONARQUBE_ORG": "my-org"
      }
    }
  }
}
```

Pin the version (`@1.0.0`) rather than floating: an MCP server is something an agent runs on your
behalf, and a pinned version is one you decided to run. This half is framework-dependent, so it
needs the .NET 10 SDK — if a client reports *the command "dnx" was not found*, that is what is
missing.

Releases are pushed to nuget.org by `.github/workflows/publish.yml`, a workflow only a `v*.*.*` tag
can start, using [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing):
the workflow exchanges its GitHub OIDC token for an API key that lives minutes. There is no NuGet
API key stored in this repository, so there is none to leak.

## Authentication

One credential: a SonarQube Cloud **user token**, sent as a Bearer credential.

1. Sign in at [sonarcloud.io](https://sonarcloud.io) and open
   [**Account → Security**](https://sonarcloud.io/account/security).
2. Under *Generate Tokens*, choose the **User Token** type. This matters: the *Project Analysis
   Token* and *Global Analysis Token* types next to it exist for CI scanners and are rejected by the
   web-service endpoints this server calls. If a token that looks valid answers 401 on every call,
   this is almost always why.
3. Name it (`sonarqube-mcp`, say), generate it, and copy it — SonarCloud shows it once.
4. Put it in the environment the MCP client launches the server with:

   ```bash
   export SONARQUBE_TOKEN=...
   ```

   ```powershell
   $env:SONARQUBE_TOKEN = '...'
   ```

The token inherits the permissions of the account that created it. Reading a private project needs
*Browse*; `transitionIssue`, `assignIssue` and `addIssueComment` need *Administer Issues*; and
`setHotspotStatus` needs *Administer Security Hotspots*. A token from an account without them
authenticates fine and then answers 403 on the write.

**No token is legal.** The server starts, and public projects are readable anonymously — which is
enough to try it out against an open-source project. Nothing private is reachable and no write tool
works.

### `SONARQUBE_ORG`

The organization key, which is the last segment of the URL when you open your organization:
`https://sonarcloud.io/organizations/`**`my-org`**. It is *not* the display name, and it is *not*
your GitHub organization's name unless they happen to coincide.

Only two tools need it — `listProjects`, which lists an organization's projects, and `getRule`,
whose rule catalogue is organization-scoped — so it is optional. Everything else identifies its
target by project key. Setting it is still worth it: without it, a model has no way to discover a
project key it was not told.

```bash
export SONARQUBE_ORG=my-org
```

## Client configuration

MCP clients launch the binary with an environment block and talk to it over stdio. Use the
absolute path to the executable.

### Claude Code

The shortest path is the plugin — it wires the server *and* the workflow skill in one step, and
prompts for the credentials. See [Agent skill](#agent-skill). To register only the server, either
run:

```bash
claude mcp add sonarqube \
  --env SONARQUBE_TOKEN=... \
  --env SONARQUBE_ORG=my-org \
  --env SONARQUBE_MCP_DEFAULT_PROJECT=my-org_my-repo \
  -- /usr/local/bin/sonarqube-mcp
```

or commit an `.mcp.json` in the repository so everyone working in that checkout gets the same
server, with the project key already pinned:

```json
{
  "mcpServers": {
    "sonarqube": {
      "type": "stdio",
      "command": "/usr/local/bin/sonarqube-mcp",
      "env": {
        "SONARQUBE_TOKEN": "...",
        "SONARQUBE_ORG": "my-org",
        "SONARQUBE_MCP_DEFAULT_PROJECT": "my-org_my-repo"
      }
    }
  }
}
```

### VS Code

`.vscode/mcp.json` in the workspace (or the user-level `mcp.json`):

```json
{
  "servers": {
    "sonarqube": {
      "type": "stdio",
      "command": "C:\\tools\\sonarqube-mcp\\sonarqube-mcp.exe",
      "env": {
        "SONARQUBE_TOKEN": "...",
        "SONARQUBE_ORG": "my-org",
        "SONARQUBE_MCP_DEFAULT_PROJECT": "my-org_my-repo"
      }
    }
  }
}
```

### Claude Desktop

`claude_desktop_config.json` (`%APPDATA%\Claude\` on Windows,
`~/Library/Application Support/Claude/` on macOS):

```json
{
  "mcpServers": {
    "sonarqube": {
      "command": "/usr/local/bin/sonarqube-mcp",
      "env": {
        "SONARQUBE_TOKEN": "...",
        "SONARQUBE_ORG": "my-org"
      }
    }
  }
}
```

`SONARQUBE_MCP_DEFAULT_PROJECT` is worth setting whenever the server is configured per checkout: the
`projectKey` argument becomes optional on every tool, which removes the single most common source of
404s. It is the `id` in a sonarcloud.io project URL — for example `my-org_my-repo` — not the
repository name.

## Environment variables

Configuration is environment variables only; there are no config files and no configuration
providers. Every value is read once at startup, and a malformed or out-of-range value falls back
to the documented default rather than failing the process (in server mode there would be nowhere
to report it — stdout is the protocol channel). `sonarqube-mcp status` prints the same list, and is
the authority when this table and the binary disagree.

| Variable | Default | Meaning |
|---|---|---|
| `SONARQUBE_TOKEN` | — | User token, sent as `Bearer`. Absent is legal: public projects stay readable, everything else fails with a message naming this variable. |
| `SONARQUBE_ORG` | — | Organization key — the last segment of `sonarcloud.io/organizations/{key}`. Required by `listProjects` and `getRule` only. |
| `SONARQUBE_URL` | `https://sonarcloud.io` | Base URL. Only `sonarcloud.io` and `sonarqube.us` over `https` are accepted; anything else is ignored with a warning on stderr. |
| `SONARQUBE_MCP_DEFAULT_PROJECT` | — | Default for the `projectKey` tool argument. The `id` in a project URL, not the repository name. |
| `SONARQUBE_MCP_READ_ONLY` | `0` | `1` registers only the fifteen read tools; the four write tools are absent from `tools/list`. Accepts `1/true/yes/on` and `0/false/no/off`. |
| `SONARQUBE_MCP_LOG_LEVEL` | `Information` | Minimum level for the stderr logger: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`. |
| `SONARQUBE_MCP_MAX_PAGE_SIZE` | `100` | Ceiling on a tool's `pageSize`, 1–500. 500 is the API's own hard limit. |
| `SONARQUBE_MCP_DEFAULT_PAGE_SIZE` | `50` | The `pageSize` a tool uses when the call omits it. Clamped to the ceiling above. |
| `SONARQUBE_MCP_MAX_SOURCE_LINES` | `2000` | Cap on `getFileCoverage`'s `from`–`to` span, 1–20000. |
| `SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS` | `100` | Whole-request timeout, 5–600. |

## Usage

The workflow the server is designed around is **the gate first, then the issues it failed on**. A
quality gate is one cheap call that says whether anything is wrong at all and which metrics are
responsible; an issue search without it is a guess about where to look.

Triaging a pull request whose gate has gone red, end to end:

1. **Ask what failed.**

   ```text
   getQualityGateStatus { "pullRequest": "3266" }
   ```

   `status: "ERROR"` plus a `failingConditions` array — each entry naming the metric, the
   comparator, the threshold and the measured value. `NONE` with no conditions is not a failure: it
   means no gate has been computed for that scope yet.

2. **List the issues behind it, scoped to the same pull request.**

   ```text
   searchIssues { "pullRequest": "3266", "impactSeverities": ["HIGH", "BLOCKER"] }
   ```

   Each issue arrives with its project-relative **file path** — not the `project:path/to/File.cs`
   component key the API returns — plus the line, the message, the rule key and the clean-code
   impacts. The default `issueStatuses` is `OPEN,CONFIRMED`, so this is the outstanding work. Narrow
   with `component` (a file or a directory), `rules` or `createdInLast` rather than paging: only the
   first 10,000 results of any search are reachable.

3. **Understand an unfamiliar rule — once.**

   ```text
   getRule { "ruleKey": "csharpsquid:S2259" }
   ```

   What the rule flags, why it matters and how to fix it, with the HTML converted to text and code
   fenced. The explanation is the same for every issue that rule raised, so fetch it per rule and
   never per issue.

4. **Fix the code in the checkout.** Nothing here edits a file; the server's job is to say what is
   wrong and where.

5. **Dispose of the one that is genuinely a false positive.**

   ```text
   getIssue { "issueKey": "AZ_xePOumT_q4T_1FWf8" }
   transitionIssue {
     "issueKey": "AZ_xePOumT_q4T_1FWf8",
     "transition": "falsepositive",
     "comment": "The null check is in the base constructor, which the analyser does not follow."
   }
   ```

   `getIssue` first, because its `availableTransitions` is the only reliable statement of what this
   issue will accept right now — a transition that is not legal from the current state is a 400.
   The change is immediate and visible to everyone in the organization, and it is reversible:
   `reopen` undoes it and the issue's changelog records who did what. If a model should not be able
   to do this at all, set `SONARQUBE_MCP_READ_ONLY=1` and the four write tools are never registered
   — not refused when called, absent from `tools/list` entirely.

Two more passes worth knowing, both described in full in the shipped skill:

- **Coverage.** `listComponentMeasures` with `sortByMetric: "uncovered_lines"` ranks the biggest gaps
  first. Ranking by `"coverage"` instead needs `ascending: true` — the default order is largest-first,
  and the largest coverage is the *best*-covered file. `getFileCoverage` on whichever file comes out
  on top returns the actual line numbers tests never executed. That is the actionable form of
  "coverage is too low".
- **Security hotspots.** `searchHotspots` with `status: "TO_REVIEW"`, `getHotspot` for the risk
  description and `canChangeStatus`, then `setHotspotStatus` with `REVIEWED` plus `SAFE` or `FIXED`
  and a justification comment.

### `status`

```
$ sonarqube-mcp status
sonarqube-mcp 1.0.0

Configuration
  Base URL:          https://sonarcloud.io
  SONARQUBE_TOKEN:   set
  Organization:      my-org
  Default project:   my-org_my-repo
  Read-only mode:    off
...
```

It reports what the server *would* use if it started right now, then probes the credential: whether
the token is accepted, and — when an organization is set — whether that organization is readable
with it, which is the only way to tell a bad token from a bad organization in one command. It never
prints any part of the token, not even a prefix, and it always exits 0: "nothing is configured" is a
fact it reports, not a failure of it.

## Agent skill

The workflows above are also shipped as an [Agent Skill](https://agentskills.io) — the open
`SKILL.md` format most coding agents now read — at
[`.claude/skills/sonarqube-code-quality/SKILL.md`](.claude/skills/sonarqube-code-quality/SKILL.md).

The server already teaches its own conventions: the tool schemas and the `initialize` instructions
arrive in every session. What they cannot teach is the *order* — the gate before the issue list, one
rule explanation per rule, `getIssue` before a transition — because each schema only describes its
own tool. That, plus the recovery moves and when the checkout already has the answer, is what the
skill holds. Only its one-paragraph description is always in context; the body loads when SonarQube
work actually starts.

There is exactly one copy of the file, and everything below installs that copy.

### Claude Code: the plugin

This repository is also its own [plugin marketplace](https://code.claude.com/docs/en/plugin-marketplaces),
so one install delivers **both** the skill and the server:

```
/plugin marketplace add lahma/sonarqube-mcp
/plugin install sonarqube-mcp@sonarqube-mcp
```

or, without starting a session:

```bash
claude plugin marketplace add lahma/sonarqube-mcp
claude plugin install sonarqube-mcp@sonarqube-mcp
```

Enabling it asks for the token, the organization and an optional default project in a dialog. Every
field is optional and each maps to the environment variable of the same name in *Environment
variables* above; the token is marked sensitive rather than written to `settings.json` in the clear.
The plugin runs the server with `dnx`, so it needs the .NET 10 SDK, and it is **pinned to the
release it shipped with** rather than floating, so the skill and the server it describes always move
together. `/plugin update` picks up the next release.

Working inside a checkout of this repository needs none of that — `.claude/skills/` is loaded as a
project skill automatically.

### Any other tool

The skill installs from this repository with either of the two ecosystem CLIs, both of which read
`.claude/skills/` out of the source repo and write to whatever location your agent expects:

```bash
npx skills add lahma/sonarqube-mcp --skill sonarqube-code-quality -a codex -y
gh skill install lahma/sonarqube-mcp sonarqube-code-quality --agent codex
```

Replace `codex` with `claude-code`, `cursor`, `gemini-cli` or `github-copilot`. Or copy the
directory by hand — it is one file.

Both the skill and the plugin manifests are checked against the server on every build: one test
cross-references every tool the skill names against the real tool inventory in both directions, so
it can neither name a tool that does not exist nor quietly omit one that does; another asserts that
the plugin's skill path still resolves, that its version is the one in `CHANGELOG.md`, that the
`dnx` pin matches it, and that every credential it prompts for reaches the server.

## Troubleshooting

Start with `sonarqube-mcp status`: it prints the base URL, whether a token is set, the organization,
the default project and the read-only flag, then probes the credential — and none of the values.

**"No SonarQube token is configured".** `SONARQUBE_TOKEN` is not visible to the *server process*,
which is not the same as not being set in your shell: MCP clients launch the server with the
environment block from their own configuration file. Put it there, then restart the client — a
running server reads the environment exactly once, at startup.

**401 on every call with a token that looks right.** Almost always the wrong *kind* of token. It
must be a **User Token** from Account → Security; a *Project Analysis Token* or *Global Analysis
Token* authenticates scanners, not web services. A token from a different SonarQube instance than
`SONARQUBE_URL` fails the same way. Note that a rejected token's 401 comes back with an empty body,
so there is no server-side message to quote — this is the whole diagnosis.

**404 on a project you can see in the browser.** The project key is the `id` query parameter in a
sonarcloud.io project URL, not the repository name and not `owner/repo`. It usually looks like
`my-org_my-repo`. Call `listProjects` (with `SONARQUBE_ORG` set) to see the real keys. A branch or
pull request that has never been analysed is also a 404 rather than an empty result: `listBranches`
and `listPullRequests` say which ones exist.

**404 from `getFileCoverage` on a file that definitely exists.** The source endpoints answer 404 —
not 403 — when the token lacks the *See Source Code* permission on the project, so this can mean
either a wrong component key or a missing permission. Confirm the key with `listComponents`; if the
key is right, the permission is the problem.

**403 on a write.** The token is valid and the account lacks the permission. Transitioning,
assigning and commenting need *Administer Issues*; a hotspot review needs *Administer Security
Hotspots*. `getHotspot` reports `canChangeStatus` before you try. Ask a project administrator, or
use a token from an account that has the permission.

**429 Too Many Requests.** SonarQube Cloud publishes no rate limits and sends no `X-RateLimit-*`
headers, so there is no number to read. The client already retries with exponential backoff and
honours `Retry-After`; a 429 that reaches you survived that. Wait about a minute and cut the request
rate: a smaller `pageSize`, fewer `metricKeys` per call, and a narrower `searchIssues` filter
instead of paging through everything.

**A measure you asked for is not in the result.** It is in `missingMetrics`. SonarQube silently omits
a metric it has no data for, so absent means *not measured* — never zero. Check the key against
`listMetrics`: a key that does not exist produces exactly the same silence.

**`getRule` returns no description sections.** The server is running without a token. SonarQube
Cloud sends rule descriptions only to an authenticated request: the identical call answers with no
`descriptionSections` anonymously and with the full text when any ordinary user token is attached
(verified against sonarcloud.io). Set `SONARQUBE_TOKEN` and restart the server — `sonarqube-mcp
status` will tell you whether it is set and whether it is accepted. Until then the tool degrades
rather than fails: `sections` is empty, a `note` says why, and the rule's name, impacts and
clean-code attribute are still returned.

**Nothing works and you want to see why.** `SONARQUBE_MCP_LOG_LEVEL=Debug`. All logging goes to
stderr; MCP clients usually surface it in a server log pane.

## Security

- **The token is never logged, printed or cached.** It is read from the environment at startup,
  attached as an `Authorization: Bearer` header on each individual request — never on the
  `HttpClient`'s default headers, so nothing can carry it by accident — and never written to disk.
  `status` reports whether it is set and nothing else: not a prefix, not a length, not a masked
  form, because a status readout has a habit of ending up in a bug report.
- **Read-only mode is a registration decision, not a runtime check.** With
  `SONARQUBE_MCP_READ_ONLY=1` the write tool class is never registered, so `transitionIssue`,
  `assignIssue`, `addIssueComment` and `setHotspotStatus` are absent from `tools/list` — a model
  cannot propose a call that does not exist. A smoke test drives a real stdio handshake with the
  flag set and asserts their absence.
- **The base URL is allowlisted.** `SONARQUBE_URL` must be `https` on `sonarcloud.io`,
  `www.sonarcloud.io` or `sonarqube.us`; anything else is ignored with a warning. Redirects are not
  followed at all — a 3xx is an error naming the `Location` — so a credential cannot be carried to a
  host the allowlist never approved.
- **No telemetry, no analytics, no update check.** The server talks to exactly one host, the one
  above, and only when a tool is called.
- **The supply chain is three runtime packages**, all from Microsoft or the official MCP
  organisation, centrally pinned in `Directory.Packages.props` with transitive pinning on, and the
  MCP SDK pinned to an exact version. Adding one requires a recorded decision in
  [AGENTS.md](AGENTS.md). Builds are deterministic and SourceLink-enabled, so a release binary can
  be traced back to the commit it came from.

## Building from source

Needs the .NET 10 SDK (the exact version is pinned in `global.json`).

```bash
git clone https://github.com/lahma/sonarqube-mcp.git
cd sonarqube-mcp

./build.sh Test           # restore, compile, run the tests
./build.sh SmokeTest      # AOT publish + real stdio JSON-RPC handshakes against the binary
./build.sh Pack           # the NuGet tool package, into artifacts/packages
```

```powershell
.\build.ps1 Test
.\build.ps1 SmokeTest
.\build.ps1 Pack
```

The orchestrator is [Fallout](https://fallout.build); `build.ps1` / `build.sh` bootstrap the CLI
from `.config/dotnet-tools.json`, so nothing needs installing globally. To publish a binary for a
specific platform:

```
dotnet fallout PublishAot --runtime linux-arm64
```

The executable lands in `artifacts/publish/{rid}/` and the release archive in
`artifacts/archives/`. `CHANGELOG.md` is the version authority — the build parses its top section
and stamps that version into the binary, the package and `.mcp/server.json` (a test fails if the
manifest falls behind). Note that `PublishAot` applies only when a runtime identifier is given:
`Pack` deliberately produces the portable, framework-dependent tool half from the same project.

## Contributing

Read [AGENTS.md](AGENTS.md) first. It records the design decisions, the package budget, the API
gotchas that cost a probe each to find, and the handful of hard rules that are easy to break by
accident (never transcribe SonarSource's own MCP server, no `Console.Write` outside `Cli/`, never
hand-edit the generated `build.yml`).

## License

MIT. See [LICENSE](LICENSE).
