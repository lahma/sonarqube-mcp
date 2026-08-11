# AGENTS.md

Guidance for humans and AI agents working in this repository. `CLAUDE.md` imports this file,
so keep it the single source of truth.

`sonarqube-mcp` is a Model Context Protocol server for **SonarQube Cloud** (sonarcloud.io and
sonarqube.us), written in C# on .NET 10 and published as a Native AOT single binary per RID.
SonarQube Server — the self-hosted and Data Center editions — is explicitly out of scope, and so is
the v2 API on `api.sonarcloud.io`: every call this server makes is a **v1 `api/…` web service** on
the configured base host. The point of the project is a dependency tree small enough for one person
to audit — treat that as a hard constraint, not a preference.

## Hard rules

1. **Never transcribe SonarSource's official `sonarqube-mcp-server`.** It is source-available under
   the Sonar Source-Available Licence, not an OSS licence, and copying from it would put a licence
   this repository does not carry on code this repository ships. The line is between *facts about
   the API* and *someone's expression of them*. Endpoint paths, parameter names, accepted values,
   response field names and status-code behaviour are facts — they come out of
   `api/webservices/list`, the public documentation and live probes, and they are fine. Their
   phrasing is not: no code, no comments, no error strings, no tool names or descriptions, no result
   record shapes and no documentation prose may be lifted, adapted or paraphrased from that project.
   Everything in this repository was written against the API itself and against the template this
   repository is derived from. If a sentence's provenance is uncertain, rewrite it from the API
   evidence rather than leave it.
2. **No new NuGet packages without a decision recorded in this file.** The *Package budget* below is
   complete. If a package looks necessary, add a row to *Package budget changes* with the date, the
   package, and why nothing already present can do the job — before referencing it.
3. **Never hand-edit `.github/workflows/build.yml` or `.github/workflows/publish.yml`.** Both are
   generated from the two `[GitHubActions]` attributes in `build/Build.CI.GitHubActions.cs` and
   regenerating overwrites edits. Change the attribute and re-run the build (any run regenerates;
   `dotnet fallout --generate-configuration GitHubActions_publish --host GitHubActions` does just
   the one). A run whose generated output differs *aborts* with `Configuration files for
   GitHubActions (…) have changed` — run it again and commit the regenerated YAML.
   (`.github/workflows/release.yml` is hand-written by design — the generator has no matrix
   support — and *is* edited directly.)
4. **No `Console.Write*` outside `src/SonarQube.Mcp/Cli/`.** In server mode stdout *is* the MCP
   protocol channel; a stray write corrupts the JSON-RPC stream. Logging goes to stderr
   (`AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`). Only the CLI modes
   (`status`, `version`, `help`), which never speak the protocol, may write to stdout. A test
   enforces this.
5. **`.gitignore` must never contain a bare `build` rule.** The Fallout build project lives in
   `build/`; a stock Visual Studio .gitignore silently untracks the entire orchestrator.
6. LF line endings everywhere (`.gitattributes` enforces it). `TreatWarningsAsErrors` is on —
   the compiler and analyzers are the lint step.

## Design decisions (D1–D26)

| # | Decision |
|---|---|
| D1 | **One production project** `src/SonarQube.Mcp` (Exe) + one test project. Testability via `InternalsVisibleTo`. |
| D2 | Binary `sonarqube-mcp` (`AssemblyName`), `RootNamespace SonarQube.Mcp`. |
| D3 | **Bare `ServiceCollection`**, not `Host.CreateApplicationBuilder` — the SDK registers `McpServer` as a singleton; `provider.GetRequiredService<McpServer>().RunAsync()` (this is the SDK's own AOT test app shape). Drops config providers/metrics/lifetime from cold start. `PosixSignalRegistration` for SIGINT/SIGTERM. |
| D4 | **Hand-rolled retry `DelegatingHandler`** (~90 lines) — `Microsoft.Extensions.Http.Resilience` would pull in Polly (third-party) plus six more packages. Retries 429/408/5xx with exponential backoff, honours `Retry-After`, and resends only content the request can replay. |
| D5 | No `IHttpClientFactory` — one singleton `HttpClient` over a hand-built handler chain (auth, then retry, then the transport). |
| D6 | **`SonarToolJsonContext` goes first** in `TypeInfoResolverChain` (chain cleared, ours added, the SDK resolver second, then `MakeReadOnly()`), so JIT and AOT resolve identically. `SonarWireJsonContext` — the API's own shapes — is **never** chained into the MCP options: the wire vocabulary and the tool vocabulary are different languages and a shared resolver would let one leak into the other. |
| D7 | `JsonSerializerIsReflectionEnabledByDefault=false` in the server **and** the test csproj — a missing `[JsonSerializable]` then fails in `dotnet test`, not only after an AOT publish. |
| D8 | **Static tool methods**; `SonarApiClient` and `SonarQubeMcpOptions` as plain parameters 0 and 1 (DI-bound via `IServiceProviderIsService`, excluded from the schema); avoids per-call instance activation and is directly unit-testable. |
| D9 | No `RuntimeIdentifiers` in the csproj (would pull ILCompiler packs per RID on every restore); the RID list lives in `Build.cs` and the release matrix, and reaches publish via `-r`. |
| D10 | No `PublishSingleFile` (ignored under AOT). |
| D11 | **xunit.v3 + `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`** (VSTest bridge) so the stock Fallout `ITest` component works unmodified. No mocking or assertion libraries. |
| D12 | **Environment variables only, and `FromEnvironment` never throws.** One `SonarQubeMcpOptions` record, read once in `RunStdioAsync`; a malformed or out-of-range value falls back to the documented default and is reported through the logger (stderr) rather than by failing startup. A server that refuses to start leaves the MCP client with a dead process and no channel to complain on — stdout is the protocol. There is no config file, no `Microsoft.Extensions.Configuration` and no command-line configuration. |
| D13 | **A static user token, and nothing else.** No OAuth, no device flow, no token cache, therefore no `login`/`logout` and no `System.Security.Cryptography.ProtectedData` (the template's fourth package). The token is attached as `Authorization: Bearer` **per request** by `AuthenticationHandler` and never set on `DefaultRequestHeaders`, so a request the pipeline issues for its own reasons cannot carry the credential by default. A 401 is not retried: a static token that was rejected once will be rejected again. |
| D14 | **`SONARQUBE_URL` is allowlisted, not merely parsed**: `https` only, and the host must be `sonarcloud.io`, `www.sonarcloud.io` or `sonarqube.us`. Anything else falls back to the default (D12) and logs a warning. This is what keeps "Cloud only" a property of the binary rather than a sentence in a README. |
| D15 | **CLI modes on the same binary** (argv dispatch, hand-rolled parsing): `serve` (the default with no args), `status`, `version`, `help`. `Cli/` may use stdout; server mode never does. `status` is the whole support surface — it prints what the server would use and probes the credential. |
| D16 | **Redirects are not followed at all.** The transport sets `AllowAutoRedirect = false` and a 3xx becomes a `SonarApiException` naming the `Location`. No `api/` endpoint on Cloud is expected to redirect, and `SocketsHttpHandler` strips `Authorization` on every automatic redirect anyway — so a followed redirect would either be an anonymous request presented as an authenticated one, or a credential sent somewhere the allowlist never approved. Loud beats silent. |
| D17 | **NuGet is a second distribution channel, not the primary one.** `dotnet pack` produces `sonarqube-mcp`, a framework-dependent .NET tool package (`PackAsTool`) carrying `PackageType=McpServer` and an embedded `.mcp/server.json`, so `dnx sonarqube-mcp@{version}` works without a download step; the Native AOT binaries stay the recommended way to run the server. It is pushed by **trusted publishing** — the workflow exchanges its GitHub OIDC token for an API key that lives minutes — so no NuGet API key exists in the repository or in GitHub secrets. The exchange is **C# inside the build** (`build/Build.Publish.cs`), not a marketplace action. It has its own generated workflow file, `.github/workflows/publish.yml`, triggered by `v*.*.*` tags only. |
| D18 | **Paging is `p`/`ps`, and there are no cursors.** SonarQube's paging is a pair of integers; there is nothing opaque to encode, so inventing a cursor would add an SSRF surface (a model-supplied URL to validate) for no benefit. `ToolDefaults.ResolvePaging` clamps `pageSize` to `SONARQUBE_MCP_MAX_PAGE_SIZE` silently and enforces the API's 10,000-result cap **before the request leaves the process**, turning `Can return only the first 10000 results` into an instruction to narrow the search. Every paginated result carries the same envelope: `page`, `pageSize`, `totalCount`, `hasMore`, `note?`. |
| D19 | **`organization` comes from `SONARQUBE_ORG` and is never a tool parameter.** One organization per server instance is the normal case; a model cannot discover an org key it was not told, and a wrong one is an opaque 400. Two organizations means two server entries in the client configuration, which is one line of JSON. `ToolDefaults.RequireOrganization` throws an `McpException` naming the variable and explaining that it is the `/organizations/{key}` segment of the URL. |
| D20 | **`projectKey` is optional everywhere, defaulted from `SONARQUBE_MCP_DEFAULT_PROJECT`.** An MCP server is configured per checkout in practice, and the Sonar project key is not guessable from the directory name — pinning it in the environment removes the single most common source of 404s. Neither set is an `McpException` naming the variable and pointing at `listProjects`. |
| D21 | **`component` accepts a path or a key.** `ToolDefaults.ResolveComponent` takes `src/Widget.cs`, `src\Widget.cs`, `./src/Widget.cs` or the full `proj:src/Widget.cs` and normalises to the key form; `ResolveComponentPath` does the inverse for `searchHotspots`, whose `files` parameter takes the project-relative path (C12). The model has file paths, never component keys, and the failure mode of getting it wrong is an empty result rather than an error. |
| D22 | **The MQR taxonomy is the only vocabulary at the tool boundary.** `issueStatuses` (`OPEN, CONFIRMED, FALSE_POSITIVE, ACCEPTED, FIXED`), `impactSeverities` (`INFO, LOW, MEDIUM, HIGH, BLOCKER`) and `impactSoftwareQualities` (`MAINTAINABILITY, RELIABILITY, SECURITY`). The legacy spellings a model reaches for — `MINOR`/`MAJOR`/`CRITICAL`, `BUG`/`CODE_SMELL`/`VULNERABILITY`, `REOPENED`/`RESOLVED`/`CLOSED` — are **rejected with the mapping in the message**, not silently translated. Translating would hide which vocabulary the caller is in; the API's own 400 lists the accepted values without saying which one means what was asked for. |
| D23 | **Read-only mode is the absence of a registration.** `SONARQUBE_MCP_READ_ONLY` makes `McpServerSetup.ToolTypesFor` return three tool classes instead of four, so the four write tools never appear in `tools/list`. It is not a check inside a tool: a model must not be able to propose a call the server would then refuse. `SmokeTest`'s second leg is the end-to-end proof. |
| D24 | **Writes are `application/x-www-form-urlencoded` `ByteArrayContent`**, built by `Http/FormBody.cs` — deliberately not `FormUrlEncodedContent`. `RetryHandler.IsResendable` recognises `ByteArrayContent`, which is exactly what makes a write replayable after a 429. SonarQube rejects a JSON body and ignores query parameters on these endpoints, so this is also the only encoding that works. |
| D25 | **Measures are strings, absence is not zero, and ratings are letters.** Every measure value arrives as a string, including for `INT` and `PERCENT`; new-code numbers live in `periods[0].value` and never in `value`; a metric with no data is *silently omitted* from the response. `MeasureFormatting` therefore lifts `newCodeValue` out of the period, translates `*_rating` `"1.0"`–`"5.0"` to `A`–`E`, and diffs what was asked for against what came back into `missingMetrics`. A model reading an absent `coverage` as 0% instead of "not measured" draws the opposite conclusion from the truth. |
| D26 | **Rule and hotspot prose is HTML, converted by hand, and truncation is visible.** `getRule` reads `rules/search?rule_key=…&f=descriptionSections` (C1 — `rules/show` has no `f` on Cloud), and `HtmlToText` (~90 lines, no dependency) unescapes entities, fences `<pre><code>`, bullets `<li>`, breaks on `<p>`/`<h*>`/`<br>` and drops the rest. Sections are capped at `ToolDefaults.MaxRuleSectionChars` with a visible marker and a `truncated` flag on the result. A response with no sections at all is reported as a `note` about entitlement, not as an error — see *API gotchas*. |

Five tools that could plausibly exist do not, and the reasons are part of the design rather than an
oversight. They are **v1.1 candidates**, not rejections:

- **`searchRules`** — browsing the rule catalogue is not a coding-agent task. The *endpoint* is
  used, by `getRule`. A model that wants "all the rules about nullability" is better served by
  `searchIssues` with `tags` or `rules` against the code it actually has.
- **`getRawSource`** — the agent has the file on disk and reading it is free. The endpoint also
  needs the *See Source Code* permission and answers 404 rather than 403 without it, so the tool
  would spend a call to produce a confusing error. Leaving it out also keeps every response in the
  client JSON, with no plain-text path.
- **`getDuplications`** — `duplicated_lines_density` through `listComponentMeasures` answers "where
  is the duplication" at file granularity, which is enough to act on. The block detail needs a
  `_ref`-into-`files` join for a question asked about once a quarter.
- **`listProjectAnalyses`** — `listBranches` already carries `analysisDate` and `getMeasuresHistory`
  already carries the trend. Only VERSION events are unique to it.
- **`getScmInfo`** — positional-array JSON (`[[line, author, date, revision], …]`) would need the
  codebase's only hand-written array converter, to answer a question `git blame` answers instantly
  and for free.

Other locked choices worth restating. Tool names are **camelCase verbNoun**
(`getQualityGateStatus`) via `[McpServerTool(Name = …)]`, with a sentence-case `Title`. All nineteen
are `OpenWorld = true` and `UseStructuredContent = true`. `Destructive` defaults to **true** in the
SDK, so a non-destructive write must set it explicitly `false`, and a read tool must not set it at
all (`ReadOnly` already says it changes nothing, and `ToolInventoryTests` asserts the hint's
*absence*). Never call `WithToolsFromAssembly()` (IL2026) — one `WithTools<T>(jsonOptions)` per tool
class. There is exactly **one** `JsonConverter` in the codebase, `SonarDateTimeOffsetConverter`,
because SonarQube sends `2026-08-10T20:26:06+0000` and `System.Text.Json`'s reader wants `+HH:mm`.
And every result record carries a `url` that is **derived, not fetched**: SonarQube returns no web
link for an issue, hotspot, project or file, but all of them are composable from the base URL, so
`ComponentKeys` composes them once.

## Tool table

The design's tool inventory, in full. `ToolInventoryTests` asserts this set of names and every one
of these four flags against what an MCP client actually receives, `Build.cs`'s `ExpectedToolNames`
asserts the names again over a real `tools/list` in `SmokeTest`, and `AgentSkillTests` asserts that
the shipped skill names them too.

`Destructive` is blank on the fifteen read tools deliberately: `ReadOnly` already says they change
nothing, so the SDK omits the hint and the test asserts its *absence*. On a write tool it is never
blank — the SDK's default is `true`, so a non-destructive write that stays silent tells clients to
prompt before every comment.

| Tool | Class | ReadOnly | Destructive | Idempotent | OpenWorld |
|---|---|---|---|---|---|
| `listProjects` | ProjectReadTools | true | — | true | true |
| `listComponents` | ProjectReadTools | true | — | true | true |
| `listBranches` | ProjectReadTools | true | — | true | true |
| `listPullRequests` | ProjectReadTools | true | — | true | true |
| `getQualityGateStatus` | ProjectReadTools | true | — | true | true |
| `searchIssues` | IssueReadTools | true | — | true | true |
| `getIssue` | IssueReadTools | true | — | true | true |
| `getRule` | IssueReadTools | true | — | true | true |
| `searchHotspots` | IssueReadTools | true | — | true | true |
| `getHotspot` | IssueReadTools | true | — | true | true |
| `getComponentMeasures` | MeasureReadTools | true | — | true | true |
| `listComponentMeasures` | MeasureReadTools | true | — | true | true |
| `getMeasuresHistory` | MeasureReadTools | true | — | true | true |
| `listMetrics` | MeasureReadTools | true | — | true | true |
| `getFileCoverage` | MeasureReadTools | true | — | true | true |
| `transitionIssue` | IssueWriteTools | false | **false** | **false** | true |
| `assignIssue` | IssueWriteTools | false | **false** | true | true |
| `addIssueComment` | IssueWriteTools | false | **false** | false | true |
| `setHotspotStatus` | IssueWriteTools | false | **false** | true | true |

Four of those rows are judgement calls rather than readings of the API. The same arguments are in
`IssueWriteTools`' class-level `<remarks>`, where they are read by whoever changes the annotations:

- **`transitionIssue` is not destructive.** Every transition it exposes is reversible — `reopen`
  undoes `resolve`, `falsepositive`, `wontfix` and `accept`; `unconfirm` undoes `confirm` — nothing
  is deleted, and SonarQube records each change in the issue's changelog with the actor and the
  timestamp, so it is both undoable and auditable. The real objection is that marking an issue
  false-positive removes it from the organization's counts and can flip a quality gate; that is
  *shared-state visibility*, which is what `openWorldHint` already says, not the destructive update
  `destructiveHint` means. And a confirmation prompt in front of every `confirm` teaches a user to
  click through the prompts that matter. The description states plainly that the change is immediate
  and visible to the whole organization.
- **`transitionIssue` is not idempotent.** The caller names a *transition*, not an end state, and
  SonarQube rejects a transition that is not legal from the issue's current state. That rejection is
  information — it almost always means the issue is not in the state the model believed — so it is
  surfaced rather than swallowed. To make the failure recoverable rather than merely honest, the
  tool returns `availableTransitions` from the resulting state (re-reading the issue when the
  operation response did not carry them), and the 400 handler appends the pointer to `getIssue`.
- **`setHotspotStatus` is idempotent.** `hotspots/change_status` takes a target `status` plus
  `resolution` pair rather than a transition, so re-applying the same pair is a no-op. Note the one
  asymmetry, which the description states: a `comment` argument is appended *every* time, including
  on a no-op. **Unverified against a real token** — it cannot be probed anonymously. If the API
  turns out to answer 400 to a no-op, the fix is to swallow that specific 400, not to flip the
  annotation, because the caller named an end state and the end state is in place.
- **`assignIssue` is idempotent and `addIssueComment` is not.** Assigning the same person twice
  changes nothing; commenting twice makes two comments. Which is why the comment tool's description
  says never to retry a call whose outcome is unknown without reading the issue first.

## Adding a tool

A new tool has to be added in **five** places, or the build fails:

1. The *Tool table* above.
2. `ToolInventoryTests.ExpectedToolNames`, plus its annotation table.
3. `Build.cs`'s `ExpectedToolNames`, which `SmokeTest` checks against a live `tools/list`.
4. `.claude/skills/sonarqube-code-quality/SKILL.md`, in backticks, inside a playbook —
   `AgentSkillTests` fails on a tool the skill never mentions as well as on a name no tool answers
   to.
5. For a **write** tool only: `Build.cs`'s `WriteToolNames`, which is what the read-only leg of
   `SmokeTest` asserts the absence of. A write tool missing from that array is a write tool the
   read-only mode is never proven to remove.

## API gotchas

Recorded because each one cost a probe to find, and most of them fail *silently*. The `C` numbers
are the live-verification corrections from the design document; they are cited from the code and
from the fixture manifest.

**Shapes that are not what the catalogue says**

- **C1 — `api/rules/show` has no `f` parameter on Cloud.** Rule descriptions come from
  `rules/search?rule_key=…&f=descriptionSections`, which does declare it.
- **C5 — `api/webservices/list` is incomplete.** `hotspots/search` accepts `branch` and
  `pullRequest`, and `components/search` accepts `qualifiers`, though none of the three is listed.
  Verified by call, not by inference.
- **C11 — `api/sources/lines` is absent from the catalogue entirely**, with and without
  `include_internals=true`, yet answers 200. This is a missing *action*, not a missing parameter, so
  a drift test that looks the action up first throws rather than fails. The drift test is therefore
  a **subset** assertion with an explicit `UndocumentedButVerified` list, never an equality.
- **C7 — `assignee` means two different things.** `issues/search` returns a **login**
  (`ada@github`); `hotspots/search` returns an internal **UUID**; `hotspots/show` returns a login
  again. Per-endpoint, not per-entity — which is why the result records name them differently
  (`assignee` versus `assigneeId`) and the two hotspot shapes do not share a mapper.
- **C8 — `hotspots/show` spells its comments array `comment`, singular.** The obvious plural
  deserialises to `null` with no error, which is the worst failure mode available.
- **`hotspots/show` returns `component` and `project` as objects**, where `hotspots/search` returns
  them as strings. Two DTOs, two fixtures.
- **C12 — `hotspots/search`'s `files` takes the project-relative path**, not the component key.
  `files=proj:src/App/appsettings.json` matches nothing; `files=src/App/appsettings.json` matches.
  No error either way — an empty result. `ToolDefaults.ResolveComponentPath` is what makes the one
  tool that sends a path safe to call with either spelling.
- **`ComponentDto` needs both `id` and `uuid`.** Same value, two names: the measures endpoints send
  `id`, everything else sends `uuid`.

**Values and parameters**

- **C6 — `metrics/search` rejects `f=type`.** The accepted `f` values are
  `name, description, domain, direction, qualitative, hidden, decimalScale`; asking for `type` is a
  400. Omitting `f` altogether returns every field including `type` and `direction`, so `listMetrics`
  sends **no `f` at all**.
- **`measures/component_tree` caps `metricKeys` at 15** (its own `maxValuesAllowed`), while
  `measures/component` is asked for at most 25 by us. Exceeding it is a 400 that reads like a value
  error.
- **`measures/search_history`'s parameter is `metrics`, not `metricKeys`**, and its `total` counts
  **analyses**, not measures.
- **`additionalFields` on measures is `[metrics, periods]`** — `period`, singular, is a 400.
- **A metric with no data is silently omitted** from the response. Absent is not zero. This is what
  `missingMetrics` exists for.
- **New-code values live in `periods[].value`**, never in `value`. A tool that reads `value` reports
  the overall number as if it were the new-code number.
- **Measure values are always strings**, including for `INT` and `PERCENT`, and `*_rating` `"1.0"`
  means **A**.
- **`p * ps > 10000` is a 400 and `ps > 500` is a 400.** Both are enforced client-side (D18); the
  first is turned into an instruction to narrow the search.
- **`hotspots/change_status` rejects `ACKNOWLEDGED` on Cloud** (C2): `FIXED` and `SAFE` only. The
  search endpoint's `resolution` has the same two values, for a different reason, so the two
  validators are separate.
- **`FALSE-POSITIVE` (hyphen) is the legacy resolution while `FALSE_POSITIVE` (underscore) is the
  issue status.** Only the underscore form is exposed; the rejection message must not confuse them.
- **`branch` and `pullRequest` are mutually exclusive everywhere**, and `createdAfter` and
  `createdInLast` are mutually exclusive with each other. `pullRequest` is the SCM number as a
  string.
- **`api/projects/search` needs organization-admin rights.** `components/search` is the
  list-projects endpoint, and it works anonymously on a public organization.

**Errors and transport**

- **C3 — a 401 from a bad token has an empty body** (`Content-Length: 0`), while a 401 from no
  token at all carries the `{"errors":[{"msg":…}]}` envelope. Never dereference the envelope
  unconditionally; the error composer must produce a useful message from nothing.
- **C4 — `api/authentication/validate` answers `{"valid":true}` anonymously.** It is a token
  *rejection* probe, not a token *presence* probe, so `status` does not call it when no token is
  set, and additionally reads one project when an organization is configured — which is the only way
  to tell a bad token (401) from a bad organization (400/404) in one command.
- **`api/sources/*` answers 404, not 403, for a permission problem**, and additionally requires
  *See Source Code*. So a 404 there means either "wrong component key" or "the file is there and
  this token cannot read it", and the error message says both.
- **`hotspots/change_status` answers 204 with no body.** Nothing to deserialise;
  `setHotspotStatus` re-reads through `hotspots/show` and reports what SonarQube stored.
- **429 carries no rate-limit headers and SonarQube Cloud publishes no limits.** Back off blind and
  say so — the error quotes `Retry-After` when there is one and otherwise says to wait about a
  minute and cut the request rate.
- **POST bodies must be form-urlencoded** (D24) — not JSON, not query parameters.
- **Timestamps come back as `+0000`.** One converter, applied by attribute (D26's neighbour in
  *Other locked choices*).
- **`projectStatus.status = "NONE"` with empty conditions is normal**, not an error: it is what an
  unanalysed or ungated scope looks like, and the result carries a `note` saying so.
- **C9 — the `components/tree` `q`-versus-`strategy` trap does not reproduce under the default
  strategy.** A filename query with no `strategy` returned ten matches, not zero. The `scope`
  parameter is kept because it bundles `strategy` and `qualifiers` into one comprehensible choice,
  **not** because of that trap — do not repeat the claim in a tool description without re-verifying
  it. It may still hold for `strategy=children`.
- **`getRule` may return no sections at all.** Anonymously, `f=descriptionSections` came back empty
  with a `requiredEntitlements` field on the rule. That looks like an anonymous-access restriction
  rather than a parameter error, but it is **unproven**: the tool degrades to `sections: []` plus a
  `note` and still returns the name, impacts and clean-code attribute. Phase F proves or disproves
  it with a real token, and the changelog says which.

## Agent skill

`.claude/skills/sonarqube-code-quality/SKILL.md` is an [Agent Skill](https://agentskills.io)
shipped with the repository. It is the **only** copy — there is no second one under `.agents/`, and
none inside a plugin directory either. Everything that installs it installs *that* file:

- **Claude Code, in a checkout**: loaded as a project skill, because `.claude/skills/` is where
  project skills live. Nothing to install.
- **Claude Code, anywhere else**: the repository is its own plugin marketplace (below), and the
  plugin's `skills` path points back at the same file.
- **Every other tool**: `npx skills add lahma/sonarqube-mcp` and `gh skill install` both scan
  `.claude/skills/` in the source repository and write to whatever location the target agent uses,
  so no second location is needed here for Codex, Gemini CLI, Cursor or Copilot to install it.

### The plugin and its marketplace

`.claude-plugin/marketplace.json` lists exactly one plugin whose `source` is `"./"` — the
repository root. That is the load-bearing choice: **an installed plugin cannot reference files
outside its own root**, so any narrower source would have forced a second copy of `SKILL.md` or a
symlink (which a Windows checkout without `core.symlinks` silently turns into a text file). With
the root as the source, `.claude-plugin/plugin.json` names
`./.claude/skills/sonarqube-code-quality` directly.

The same manifest bundles the MCP server (`dnx sonarqube-mcp@{version} --yes`), so one
`/plugin install` delivers the skill and the server together. Two things about it are deliberate:

- **The `dnx` pin is explicit, never floating.** Claude Code keys plugin updates off the plugin
  `version`; a floating package id would let the server change under a user whose plugin version
  never moved, pairing one release's skill with another release's tool surface.
  `PluginManifestTests` asserts the pin, the plugin `version` and `CHANGELOG.md` are the same
  string, so releasing means bumping the changelog and nothing else.
- **The credential surface is derived, not restated.** `userConfig` declares one prompt per
  environment variable in `.mcp/server.json`, and the `env` block wires each one through
  `${user_config.…}`. The test asserts the two agree, including that a variable marked `isSecret`
  is marked `sensitive` — an unmarked secret is written to `settings.json` in the clear, and a
  mistyped placeholder is passed to the server as a literal credential rather than failing.

Validate both manifests with the official CLI, which is non-interactive:

```powershell
claude plugin validate . --strict          # the marketplace manifest
claude plugin marketplace add .            # resolves it; remove afterwards
```

### The budget of attention

Three surfaces teach this server, and they are paid for at three different rates. That is the whole
reason there are three of them:

- **`ServerInstructions`** is read by every session that attaches the server, whether or not any
  Sonar work happens. It stays at about ten lines, and it holds only what a client needs *before*
  the first call: what a project key is, that `branch` and `pullRequest` are exclusive, that absent
  is not zero, that ratings are letters, that the writes are immediate.
- **The skill's body** is paid only by a session that actually starts SonarQube work; its
  always-loaded part is the frontmatter `description` alone. So it holds the multi-call *order*, the
  recovery moves and the when-not-to-call rules — none of which a schema can carry, because each
  schema sees one tool.
- **Parameter descriptions** are paid per call, by the model that is already looking at that
  parameter. Anything that is only true of one argument belongs there and nowhere else.

Guidance in the wrong place is not merely untidy: it is either context spent in every session that
never needed it, or advice that arrives after the mistake. The skill must not restate the schemas or
the README — the schemas arrive with every session anyway, and the README is for humans.

`AgentSkillTests` is what stops the skill drifting — see *Testing*.

## Package budget

Complete, as of the initial implementation. Versions are centrally pinned in
`Directory.Packages.props`, with transitive pinning on.

- `src/SonarQube.Mcp`: `ModelContextProtocol` (pinned **exactly** `[2.1.0]`, because the AOT and
  serializer contract this server depends on — resolver chaining, schema generation, annotation
  defaults — is verified against that one version and a floating range would move it under a
  release), `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging.Console`.
  **Three, not the template's four**: `System.Security.Cryptography.ProtectedData` is deliberately
  absent, because a static token read from the environment (D13) means there is no token cache, and
  no token cache means nothing to encrypt at rest.
- `tests/`: `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`. No mocking or
  assertion libraries — use the hand-rolled `StubHttpMessageHandler`.
- `build/`: `Fallout.Common` + `Fallout.Components` 10.4.0 (the build project opts out of CPM).

SourceLink needs no package reference — it is in-SDK on .NET 8 and later.

### Package budget changes

_None yet._ The trusted-publishing exchange (D17) uses `Fallout.Common.Utilities.Net`, which arrives
with `Fallout.Common` 10.4.0 as a transitive dependency — no new `PackageReference` anywhere, and
nothing added to `src/` or `tests/`.

## Layout

```
.claude-plugin/             marketplace.json + plugin.json — the repository as its own Claude Code
                            plugin marketplace, source "./" (PluginManifestTests)
.claude/skills/             The shipped Agent Skill (one canonical copy; AgentSkillTests keeps its
                            tool references in step with the inventory)
.mcp/server.json            MCP server manifest (D17), packed into the NuGet package at
                            /.mcp/server.json; its version must match CHANGELOG.md (test-enforced)
src/SonarQube.Mcp/          One production project (D1); AssemblyName sonarqube-mcp
  Program.cs                Entry point — hands argv straight to the CLI dispatcher
  McpServerSetup.cs         DI wiring, JSON options (D6), tool registration incl. ToolTypesFor
                            (D23), stdio run loop
  ServerVersion.cs          Product name + version, read from the assembly (build stamps it)
  Cli/                      status / version / help (D15). The ONLY place stdout is legal
  Configuration/            SonarQubeMcpOptions.FromEnvironment() — the complete env-var surface,
                            and it never throws (D12)
  Authentication/           StaticTokenCredential + AuthenticationRequiredException (D13)
  Http/                     SonarApiClient, the handler chain (auth + retry), SonarRequestBuilder,
                            FormBody (D24), PagedResult, SonarApiException
  Http/Models/              Wire DTOs and SonarWireJsonContext (explicit [JsonPropertyName] on
                            every property; never chained into the MCP JSON options)
  Json/                     SonarDateTimeOffsetConverter — the one converter (+0000 timestamps)
  Tools/                    ProjectReadTools, IssueReadTools, MeasureReadTools, IssueWriteTools,
                            ToolDefaults, ToolErrors, ResultMapper, ComponentKeys,
                            MeasureFormatting, HtmlToText, ServerInstructions
  Tools/Models/             Result records and SonarToolJsonContext (camelCase)
tests/SonarQube.Mcp.Tests/  The single test project; internals visible via InternalsVisibleTo
build/                      The Fallout orchestrator (Build.cs, Build.Publish.cs,
                            Build.CI.GitHubActions.cs, ReleaseNotesParser.cs, SemVersion.cs) —
                            `build/` is a resolver convention, and `.gitignore` must never
                            untrack it
```

Everything a user can configure is an environment variable read in `Configuration/`; everything a
user can see is either a tool result shaped in `Tools/Models/` or an error composed in
`Tools/ToolErrors.cs`. Those three places are where a behaviour change becomes user-visible, so
they are the ones to keep `README.md` in step with.

## Build

The orchestrator is [Fallout](https://fallout.build) 10.4.0 (stable channel), the maintained
hard fork of NUKE. The CLI is pinned in `.config/dotnet-tools.json` as `fallout.globaltool`
(command `fallout`) and resolves `build/_build.csproj` by convention.

```powershell
.\build.ps1 Test          # restore, compile, run tests
.\build.ps1 SmokeTest     # AOT publish + two real stdio JSON-RPC handshakes against the binary
.\build.ps1 Pack          # the NuGet tool package (D17) into artifacts/packages
```

`PublishAot` is switched **off when no `RuntimeIdentifier` is set**. `dotnet pack` of a tool
package packs `$(PublishDir)`, so it runs a publish of the server project; left at `true` that
publish fails with *RuntimeIdentifier is required for native compilation* (D9 deliberately keeps
RIDs out of the csproj). The AOT publish always passes `-r`, so keying the property off
`RuntimeIdentifier` gives the release path AOT and the pack path a portable IL tool from the same
project file.

`CHANGELOG.md` is the **version authority**: its top section is parsed in `OnBuildInitialized`
and passed to the build as the version. The file is never mutated by the build, and the first
line must parse as a version header (`# 1.0.0`) — do not add a `# Changelog` title, it would
abort the build.

## Testing

```powershell
.\build.ps1 Test                                   # the whole suite, via the orchestrator
dotnet test tests\SonarQube.Mcp.Tests               # the same tests, without a publish
dotnet test tests\SonarQube.Mcp.Tests --filter "FullyQualifiedName~Measure"
```

xunit.v3 with the `xunit.runner.visualstudio` VSTest bridge (D11). No mocking or assertion
libraries: HTTP is faked with the hand-rolled `StubHttpMessageHandler`, the retry schedule runs on a
manual `TimeProvider`, and fixtures are embedded resources captured live from sonarcloud.io with
their provenance recorded in `Fixtures/MANIFEST.md` (JSON cannot carry comments, so the manifest is
where a capture's URL and date live). Because `JsonSerializerIsReflectionEnabledByDefault=false` is
set in the test csproj as well (D7), a type missing from a `JsonSerializerContext` fails under
`dotnet test` rather than only after an AOT publish.

Some rules are too easy to break silently to be left to review, so tests enforce them by reflection
or by scanning the source tree. Do not delete one to make a change pass:

- **The tool inventory matches the design table.** `ToolInventoryTests` walks `[McpServerTool]`
  methods and asserts the full set of names, each tool's `ReadOnly` / `Destructive` / `Idempotent` /
  `OpenWorld` against *Tool table* above — reading `tool.ProtocolTool.Annotations`, which is what a
  client receives, not the attribute — plus a `[Description]` on every tool method and every
  non-injected parameter. `Destructive` defaults to *true* in the SDK, so a new non-destructive
  write tool fails this test until it says so explicitly.
- **Read-only mode removes tools rather than refusing calls.** `ReadOnlyModeTests` drives
  `McpServerSetup.ToolTypesFor` — the production selection, not a copy — and asserts the write class
  is still fully constructible, so the mode cannot be faked by breaking it.
- **The schemas stay in the tool vocabulary.** `ToolSchemaTests` asserts our resolver is first in
  the chain and the options are read-only (D6), that `client`, `options` and `cancellationToken`
  never appear in a schema, that every property is camelCase and described, and that a paginated
  tool carries the whole envelope while an unpaginated one carries none of it.
- **`NoStdoutWritesTest`.** Scans the production sources for `Console.Write*` and fails on any
  outside `src/SonarQube.Mcp/Cli/` (hard rule 4). In server mode stdout is the protocol channel.
- **The parameters we send are parameters the API has.** `ApiParameterContractTests` drives every
  client method through the stub with every optional argument set, collects the query keys actually
  sent and asserts each is in a declared table — with a deliberately-bogus-parameter guard so the
  test cannot go vacuous. Its opt-in live half (`SONARQUBE_MCP_LIVE_TESTS=1`) checks that table
  against `api/webservices/list` as a **subset**, with an `UndocumentedButVerified` exception list
  for C5 and C11.
- **The shipped skill names the tools that exist, and all of them.** `AgentSkillTests` reads
  `.claude/skills/sonarqube-code-quality/SKILL.md` and cross-checks every backticked tool-shaped
  identifier against the reflected inventory, in both directions: a name no tool answers to fails,
  and so does a tool the skill never mentions. Tool *parameter* names are excluded by reflection
  rather than by a hand-kept list, so `sortByMetric` does not read as a missing tool. Nothing else
  loads that file, so without this it would keep advertising an older surface. The frontmatter is
  checked too: `name` must equal the directory name and satisfy the Agent Skills charset, and
  `description` must be present and within 1024 characters.
- **The plugin manifests still describe a working install.** `PluginManifestTests` asserts that the
  marketplace lists this repository as its one plugin with `source: "./"`, that the declared skill
  path still has a `SKILL.md` behind it, that the plugin `version` and the `dnx` pin are both
  `CHANGELOG.md`'s version, and that the credential surface matches `.mcp/server.json` placeholder
  for placeholder.
- **`.mcp/server.json` agrees with the version authority.** `McpServerManifestTests` reads the
  checked-in manifest, `CHANGELOG.md` and the server csproj from the repository root and asserts the
  manifest's `version` and its NuGet entry's `version` both equal the changelog's top version, and
  that its `identifier` equals `<PackageId>`. Nothing in the build reads the manifest back, so
  without this a released package would keep advertising an old version indefinitely.

`SmokeTest` is the end-to-end check the unit tests cannot be: it publishes the Native AOT binary,
spawns it, and drives a real `initialize` / `notifications/initialized` / `tools/list` exchange over
stdio — **twice**. The first leg runs with a clean environment and asserts `serverInfo.name` and all
nineteen tool names; the second sets `SONARQUBE_MCP_READ_ONLY=1` and asserts exactly the fifteen
read names with none of the four writes. Both legs run with no token configured, which also proves
the handshake completes without credentials. CI runs `Test` and `SmokeTest` on every push and pull
request.

## Release engineering

Releases are cut by pushing a `v*` tag, which starts **two independent workflows**:
`release.yml` (binaries + the GitHub Release) and `publish.yml` (nuget.org, D17 — *The NuGet leg*
below). They do not depend on each other, deliberately: the AOT binaries are the product, so a
nuget.org outage or a policy mismatch must not be able to hold up the GitHub Release, and neither
must the reverse.

`.github/workflows/release.yml` is hand-written (the `[GitHubActions]` generator has no matrix
support) and runs five publish legs — one per RID, each on its own hardware — followed by a
`release` job that assembles the GitHub Release:

| RID | Runner |
|---|---|
| `linux-x64` | `ubuntu-latest` |
| `linux-arm64` | `ubuntu-24.04-arm` |
| `win-x64` | `windows-latest` |
| `win-arm64` | `windows-11-arm` |
| `osx-arm64` | `macos-latest` |

Every leg runs `dotnet fallout SmokeTest --runtime <rid>`, and `SmokeTest` depends on `PublishAot`,
so each leg compiles its own binary, speaks JSON-RPC to it *on the architecture it targets*, and
only then uploads the archive. **Never ship a binary that has not answered a handshake on its own
architecture** — that is why the arm64 legs run on arm64 runners instead of being cross-compiled.
`fail-fast` is off so one broken runtime cannot mask the state of the other four.

`CHANGELOG.md` stays the version authority here too: `CreateGitHubRelease` refuses to publish
unless `GITHUB_REF_NAME` equals `v{version parsed from CHANGELOG.md}`. To release, land the
changelog entry first, then tag that commit. `Prerelease` follows the version string
(`Version.Contains('-')`), so a `1.0.0-rc.1` tag publishes as a prerelease and never displaces the
latest stable release.

Two properties of the release path are inherited from the template, where they were established by a
throwaway tag rather than reasoned about, and both still apply here:

- **R1 — arm64 runners are available and need no extra native-toolchain setup** on a public
  repository; both arm64 legs completed a full AOT publish plus handshake. If that ever regresses,
  the documented fallback is to cross-compile the affected RID from the x64 runner of the same OS
  with its smoke step skipped — and to mark it as unverified in both `release.yml` and here, because
  an unverified binary must not go out silently.
- **R2 — `CompressionExtensions.TarGZipTo` cannot be used.** It archives through SharpZipLib's
  `TarEntry.CreateEntryFromFile`, which hard-codes *every* entry's mode to `0700` instead of reading
  it off disk — shipping `LICENSE` and `README.md` executable and the binary unreadable to anyone
  but the extracting user. `PublishAot` therefore invokes the **`tar` CLI**
  (`ProcessTasks.StartProcess`), which is present on the GitHub runners and in Git Bash. Windows
  RIDs keep using `ZipTo`: a zip carries no Unix mode and the payload is a `.exe`.

The release body is composed from `artifacts/release-notes.md`, which `Build.WriteReleaseNotes`
reflows out of `CHANGELOG.md`'s newest section — one single-line bullet per entry — because
`ChangelogTasks.ExtractChangelogSectionNotes` only recognises `## ` headings and ends a section at
the first non-bullet line, and this changelog uses `#` headings with wrapped bullets. Keep changelog
entries as `- ` bullets with wrapped continuation lines and this keeps working.

### The NuGet leg

`.github/workflows/publish.yml` is **generated** from the second `[GitHubActions]` attribute in
`build/Build.CI.GitHubActions.cs` (hard rule 3 — never hand-edit it). One Ubuntu job runs
`dotnet fallout Compile Test Pack Publish` with `permissions: { contents: read, id-token: write }`,
`environment: nuget` and `env: NUGET_USER: lahma`.

Three shapes are load-bearing and were chosen, not defaulted:

- **Its own file.** A nuget.org trusted publishing policy is scoped by repository + workflow *file
  name* (+ environment) and the nuget.org UI has no branch or tag filter, so whichever file the
  policy names can mint a publishing key on *every* run of that file. `publish.yml` only ever runs
  on a `v*.*.*` tag, and `build.yml` — which runs on every push and PR — is not the file named in
  the policy and therefore cannot mint anything.
- **No `paths:` filter.** A path filter alongside `tags:` evaluates over the tag commit's diff and
  can silently skip the release run.
- **One job.** More jobs would mean more OIDC exchanges racing the one-key-per-30-s rate limit, and
  the target is Linux-only anyway.

`Publish` is gated on a tag build and refuses in ordered steps otherwise: a non-tag invocation is a
logged *skip* (there is no preview feed — the only push this repository makes is a tagged release),
and once past the gate it asserts, in order, that it is on GitHub Actions, that `NUGET_USER` is set,
and that `GITHUB_REF_NAME` equals `v{version from CHANGELOG.md}`. It then mints the key immediately
before pushing, because the key lives 15–60 minutes and one OIDC token mints exactly one key. The
whole ladder is exercisable locally:

```powershell
dotnet fallout Publish --skip                      # skips, and says why
$env:GITHUB_ACTIONS='true'; $env:NUGET_USER='lahma'; $env:GITHUB_REF_NAME='v1.0.0'
dotnet fallout Publish --skip                      # must fail with "GitHub OIDC is unavailable"
```

That last failure is the success signal: it proves the tagged path routed all the way to the token
exchange. Clear the three variables afterwards.

Three things about the exchange are worth not rediscovering:

- **The POST to `https://www.nuget.org/api/v2/token` must carry a `User-Agent`.** The endpoint sits
  behind Azure Front Door, which answers `400 {"error":"A User-Agent header is required."}` to a
  request without one, *before* looking at the token or the policy — and a bare `HttpClient` sends
  none. So **a 400 means the request never reached the policy**; stop looking at nuget.org settings.
  `Build.Publish.cs` sends `sonarqube-mcp-build/1.0`.
- The OIDC audience is `https://www.nuget.org` and the response field is `apiKey`. The minted key is
  `add-mask`ed, and Fallout already marks `DotNetNuGetPushSettings.ApiKey` as secret.
- `NUGET_USER` is the nuget.org **profile name of whoever created the trusted publishing policy**
  (`lahma`) — not an email, not an organisation. It is public information and is deliberately a
  plain workflow-level `env:` value, not a secret: a secret would be masked out of exactly the error
  message that names it. Getting it wrong is the most common 401.

Setting up the policy on nuget.org is manual and must exist **before** the first tag: Trusted
Publishing → policy owner `lahma`, Repository Owner `lahma`, Repository `sonarqube-mcp`, Workflow
File `publish.yml` (filename only, **not** `release.yml` and not the `.github/workflows/` path),
Environment `nuget`. A policy covers every package the selected owner owns; there is no per-package
scoping.
