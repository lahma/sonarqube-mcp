I have the full picture: the blueprint, the template repo's actual source, and live-verified SonarCloud behaviour (my probes turned up several corrections to the brief — flagged inline below).

---

# `sonarqube-mcp` — Implementation Design

## 0. Live-verification corrections to the brief (read these first)

I re-probed sonarcloud.io (Sonar-Version 13.9, anonymous, org `quartznet`) on 2026-08-11. Five findings change the design:

| # | Brief said | Live reality | Consequence |
|---|---|---|---|
| C1 | `api/rules/show` takes `f=descriptionSections` | `api/webservices/list` shows `rules/show` accepts **only** `actives`, `key`, `organization`. No `f`. | **`getRule` is built on `api/rules/search?rule_key=…&f=descriptionSections`**, which *does* declare `descriptionSections` in `f`'s `possibleValues`. |
| C2 | `hotspots/change_status` resolution accepts `ACKNOWLEDGED` | `possibleValues: ["FIXED","SAFE"]` only on Cloud | `setHotspotStatus` allows FIXED/SAFE only; error message says so. |
| C3 | 401 always returns `{"errors":[{"msg":…}]}` | **No token** → `{"errors":[{"msg":"Authentication is required"}]}`. **Bad token** → `401` with `Content-Length: 0`, empty body. | Error funnel must handle an empty 401 body without an NRE or a useless message. |
| C4 | `GET api/authentication/validate` is a token probe | Anonymous (no header) also returns `{"valid":true}` | `status` must not call it when no token is set, and must additionally probe a protected endpoint to prove the token *and* the org. |
| C5 | `webservices/list` is authoritative | `hotspots/search` accepts `branch` (verified 200 + a bogus branch yields "Project doesn't exist"), and `components/search` accepts `qualifiers`, though **neither is listed**. | The drift test asserts *our params ⊆ documented ∪ an explicit `UndocumentedButVerified` list*, never equality. |

Two more confirmed traps worth naming now: `measures/component_tree` declares `metricKeys` **`maxValuesAllowed: 15`**; and timestamps come back as `2026-08-10T20:26:06+0000` — **System.Text.Json's ISO-8601 reader requires `+HH:mm`, so this needs a custom converter** (the one converter in the codebase).

Also confirmed as designed: `additionalFields` on measures is `[metrics, periods]` (plural); new-code values arrive as `"periods":[{"index":1,"value":"2","bestValue":false}]`; `new_coverage` was **silently absent** from a response that requested it; issue `assignee` is a login (`lahma@github`) while hotspot `assignee` is a UUID (`AYgE5F7pEoXHSow6lKjD`) — *but see C7*; `qualitygates/project_status` on an ungated project is `{"status":"NONE","conditions":[],"periods":[]}`; `sources/lines` `code` is **syntax-highlighted HTML** (`<span class="cd">…`), not plain text.

### 0b. Phase B corrections — re-probed 2026-08-11 while capturing the golden fixtures

Six further findings, all verified against live sonarcloud.io (org `quartznet`, project
`quartznet_quartznet`, anonymous) or against the API's own `webservices/list` metadata. Every one is
already reflected in the Phase B code and in `tests/SonarQube.Mcp.Tests/Fixtures/MANIFEST.md`.

| # | This document said | Live reality | Consequence |
|---|---|---|---|
| C6 | `listMetrics` calls `metrics/search?ps=500&f=name,description,domain,type,hidden` (§1c #14, §5) | `f`'s `possibleValues` are `[name, description, domain, direction, qualitative, hidden, decimalScale]`. **`type` is not among them**, and the request answers `400 Value of parameter 'f' (type) must be one of: […]`. | **No `f` is sent at all.** Omitting it returns every field including `type` *and* `direction` (which is what `higherIsBetter` is derived from), so naming a subset buys nothing and costs the whole call. `SonarApiClient.SearchMetricsAsync` sends only `ps`, asserted by `SearchMetricsSendsNoFieldSelector`. |
| C7 | hotspot `assignee` is a UUID (§0, §10 #15) | True on `hotspots/**search**` (`AYgE5F7pEoXHSow6lKjD`). **On `hotspots/show` the same field is a login** (`lahma@github`), and `show` additionally carries a `users[]` sidecar. | The claim is per-endpoint, not per-entity. `HotspotSummary.assigneeId` (search) stays as designed; `HotspotDetail` must expose the value as a **login**, and the two must not share a mapper. |
| C8 | `hotspots/show` returns `comments[]` (§5) | The array is spelled **`comment`, singular**. | `HotspotShowResponseDto.Comment` carries `[JsonPropertyName("comment")]`. The obvious plural deserialises to `null` with no error, which is the worst possible failure mode. |
| C9 | `components/tree`'s `q` under the API default `strategy` returns 0 for a filename query (§1c #2, §10 #20) | `components/tree?component=quartznet_quartznet&q=QuartzScheduler&ps=2` — no `strategy`, so the documented default `all` applies — returned **10 matches**. | The `scope` parameter is still worth having (it bundles `strategy`+`qualifiers` into one comprehensible choice) but **the stated justification does not reproduce**. Phase C must re-verify before repeating the claim in a tool description; the trap may be specific to `strategy=children`, which is not the default. |
| C10 | `sources/lines` may 404 anonymously; capture it synthetically if so (§8) | It answers **200 anonymously** on a public project. `api/sources/**raw**` is the one that 404s. | `sources-lines.json` is a live capture. §10 #18's "sources/* answers 404 for a permission problem" still stands — it is `sources/raw` that demonstrates it. |
| C11 | `webservices/list` is the parameter catalogue (§8 item 8) | **`api/sources/lines` is absent from `webservices/list` entirely**, with *and* without `include_internals=true`, yet answers 200. | Phase D's drift test must carry `sources/lines:*` in `UndocumentedButVerified` — it is not a missing *parameter*, it is a missing *action*, so a test that looks the action up first will throw rather than fail. |

Two things the design expected to be unobtainable turned out to be capturable, and both fixtures are
therefore **live, not synthetic**: `qualitygates-project-status-error.json` (pull request 3267 was
failing its gate) and `sources-lines.json` (C10).

One more wire detail with no design row: `ComponentDto` needs **both `id` and `uuid`**. They are the
same value under two names — the issues and components endpoints send `uuid`, the measures
endpoints send `id`.

---

## 1. The definitive tool table

Nineteen tools across four `[McpServerToolType] internal sealed` classes. Names are camelCase verbNoun, `Title` sentence case, exactly as `[McpServerTool(Name=…, Title=…)]`. All are `OpenWorld = true` and `UseStructuredContent = true`. `Destructive` is **omitted** on read tools (the SDK then omits `destructiveHint`, and `ToolInventoryTests` asserts its *absence*); it is **never** omitted on a write tool, because the SDK default is `true`.

### 1a. Annotation table

| # | Tool | Class | ReadOnly | Destructive | Idempotent |
|---|---|---|---|---|---|
| 1 | `listProjects` | ProjectReadTools | true | — | true |
| 2 | `listComponents` | ProjectReadTools | true | — | true |
| 3 | `listBranches` | ProjectReadTools | true | — | true |
| 4 | `listPullRequests` | ProjectReadTools | true | — | true |
| 5 | `getQualityGateStatus` | ProjectReadTools | true | — | true |
| 6 | `searchIssues` | IssueReadTools | true | — | true |
| 7 | `getIssue` | IssueReadTools | true | — | true |
| 8 | `getRule` | IssueReadTools | true | — | true |
| 9 | `searchHotspots` | IssueReadTools | true | — | true |
| 10 | `getHotspot` | IssueReadTools | true | — | true |
| 11 | `getComponentMeasures` | MeasureReadTools | true | — | true |
| 12 | `listComponentMeasures` | MeasureReadTools | true | — | true |
| 13 | `getMeasuresHistory` | MeasureReadTools | true | — | true |
| 14 | `listMetrics` | MeasureReadTools | true | — | true |
| 15 | `getFileCoverage` | MeasureReadTools | true | — | true |
| 16 | `transitionIssue` | IssueWriteTools | false | **false** | **false** |
| 17 | `assignIssue` | IssueWriteTools | false | **false** | true |
| 18 | `addIssueComment` | IssueWriteTools | false | **false** | false |
| 19 | `setHotspotStatus` | IssueWriteTools | false | **false** | true |

**The four judgement calls, argued** (these go into AGENTS.md verbatim, the way bitbucket's two do):

- **`transitionIssue` is not destructive.** Every transition this tool exposes is reversible: `reopen` undoes `resolve`, `falsepositive`, `wontfix` and `accept`; `unconfirm` undoes `confirm`. Nothing is deleted, and SonarQube records every transition in the issue's changelog with the actor and timestamp, so it is both undoable and auditable. The counter-argument — that marking an issue false-positive removes it from the organization's counts and can flip a quality gate — is real, but it is *shared-state visibility*, which is what `openWorldHint` already says, not the "destructive update" `destructiveHint` means. And the bitbucket precedent applies exactly: a confirmation prompt in front of every `confirm` teaches a user to click through the prompts that matter. The `[Description]` states plainly that the change is immediate and visible to the whole organization.
- **`transitionIssue` is not idempotent.** The caller names a *transition*, not an end state, and SonarQube rejects a transition that is not available from the issue's current state. That rejection is information — it almost always means the issue is not in the state the model believed — so it is surfaced, not swallowed. (This is the deliberate inverse of bitbucket's `resolvePullRequestComment`, which swallows 409/404 precisely because there the caller named an *end state*.) To make the failure recoverable rather than merely honest, the tool re-reads the issue on success and returns `availableTransitions`, and the 400 handler appends the transitions that *were* available.
- **`setHotspotStatus` is idempotent.** `hotspots/change_status` takes a target `status`+`resolution` pair, not a transition, so re-applying the same pair is a no-op. **Implementers must verify this with a real token** (it cannot be probed anonymously); if the API answers 400 to a no-op, the tool swallows that specific 400 — the bitbucket `resolvePullRequestComment` pattern — rather than flipping the annotation. Note that a `comment` argument *is* appended each time; the description says so.
- **`assignIssue` is idempotent** and **`addIssueComment` is not**: two comment calls make two comments, and that is the same reasoning as `addPullRequestComment`.

### 1b. Cross-cutting parameter conventions

Three shared shapes, so nineteen tools cannot drift into nineteen opinions:

- **`projectKey`** — `string?`, optional on every tool, resolved by `ToolDefaults.ResolveProject(projectKey, options)` which falls back to `SONARQUBE_MCP_DEFAULT_PROJECT` and otherwise throws an `McpException` naming that variable. Description on every occurrence: *"The Sonar project key (for example `myorg_myrepo`) — the `id` parameter in a sonarcloud.io project URL, not the repository name. Optional when SONARQUBE_MCP_DEFAULT_PROJECT is set; call listProjects to find it."*
- **`component`** — `string?` wherever a sub-project scope is meaningful. Accepts **either** a full component key (`proj:src/Widget.cs`) **or** a repository-relative path (`src/Widget.cs`, `src\Widget.cs`, `./src/Widget.cs`), which `ToolDefaults.ResolveComponent` normalises (backslashes → slashes, leading `./` stripped) and prefixes with the resolved project key. A value containing `:` or equal to the project key is used verbatim. This is the single most important ergonomic decision in the design: the model has file paths, never component keys.
- **`branch` / `pullRequest`** — both `string?`, mutually exclusive, validated once by `ToolDefaults.ResolveScope`. Omitting both means the main branch. `pullRequest` is the SCM pull-request number as a string (`"3266"`), which is what `listPullRequests` returns as `key`.
- **`page` / `pageSize`** — see §4.

`organization` is **never** a tool parameter (§1e).

### 1c. Read tools in full

Types are C#; `[]` denotes `string[]?`. Every parameter carries a `[Description]`; the one-liners below are the intent those descriptions carry.

---

**1. `listProjects`** — "List projects" — `GET api/components/search?organization={SONARQUBE_ORG}&qualifiers=TRK`

| Param | Type | Req | Default | Intent |
|---|---|---|---|---|
| `query` | string? | no | — | Substring match on project key and name; omit to list all. |
| `page` | int? | no | 1 | 1-based page. |
| `pageSize` | int? | no | 50 | Clamped 1–100. |

Result `ProjectListResult { projects: ProjectSummary[], page, pageSize, totalCount, hasMore, note? }`, `ProjectSummary { key, name, qualifier, url }`.
*Why `components/search` and not `projects/search`*: `api/projects/search` needs org-admin and 401s otherwise; `components/search` works anonymously on public orgs. Recorded in AGENTS.md.

---

**2. `listComponents`** — "List components" — `GET api/components/tree`

| Param | Type | Req | Default | Intent |
|---|---|---|---|---|
| `projectKey` | string? | no | env | The project to walk. |
| `query` | string? | no | — | Filename/path substring. |
| `scope` | string? | no | `"files"` | `files` (→`strategy=leaves&qualifiers=FIL,UTS`), `directories` (→`children`+`DIR`), `all`. |
| `branch` / `pullRequest` | string? | no | — | Mutually exclusive. |
| `page`, `pageSize` | int? | no | 1 / 50 | |

Result `ComponentListResult { components: ComponentSummary[], page, pageSize, totalCount, hasMore }`, `ComponentSummary { component, path, name, qualifier, language?, url }`.
**Trap encoded in the design**: `q` only matches *within* the chosen strategy, so a filename query under the API default `strategy=all` returns 0. That is exactly why this tool exposes `scope` with a `files` default that maps to `strategy=leaves` rather than exposing `strategy` raw.

---

**3. `listBranches`** — "List branches" — `GET api/project_branches/list` (no paging on the API)

`projectKey?`. Result `BranchListResult { branches: BranchSummary[] }`, `BranchSummary { name, isMain, type, analysisDate, bugs, vulnerabilities, codeSmells, qualityGateStatus?, commitSha?, commitMessage?, url }`.

---

**4. `listPullRequests`** — "List pull requests" — `GET api/project_pull_requests/list` (no paging)

`projectKey?`. Result `PullRequestListResult { pullRequests: PullRequestSummary[] }`, `PullRequestSummary { key, title, branch, base, qualityGateStatus, bugs, vulnerabilities, codeSmells, analysisDate, scmUrl, url }` — `key` is documented as *the value to pass back as `pullRequest`*.

---

**5. `getQualityGateStatus`** — "Get quality gate status" — `GET api/qualitygates/project_status`

`projectKey?`, `branch?`, `pullRequest?`.
Result `QualityGateResult { projectKey, branch?, pullRequest?, status, conditions: QualityGateCondition[], failingConditions: QualityGateCondition[], url, note? }`; `QualityGateCondition { metric, status, comparator, threshold, actualValue, onNewCode }` where `onNewCode = periodIndex == 1`.
`status: "NONE"` with no conditions sets `note` to *"No quality gate has been computed for this scope yet — this is normal for a project or pull request that has not been analysed against a gate, not an error."* `failingConditions` is a pre-filtered copy so the model does not have to scan.

---

**6. `searchIssues`** — "Search issues" — `GET api/issues/search`

| Param | Type | Req | Default | Intent |
|---|---|---|---|---|
| `projectKey` | string? | no | env | Scope. |
| `component` | string? | no | — | Narrow to a directory or file; path or full key. Becomes `componentKeys`. |
| `branch` / `pullRequest` | string? | no | — | Mutually exclusive. |
| `issueStatuses` | string[]? | no | `["OPEN","CONFIRMED"]` | `OPEN, CONFIRMED, FALSE_POSITIVE, ACCEPTED, FIXED`. Default is the open work; pass a list to widen. |
| `impactSeverities` | string[]? | no | — | `INFO, LOW, MEDIUM, HIGH, BLOCKER`. |
| `impactSoftwareQualities` | string[]? | no | — | `MAINTAINABILITY, RELIABILITY, SECURITY` — description teaches the mapping to the legacy CODE_SMELL / BUG / VULNERABILITY vocabulary. |
| `rules` | string[]? | no | — | Rule keys (`csharpsquid:S2259`). |
| `tags` | string[]? | no | — | |
| `languages` | string[]? | no | — | `cs`, `java`, `js`… |
| `assignees` | string[]? | no | — | Logins (`__me__` is accepted by the API). |
| `createdAfter` | string? | no | — | `YYYY-MM-DD` or ISO datetime. Mutually exclusive with `createdInLast`. |
| `createdInLast` | string? | no | — | `7d`, `1m`, `1y`. |
| `inNewCodePeriod` | bool? | no | — | → `sinceLeakPeriod`. |
| `sortBy` | string? | no | `CREATION_DATE` | `CREATION_DATE, UPDATE_DATE, CLOSE_DATE, SEVERITY, STATUS, ASSIGNEE, FILE_LINE`. |
| `ascending` | bool? | no | `false` | Newest/worst first by default, mirroring bitbucket's `-updated_on`. |
| `page`, `pageSize` | int? | no | 1 / 50 | |

Sends `additionalFields=rules` (never `_all`, which drags a 50-element `languages` array).
Result `IssueSearchResult { issues: IssueSummary[], page, pageSize, totalCount, hasMore, note? }`.
`IssueSummary { key, rule, ruleName?, message, file, line?, endLine?, component, project, issueStatus, impacts: Impact[], severity?, type?, cleanCodeAttribute?, cleanCodeAttributeCategory?, tags[], assignee?, effort?, creationDate, updateDate, url }`.
**`file` is the joined path.** The mapper builds a `key → path` dictionary from the sibling `components[]` array and resolves `issue.component` through it, falling back to the substring after the first `:`. This is the biggest single context saving available: the model never sees `quartznet_quartznet:src/…/CronExpressionConverter.cs`, it sees `src/…/CronExpressionConverter.cs` plus `component` if it needs the key back.

---

**7. `getIssue`** — "Get issue" — `GET api/issues/search?issues={key}&additionalFields=transitions,comments,rules,users&ps=1`

`issueKey` (**required**), `branch?`, `pullRequest?`.
Result `IssueDetail` = `IssueSummary` + `{ availableTransitions[], comments: IssueComment[], flows: IssueFlow[], textRange?, ruleName }`.
`availableTransitions` is what makes `transitionIssue` usable without guessing; the tool description of `transitionIssue` points here. An empty `issues[]` becomes an `McpException` naming the key and suggesting `searchIssues` with a branch/pullRequest scope.

---

**8. `getRule`** — "Get rule" — `GET api/rules/search?organization={ORG}&rule_key={key}&f=descriptionSections,name,cleanCodeAttribute,impacts,repo,langName,securityStandards,tags&ps=1` (**C1**)

| Param | Type | Req | Default | Intent |
|---|---|---|---|---|
| `ruleKey` | string | **yes** | — | `csharpsquid:S2259`, as `searchIssues` reports it. |
| `sections` | string[]? | no | `["introduction","root_cause","how_to_fix"]` | Which description sections to return: `introduction, root_cause, assess_the_problem, how_to_fix, resources`. |

Result `RuleDetail { key, name, language, repository, type?, severity?, cleanCodeAttribute?, cleanCodeAttributeCategory?, impacts[], tags[], securityStandards[], sections: RuleSection[], truncated, url }`; `RuleSection { key, content, context? }`.
**HTML handling**: section `content` is HTML. A hand-rolled `HtmlToText` converter (~90 lines, no dependency) unescapes entities, turns `<pre><code>` into fenced blocks, `<li>` into `- `, `<h*>`/`<p>`/`<br>` into line breaks, and drops everything else. Each section is capped at `ToolDefaults.MaxRuleSectionChars = 4000` with a visible `… [truncated]` marker and `truncated = true` on the result — truncation is never silent.
**Known risk (verified)**: anonymously, `f=descriptionSections` returns *no* sections and the rule carries a `requiredEntitlements` field. This is very likely an anonymous-access restriction rather than a parameter error, but **it is unproven**. `getRule` must therefore degrade gracefully: no sections → `sections: []` and a `note` saying the token may lack the entitlement to read rule descriptions, with the rule's name, impacts and clean-code attribute still returned. Phase (f) proves or disproves this with a real token; if descriptions turn out to be unavailable to ordinary tokens, `getRule` still earns its place (name + impacts + effort) but the changelog must say so.

---

**9. `searchHotspots`** — "Search security hotspots" — `GET api/hotspots/search`

`projectKey?`, `component?` (→ `files`), `status?` (`TO_REVIEW`|`REVIEWED`), `resolution?` (`FIXED`|`SAFE` — **the search endpoint rejects `ACKNOWLEDGED`**), `onlyMine?`, `inNewCodePeriod?`, `branch?`, `pullRequest?` (**C5**: both undocumented but verified), `page?`, `pageSize?`.
Result `HotspotSearchResult { hotspots: HotspotSummary[], page, pageSize, totalCount, hasMore }`; `HotspotSummary { key, file, line?, message, securityCategory, vulnerabilityProbability, status, resolution?, ruleKey, assigneeId?, creationDate, updateDate, component, url }`.
The field is named **`assigneeId`, not `assignee`** — because on this endpoint it is a UUID, unlike the login `searchIssues` returns, and the description says exactly that. Same `components[]` join as issues.

---

**10. `getHotspot`** — "Get security hotspot" — `GET api/hotspots/show?hotspot={key}` (the param is `hotspot`, not `hotspotKey`)

`hotspotKey` (required).
Result `HotspotDetail { key, file, line?, message, status, resolution?, securityCategory, vulnerabilityProbability, rule: { key, name, riskDescription, vulnerabilityDescription, fixRecommendations }, comments[], changelog[], canChangeStatus, url }`. The three rule prose fields go through the same `HtmlToText` + cap. `canChangeStatus` is what `setHotspotStatus` reads before a 403.
**Shape warning for implementers**: `hotspots/show` returns `component` and `project` as *objects*, unlike `hotspots/search` where they are strings. Model them as separate DTOs and capture a live fixture rather than reusing `HotspotDto`.

---

**11. `getComponentMeasures`** — "Get measures" — `GET api/measures/component?additionalFields=periods`

`projectKey?`, `component?` (defaults to the project), `metricKeys` (**required**, string[], capped at 25), `branch?`, `pullRequest?`.
Result `MeasuresResult { component, path?, projectKey, branch?, pullRequest?, measures: MeasureValue[], missingMetrics: string[], url }`; `MeasureValue { metric, value, newCodeValue?, bestValue? }`.
Three things the mapper does that the API does not:
1. **`missingMetrics`** — the diff between what was asked for and what came back. The API silently omits a metric with no data; a model reading an absent `coverage` as "no coverage configured" versus "0% coverage" is the difference between two opposite conclusions.
2. **`newCodeValue`** — lifted out of `periods[0].value`, because new-code numbers never arrive under `value`.
3. **`*_rating` values translated to `A`–`E`.** `"1.0"` meaning A is the most confusing single thing in the Sonar API. Documented on the record and in the tool description.

---

**12. `listComponentMeasures`** — "List measures by component" — `GET api/measures/component_tree`

`projectKey?`, `component?` (subtree root), `metricKeys` (**required**, **max 15 — the API's own `maxValuesAllowed`**), `scope?` (`files`/`directories`/`all` → `strategy`+`qualifiers`), `sortByMetric?` (→ `s=metric&metricSort=X&metricSortFilter=withMeasuresOnly`), `ascending?` (default `false`), `query?`, `branch?`, `pullRequest?`, `page?`, `pageSize?`.
Result `ComponentMeasuresResult { components: [{ component, path, name, qualifier, measures: MeasureValue[] }], page, pageSize, totalCount, hasMore, missingMetrics[] }`.
This is the "worst files by `<metric>`" tool and the reason `sortByMetric` exists as a first-class parameter instead of three raw ones.

---

**13. `getMeasuresHistory`** — "Get measures history" — `GET api/measures/search_history`

`projectKey?`, `component?`, `metrics` (**required** — the parameter really is `metrics`, not `metricKeys`), `from?`, `to?`, `branch?`, `pullRequest?`, `page?`, `pageSize?`.
Result `MeasuresHistoryResult { component, metrics: [{ metric, history: [{ date, value }] }], page, pageSize, totalCount, hasMore }` with `totalCount` documented as *"the number of analyses, not the number of measures"*.

---

**14. `listMetrics`** — "List metrics" — `GET api/metrics/search?ps=500` (**C6** — no `f`: `type` is not an accepted `f` value and asking for it is a 400, while omitting `f` returns every field anyway)

`query?` (substring over key/name/description), `domain?`, `includeDataMetrics?` (default `false`).
**No `page`/`pageSize`**: the endpoint returns all 155 metrics in one 500-row call, so the tool fetches once and filters client-side, which is both cheaper and lets the filter be a substring rather than the API's non-existent `q`.
Result `MetricListResult { metrics: [{ key, name, type, domain, description?, higherIsBetter? }], totalCount, note? }`.
`includeDataMetrics=false` drops `DATA` and `DISTRIB` types (`ncloc_data`, `file_complexity_distribution`, …) — multi-kilobyte payloads that are useless to a model. `note` records how many were filtered out, so the filtering is visible.
**Verdict: include.** It is one cheap call that unlocks the entire measures surface, and it is the only way to answer "what is the metric key for X" without the model guessing a key that produces a silent omission.

---

**15. `getFileCoverage`** — "Get file coverage" — `GET api/sources/lines`

`component` (**required**, path or key), `projectKey?`, `from?`, `to?`, `onlyUncovered?` (default `true`), `branch?`, `pullRequest?`.
Result `FileCoverageResult { component, path, from, to, uncoveredLines: int[], partiallyCoveredLines: int[], newLines: int[], duplicatedLines: int[], lines: [{ line, hits?, conditions?, coveredConditions?, isNew?, duplicated? }], note? }`.
**The `code` field is deliberately dropped.** It is syntax-highlighted HTML (verified: `<span class="cd">…`), the agent already has the file on disk, and returning it would triple the payload for zero information. `onlyUncovered=true` returns only lines with `lineHits == 0` or `coveredConditions < conditions`, which is the question anyone asks. A range wider than `ToolDefaults.MaxSourceLines` (from `SONARQUBE_MCP_MAX_SOURCE_LINES`, default 2000) is refused with a message naming `from`/`to`.
**Verdict: include.** Line-level coverage is the one thing SonarQube knows that a repo checkout often does not, and it is directly actionable ("write a test for these lines").

---

### 1d. Write tools in full

**16. `transitionIssue`** — "Transition issue" — `POST api/issues/do_transition` (form-urlencoded)

`issueKey` (required), `transition` (required — `accept, confirm, falsepositive, reopen, resolve, unconfirm, wontfix`), `comment?` (posted with the transition).
`close`, `resolveasreviewed`, `resolveassafe`, `resolveasacknowledged` and `resetastoreview` are accepted by the API but **not exposed**: the first is not a user action, and the other four are hotspot transitions that `setHotspotStatus` owns. `wontfix` is accepted as a legacy alias and the description says `accept` is the current spelling.
Result `IssueTransitionResult { key, issueStatus, status, resolution?, message, file, line?, availableTransitions[], url }`.

**17. `assignIssue`** — "Assign issue" — `POST api/issues/assign`

`issueKey` (required), `assignee?` — **omit or pass an empty string to unassign**, stated in the description. The value is a **login** (`ada@github`), not a UUID and not a display name; `__me__` assigns to the token's own account.
Result `IssueAssignResult { key, assignee?, message, file, line?, url }`.

**18. `addIssueComment`** — "Add issue comment" — `POST api/issues/add_comment`

`issueKey` (required), `text` (required, markdown).
Result `IssueCommentResult { key, commentKey?, text, createdAt?, message, file, url }` — `commentKey` is the newest entry of the returned issue's `comments[]`.

**19. `setHotspotStatus`** — "Set hotspot status" — `POST api/hotspots/change_status`, then `GET api/hotspots/show`

`hotspotKey` (required), `status` (required — `TO_REVIEW` | `REVIEWED`), `resolution?` (`FIXED` | `SAFE` — **C2**, required when `status=REVIEWED`, refused when `status=TO_REVIEW`), `comment?`.
The endpoint answers `204` with no body, so the tool re-reads via `hotspots/show` and returns the resulting state — which is also what makes the result worth structuring.
Result `HotspotStatusResult { key, status, resolution?, message, file, line?, url }`.

### 1e. `organization`: injected, never a parameter

`organization` is required by `components/search`, `rules/search`, `qualitygates/list` and `organizations/search` — of our surface, `listProjects` and `getRule`. It is taken from `SONARQUBE_ORG` and **is not a tool parameter anywhere**:

- One organization per server instance is the normal case, and it is what the official server's env-var contract already implies (`SONARQUBE_ORG`, not `SONARQUBE_DEFAULT_ORG`).
- The model cannot *discover* an org key it was not told; a wrong one is an opaque 400.
- It removes two parameters from two schemas at zero cost.
- Two organizations means two server entries in the client config, which is one line of JSON and is how every other credential-scoped MCP server works.

`ToolDefaults.RequireOrganization(options)` throws an `McpException` naming `SONARQUBE_ORG` and explaining it is the segment in `sonarcloud.io/organizations/{key}`.

### 1f. `projectKey`: parameter with an env-var default

Follows the `BITBUCKET_DEFAULT_WORKSPACE` precedent exactly. `projectKey` is an optional parameter on all thirteen project-scoped tools; `SONARQUBE_MCP_DEFAULT_PROJECT` supplies it when omitted; neither set is an `McpException` naming the variable and pointing at `listProjects`. The reason to have the default at all, where bitbucket made `repository` required: an MCP server is configured per-checkout in practice (`.mcp.json` in the repo), and the Sonar project key is *not* guessable from the directory name — pinning it in the environment removes the single most common source of 404s.

### 1g. Inclusion verdicts, against the context budget

| Candidate | Verdict | Reason |
|---|---|---|
| `listMetrics` | **In** | The only discovery path for 155 metric keys; one call, client-filtered, DATA types dropped. |
| `searchRules` | **Out as a tool** | Browsing the rule catalogue is not a coding-agent task; the *endpoint* is used, by `getRule`. A model that wants "all rules about nullability" is better served by `searchIssues(tags=…)` on the code it actually has. |
| `getRawSource` | **Out** | The agent has the file locally and reading it is free; the endpoint additionally needs "See Source Code" and answers 404 (not 403) when it is missing, so the tool would spend a call to produce a confusing error. Excluding it also removes the only plain-text response path from the client (§2). |
| `getFileCoverage` (`sources/lines`) | **In** | Line-level coverage/new-code/duplication is the one thing Sonar knows that the checkout usually does not. `code` dropped, `onlyUncovered` default true. |
| `getDuplications` | **Out** | `duplicated_lines_density` via `listComponentMeasures` answers "where is the duplication" at file granularity, which is enough to act on; the block detail needs a `_ref`-into-`files`-map join for a question that is asked once a quarter. Recorded as a v1.1 candidate. |
| `listProjectAnalyses` | **Out** | `listBranches` already carries `analysisDate` and `getMeasuresHistory` already carries the trend; only VERSION events are unique to it. v1.1 candidate. |
| `getScmInfo` (`sources/scm`) | **Out** | Positional-array JSON (`[[line, author, date, revision], …]`) would need the codebase's only hand-written array converter, to answer a question `git blame` answers instantly and for free. |

---

## 2. Project structure, file by file

```
sonarqube-mcp/
├─ .claude-plugin/{marketplace.json, plugin.json}         ADAPTED
├─ .claude/skills/sonarqube-code-quality/SKILL.md         NEW
├─ .config/dotnet-tools.json                              VERBATIM (fallout.globaltool 10.4.0)
├─ .editorconfig .gitattributes .gitignore                VERBATIM
├─ .fallout/parameters.json                               ADAPTED (Solution: sonarqube-mcp.slnx)
├─ .github/workflows/{build,publish}.yml                  GENERATED (never hand-edited)
├─ .github/workflows/release.yml                          ADAPTED (5-RID matrix, s/bitbucket/sonarqube/)
├─ .mcp/server.json                                       ADAPTED (env var block per §3)
├─ AGENTS.md  CLAUDE.md(@AGENTS.md)  CHANGELOG.md  README.md  LICENSE
├─ Directory.Build.props                                  VERBATIM + product strings
├─ Directory.Packages.props                               ADAPTED — ProtectedData row DELETED
├─ global.json  nuget.config  build.{ps1,sh,cmd}          VERBATIM
├─ sonarqube-mcp.slnx                                     ADAPTED
├─ build/
│   ├─ _build.csproj, Directory.Build.props/.targets      VERBATIM
│   ├─ Configuration.cs, ReleaseNotes.cs,
│   │  ReleaseNotesParser.cs, SemVersion.cs,
│   │  Extensions/StringExtensions.cs                     VERBATIM
│   ├─ Build.cs                                           ADAPTED (ExpectedToolNames ×19; SmokeTest runs twice)
│   ├─ Build.Publish.cs                                   VERBATIM + UA "sonarqube-mcp-build/1.0"
│   └─ Build.CI.GitHubActions.cs                          ADAPTED (names, NUGET_USER)
├─ src/SonarQube.Mcp/
│   ├─ Program.cs                                         VERBATIM shape (one line)
│   ├─ McpServerSetup.cs                                  ADAPTED (+ READ_ONLY tool-class selection)
│   ├─ ServerVersion.cs                                   VERBATIM shape
│   ├─ SonarQube.Mcp.csproj                               ADAPTED (3 PackageReferences)
│   ├─ Cli/{CliDispatcher, CliRuntime, StatusCommand}.cs  ADAPTED — Login/LogoutCommand DELETED
│   ├─ Configuration/SonarQubeMcpOptions.cs               ADAPTED (§3)
│   ├─ Authentication/
│   │   ├─ StaticTokenCredential.cs                       NEW (~60 lines; replaces 13 files)
│   │   └─ AuthenticationRequiredException.cs             ADAPTED (one reason, not six)
│   ├─ Http/
│   │   ├─ SonarApiClient.cs                              ADAPTED (~900 lines, 19 methods)
│   │   ├─ SonarRequestBuilder.cs                         ADAPTED (see below)
│   │   ├─ AuthenticationHandler.cs                       SIMPLIFIED (~70 lines)
│   │   ├─ RetryHandler.cs                                VERBATIM
│   │   ├─ FormBody.cs                                    NEW
│   │   ├─ PagedResult.cs                                 ADAPTED from Page<T>
│   │   ├─ SonarApiException.cs                           ADAPTED
│   │   └─ Models/*.cs + SonarWireJsonContext.cs          NEW (§5)
│   ├─ Json/SonarDateTimeOffsetConverter.cs               NEW
│   └─ Tools/
│       ├─ ProjectReadTools.cs  IssueReadTools.cs
│       │  MeasureReadTools.cs  IssueWriteTools.cs        NEW
│       ├─ ToolDefaults.cs  ToolErrors.cs                 ADAPTED (funnel shape kept)
│       ├─ ResultMapper.cs                                NEW
│       ├─ ComponentKeys.cs                               NEW (path↔key, components[] join, deep links)
│       ├─ MeasureFormatting.cs                           NEW (ratings, periods, missingMetrics)
│       ├─ HtmlToText.cs                                  NEW
│       ├─ ServerInstructions.cs                          NEW (10 lines, §1 of AGENTS)
│       └─ Models/*.cs + SonarToolJsonContext.cs          NEW
└─ tests/SonarQube.Mcp.Tests/  (§8)
```

**Deleted from the template (23 files):** the whole `Authentication/` OAuth subsystem — `OAuthCredentialProvider`, `OAuthTokenClient`, `TokenStore`, `TokenSet`, `TokenFileEnvelope`, `InteractiveAuthenticator`, `IInteractiveAuthenticator`, `NullInteractiveAuthenticator`, `LoopbackCallbackListener`, `BrowserLauncher`, `CredentialProviderFactory`, `ICredentialProvider`, `StaticCredentialProvider` — plus `Cli/LoginCommand.cs`, `Cli/LogoutCommand.cs`, all seven `Diffs/*`, `Http/BitbucketCursor.cs`, `Http/InvalidCursorException.cs`, `Http/FieldSets.cs`, and their eight test files. The `System.Security.Cryptography.ProtectedData` package goes with them: **the runtime package budget drops from four to three.**

**`SonarRequestBuilder` — what changes and what does not.** No cursor machinery and no SSRF guard: pages are `p`/`ps` integers the model cannot turn into a URL, so there is nothing to validate. What is *kept* verbatim is the invariant that made it safe: relative URLs only, resolved against the client's `BaseAddress`, so no code path can name a different host; `Uri.EscapeDataString` on every segment including constants; and `Query(name, string?|int?|bool?)` where "unset" and "empty" are different requests. What is *added*:
- `QueryList(name, IReadOnlyList<string>?)` producing a **comma-joined** value — Sonar takes `issueStatuses=OPEN,CONFIRMED`, not repeated parameters (the inverse of bitbucket's `QueryEach`). Values containing a comma are refused with a message rather than silently splitting.
- `RequireComponentKey` replacing `RequireSlug`: rejects blank, `.`, `..` and any segment that would collapse the path, but **permits `:` and `/`**, because a component key legitimately contains both. Component keys go in the *query string*, never in a path segment, so the dot-segment attack surface is only the fixed `api/{service}/{action}` prefix — which is why every segment still goes through the guard.

**`AuthenticationHandler` — simplified, not deleted.** It still attaches `Authorization: Bearer` **per request**, never on `DefaultRequestHeaders` (the token must not ride along on a request the chain issues itself). What is removed: the 401-invalidate-and-retry loop (a static token cannot be refreshed — a second attempt is guaranteed to fail identically) and the entire hand-rolled redirect follower (D16). The transport keeps `AllowAutoRedirect = false`, and a 3xx becomes a `SonarApiException` naming the `Location` — no known `api/` endpoint redirects, and if one appears the failure is loud rather than a silently-stripped credential.

**New, with no template ancestor:**
- `Http/FormBody.cs` — builds `application/x-www-form-urlencoded` bodies as `ByteArrayContent`, deliberately not `FormUrlEncodedContent`: `ByteArrayContent` is what `RetryHandler.IsResendable` already recognises, and that is precisely what makes a write retryable after a 429. `Uri.EscapeDataString` then `+` for spaces per the form encoding.
- `Json/SonarDateTimeOffsetConverter.cs` — accepts `+0000`, `+00:00` and `Z`; applied by `[JsonConverter]` on wire properties, so it is source-gen-visible and AOT-safe.
- `Tools/MeasureFormatting.cs` — the string-valued-measure logic: pick `value` or `periods[0].value`, translate `*_rating` to A–E, diff requested against returned to produce `missingMetrics`.
- `Tools/ComponentKeys.cs` — path↔key normalisation, the `components[]` sidecar join, and the derived deep links (below).
- `Tools/HtmlToText.cs` — rule and hotspot prose.

**Deep links are derived, not fetched.** Sonar returns no web URL for an issue, hotspot, project or file, but they are all derivable from the base URL, so `ComponentKeys` composes them once and every result record carries `url`:
`{base}/project/issues?id={project}&issues={key}&open={key}` · `{base}/security_hotspots?id={project}&hotspots={key}` · `{base}/project/overview?id={project}` · `{base}/code?id={project}&selected={component}`, each with `&branch=`/`&pullRequest=` appended when in scope. This is the direct analogue of bitbucket's *"`url` — the one link a model cannot derive and the one a human asks for"*, except here we do the deriving.

---

## 3. Configuration surface

Environment variables only. One `internal sealed record SonarQubeMcpOptions` with `FromEnvironment()` and `FromEnvironment(Func<string,string?>)`, read exactly once in `RunStdioAsync`. **`FromEnvironment` never throws** — a bad value falls back to the documented default, because failing startup leaves the MCP client with a dead server and no channel to complain on.

| Variable | Default | Clamp / validation | Notes |
|---|---|---|---|
| `SONARQUBE_TOKEN` | — | trimmed; blank → null | User token, sent as `Bearer`. Absent is legal at startup; the first tool call fails with an `AuthenticationRequiredException` naming it. |
| `SONARQUBE_ORG` | — | trimmed | Organization key. Required only by `listProjects` and `getRule`; error names it. |
| `SONARQUBE_URL` | `https://sonarcloud.io` | **host allowlist** `sonarcloud.io`, `www.sonarcloud.io`, `sonarqube.us`; scheme must be `https`; trailing slash normalised. Anything else → fall back to the default and log a Warning to **stderr**. | Cloud only. `api.sonarcloud.io` (the v2 host) is never used. |
| `SONARQUBE_MCP_DEFAULT_PROJECT` | — | trimmed | Makes `projectKey` optional on every tool. |
| `SONARQUBE_MCP_READ_ONLY` | `false` | `1/true/yes/on` · `0/false/no/off`; anything else → default | When true, `WithTools<IssueWriteTools>` is **never called** — the four write tools do not appear in `tools/list` at all. |
| `SONARQUBE_MCP_LOG_LEVEL` | `Information` | `Enum.TryParse` + `Enum.IsDefined` (rejects `"42"`) | stderr only. |
| `SONARQUBE_MCP_MAX_PAGE_SIZE` | `100` | 1–500 | The ceiling `ClampPageSize` enforces. 500 is the API's own hard limit. |
| `SONARQUBE_MCP_DEFAULT_PAGE_SIZE` | `50` | 1–`MaxPageSize` (clamped to it after both are read) | What a tool sends when `pageSize` is omitted. |
| `SONARQUBE_MCP_MAX_SOURCE_LINES` | `2000` | 1–20000 | Cap on `getFileCoverage`'s `from`–`to` span. |
| `SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS` | `100` | 5–600 | Whole-request timeout. |

Nine variables (plus the token). No config file, no `Microsoft.Extensions.Configuration`, no command-line config arguments.

**CLI.** `serve` (default with no args), `status`, `version|--version|-v`, `help|--help|-h`. Exit codes `0/1/2`. `Cli/` is the only namespace permitted to touch `Console`.

`status` prints, in order: version; resolved base URL; whether `SONARQUBE_TOKEN` is **set** (never any part of its value, and never a truncated prefix that narrows a search); `SONARQUBE_ORG`; default project; read-only mode; then the credential probe:

- No token → *"No SONARQUBE_TOKEN set."* and **`authentication/validate` is not called**, because it answers `{"valid":true}` to an anonymous request (**C4**) and printing "valid" would be a lie.
- Token set → `GET api/authentication/validate`. `valid:false` → *"the token was rejected — it is expired, revoked, or from a different SonarQube instance."*
- Token **and** org set → additionally `GET api/components/search?organization={org}&ps=1`, which proves the token *and* the org in one call and reports the project count. A 401 here versus a 400/404 distinguishes "bad token" from "bad org".

`status` always exits 0: "nothing is configured" is a fact it reports, not a failure of it.

---

## 4. Pagination at the tool boundary

**Parameters** are `page` (int?, 1-based) and `pageSize` (int?). No cursors: SonarQube's paging is a pair of integers, there is nothing opaque to encode, and inventing a cursor would add the SSRF surface the bitbucket design had to defend against for no benefit.

**`ToolDefaults.ResolvePaging(int? page, int? pageSize, options)`** returns a `(int Page, int PageSize)` and is the only place any of this is decided:

1. `pageSize` → `Math.Clamp(value, 1, options.MaxPageSize)`; null → `options.DefaultPageSize`. Clamping is silent, per the bitbucket precedent — the binding constraint is the model's context, not the API's, and a clamp is not a caller error.
2. `page` → `Math.Max(value, 1)`; null → 1.
3. **The 10,000 cap is enforced before the request leaves the process:**
   ```
   if (page * pageSize > 10_000) throw new McpException(
     $"SonarQube Cloud returns only the first 10000 results, and page {page} at pageSize {pageSize} " +
     $"asks for result {page * pageSize}. Paging deeper is not possible — narrow the search instead: " +
     "scope it with component, add issueStatuses or impactSeverities, or set createdAfter/createdInLast, " +
     "then start again from page 1.");
   ```
   Enforcing it client-side turns the API's terse `"Can return only the first 10000 results. 20000th result asked."` into an instruction. The API's own 400 is still mapped (§7) in case a future limit differs.

**Envelope.** Every paginated result record carries the same four properties, in the same order, asserted by `ToolSchemaTests`:

```
page: int, pageSize: int, totalCount: int?, hasMore: bool, note: string?
```

`hasMore = items.Count > 0 && page * pageSize < totalCount`. `note` is set only when there is something the model must know: the result cap is in reach (*"totalCount exceeds 10000; only the first 10000 results are reachable — narrow the filter."*), or a client-side filter removed rows (`listMetrics`).

**Reading the response's page numbers.** The three shapes verified live — flat (`total`/`p`/`ps`, e.g. `rules/search`), nested (`paging{pageIndex,pageSize,total}`, e.g. `components/tree`), and both (`issues/search`) — collapse into one wire base class `PagedEnvelopeDto { Total, P, Ps, Paging }` with a `PagedResult.From(dto, requestedPage, requestedPageSize)` that prefers `paging`, falls back to the flat trio, and falls back again to the requested values. The endpoints with no paging at all (`project_branches/list`, `project_pull_requests/list`, `qualitygates/*`, `measures/component`) expose no `page`/`pageSize` parameters and return no envelope — asserted by `ToolSchemaTests` in both directions, so a paginated tool cannot lose its envelope and an unpaginated one cannot grow a fake one.

---

## 5. Wire DTO inventory and the two `JsonSerializerContext`s

### `Http/Models/SonarWireJsonContext`

`[JsonSourceGenerationOptions(DefaultIgnoreCondition = WhenWritingNull)]`, **no naming policy** — every property carries an explicit `[JsonPropertyName]`, so a refactor cannot silently change what goes on the wire. Never chained into the MCP serializer options. All properties nullable: a metric with no data, an anonymous request, and a field the endpoint simply does not return are indistinguishable.

**Shared** — `PagedEnvelopeDto`, `PagingDto`, `ComponentDto` (`organization,id,key,uuid,enabled,qualifier,name,longName,path,project,language,branch,pullRequest` — `id` *and* `uuid`, because the measures endpoints send the former and everything else the latter), `TextRangeDto`, `ImpactDto`, `ErrorEnvelopeDto`+`ErrorDto{msg}`, `ValidateResponseDto{valid}`.

**Issues** — `IssuesSearchResponseDto : PagedEnvelopeDto {issues[],components[],rules[],users[],facets[],effortTotal}`, `IssueDto` (all 24 fields incl. `impacts[]`, `issueStatus`, `cleanCodeAttribute*`, `transitions[]`, `comments[]`, `flows[]`), `IssueCommentDto`, `FlowDto`+`FlowLocationDto`, `RuleRefDto`, `UserRefDto`, `IssueOperationResponseDto {issue, components[], rules[], users[]}` (the shared shape of `do_transition` / `assign` / `add_comment`).

**Hotspots** — `HotspotsSearchResponseDto : PagedEnvelopeDto {hotspots[],components[]}`, `HotspotDto`, and a **separate** `HotspotShowResponseDto` because `hotspots/show` returns `component`/`project` as objects, its `assignee` as a **login** rather than a UUID (**C7**), and its comments under the property name **`comment`, singular** (**C8**) — with `HotspotRuleDto {key,name,securityCategory,vulnerabilityProbability,riskDescription,vulnerabilityDescription,fixRecommendations}`, `HotspotChangelogDto`+`HotspotChangelogDiffDto`, `HotspotCommentDto`, and a `users[]` sidecar.

**Measures** — `MeasuresComponentResponseDto {component, metrics[], periods[]}`, `ComponentMeasuresDto : ComponentDto {measures[]}`, **`MeasureDto {metric, value: string?, periods: MeasurePeriodDto[]?, bestValue: bool?}`** and `MeasurePeriodDto {index:int?, value: string?, bestValue: bool?}` — *`value` is `string?` on both, never `double`*, `MeasuresComponentTreeResponseDto : PagedEnvelopeDto {baseComponent, components[], metrics[]}`, `MeasuresHistoryResponseDto : PagedEnvelopeDto {measures[]}` + `MeasureHistoryDto {metric, history[]}` + `HistoryEntryDto {date, value: string?}`, `MetricsSearchResponseDto : PagedEnvelopeDto {metrics[]}` + `MetricDto`.

**Quality gates** — `ProjectStatusResponseDto {projectStatus}`, `ProjectStatusDto {status, conditions[], periods[], ignoredConditions, caycStatus}`, `QualityGateConditionDto {status, metricKey, comparator, periodIndex:int?, errorThreshold, actualValue}`.

**Components** — `ComponentsSearchResponseDto`, `ComponentsTreeResponseDto {baseComponent, components[]}`.

**Branches / PRs** — `BranchesListResponseDto {branches[]}`, `BranchDto` + `BranchStatusDto` + `CommitDto` + `CommitAuthorDto`; `PullRequestsListResponseDto {pullRequests[]}`, `SonarPullRequestDto` + `PullRequestStatusDto`.

**Rules** — `RulesSearchResponseDto {rules[], total, p, ps}` (flat paging), `RuleDto` (incl. `descriptionSections[]`, `educationPrinciples[]`, `securityStandards[]`, `requiredEntitlements[]`, `params[]`), `RuleDescriptionSectionDto {key, content, context}`, `RuleContextDto`, `RuleParamDto`.

**Sources** — `SourcesLinesResponseDto {sources[]}`, `SourceLineDto {line, code, scmRevision, scmAuthor, scmDate, duplicated, isNew, lineHits, conditions, coveredConditions}`.

**No request-body DTOs at all** — every write is form-urlencoded through `FormBody`. That is a genuine simplification over the template, which had five.

**Converters:** exactly one, `SonarDateTimeOffsetConverter`, applied by attribute to `creationDate`, `updateDate`, `closeDate`, `createdAt`, `analysisDate`, `date`, `scmDate` and the commit dates. `sources/scm`'s positional-array shape, which would have needed the second, is designed out by excluding `getScmInfo`.

### `Tools/Models/SonarToolJsonContext`

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
```
Every result record (~30 types) plus the primitive parameter types the schema generator needs with reflection off: `string`, `string[]`, `int`, `int?`, `bool`, `bool?`. Chained **first** in `TypeInfoResolverChain`, SDK resolver second, chain cleared before either is added, then `MakeReadOnly()` — all of it in `McpServerSetup.CreateToolSerializerOptions()`, which the tests call rather than reimplement.

---

## 6. `ToolDefaults` validation inventory

Everything thrown is already an `McpException`; every message names the parameter or environment variable to change, and lists the exact allowed values. This is the complete surface:

| Member | Behaviour |
|---|---|
| `ResolveProject(projectKey, options)` | Trim; fall back to `SONARQUBE_MCP_DEFAULT_PROJECT`; else throw naming it and `listProjects`. |
| `RequireOrganization(options)` | Non-blank `SONARQUBE_ORG` or throw, explaining it is the `/organizations/{key}` segment. |
| `ResolveComponent(component, projectKey, options)` | Null → the project key. Contains `:` or equals the project key → verbatim. Otherwise normalise (`\`→`/`, strip leading `./` and `/`) and return `{project}:{path}`. Blank after normalising → throw. |
| `ResolveScope(branch, pullRequest)` | Both set → throw: *"branch and pullRequest are mutually exclusive; pass one or neither (neither means the main branch)."* Returns a `readonly record struct AnalysisScope(string? Branch, string? PullRequest)` that the request builder consumes, so no tool can forget one of the two. |
| `ResolvePaging(page, pageSize, options)` | §4, including the 10,000 pre-flight. |
| `RequireIssueKey(key)` / `RequireHotspotKey` / `RequireRuleKey` | Non-blank; `ruleKey` must contain `:` (`repository:S1234`) with the message spelling out the shape. |
| `ResolveIssueStatuses(string[]?)` | Default `["OPEN","CONFIRMED"]`. Validates against `OPEN, CONFIRMED, FALSE_POSITIVE, ACCEPTED, FIXED` — **the exact list in the error message**. Rejects the legacy `REOPENED`/`RESOLVED`/`CLOSED` with a message mapping each to its modern equivalent. |
| `ResolveImpactSeverities` | `INFO, LOW, MEDIUM, HIGH, BLOCKER`. Rejects `MINOR`/`MAJOR`/`CRITICAL` with the mapping (`MINOR`→`LOW`, `MAJOR`→`MEDIUM`, `CRITICAL`→`HIGH`). |
| `ResolveImpactSoftwareQualities` | `MAINTAINABILITY, RELIABILITY, SECURITY`. Rejects `BUG`/`CODE_SMELL`/`VULNERABILITY` with the mapping. |
| `ResolveIssueSort(sortBy)` | `CREATION_DATE, UPDATE_DATE, CLOSE_DATE, SEVERITY, STATUS, ASSIGNEE, FILE_LINE`; default `CREATION_DATE`. |
| `ResolveTransition(transition)` | `accept, confirm, falsepositive, reopen, resolve, unconfirm, wontfix` (lowercased). The message notes `accept` supersedes `wontfix` and that `getIssue`'s `availableTransitions` says which are legal *now*. |
| `ResolveHotspotStatus(status, resolution)` | `status` ∈ `TO_REVIEW, REVIEWED`; `resolution` ∈ `FIXED, SAFE` (**C2** — the message says `ACKNOWLEDGED` is not accepted on SonarQube Cloud); `REVIEWED` without a resolution → throw; `TO_REVIEW` **with** one → throw. |
| `ResolveHotspotSearchStatus` | Same status list; `resolution` limited to `FIXED, SAFE` with a note that search and change_status differ. |
| `RequireMetricKeys(string[]?, int max)` | Non-empty; trimmed; deduplicated; `> max` → throw naming the count and the limit. `max` is **15** for `listComponentMeasures` (the API's own `maxValuesAllowed`) and 25 for `getComponentMeasures`. Message points at `listMetrics`. |
| `ResolveCreatedFilter(createdAfter, createdInLast)` | Both set → throw (the API rejects the combination). `createdInLast` must match `^\d+[dwmy]$`. |
| `ResolveComponentScope(scope)` | `files`→(`leaves`,`FIL,UTS`), `directories`→(`children`,`DIR`), `all`→(`all`,null). Message explains the `q`-versus-`strategy` trap. |
| `ResolveLineRange(from, to, options)` | `from ≥ 1`, `to ≥ from`, span ≤ `MaxSourceLines` → else throw naming `from`/`to` and the cap. |
| `ResolveRuleSections(string[]?)` | Default `introduction, root_cause, how_to_fix`; validates against the five section keys. |
| `CleanList(string[]?)` | Trim, drop blanks, null when empty — verbatim from the template. |

`ToolErrors.ExecuteAsync(context, body)` wraps every tool body, so **only `McpException` escapes a tool method**. `ToolCallContext` becomes `readonly record struct ToolCallContext(string Tool, string? Project = null, string? Component = null, string? EntityKey = null)`.

---

## 7. Error funnel

```
McpException  ─ passthrough (argument validation already produced the finished product)
OperationCanceledException ─ rethrow untouched
AuthenticationRequiredException ─ the no-token case, below
SonarApiException ─ status table, below
ArgumentException ─ "Invalid argument. {message}\nFix the named argument and call again; retrying unchanged will fail the same way."
_ ─ "Unexpected error: {Type}: {message}", stack trace to stderr at Debug only
```

`SonarApiException` carries `StatusCode`, the parsed `ErrorEnvelopeDto` (nullable — **C3**), the raw body capped at 16 KiB, `RetryAttempts` and `RetryAfterSeconds`. `Detail(exception)` joins all `errors[].msg` values with `"; "` and returns null for an empty body.

| Status | Message |
|---|---|
| **no token** | *"No SonarQube token is configured, so this server cannot call the API.*<br>*Set `SONARQUBE_TOKEN` in the environment the MCP client launches this server with, then restart it. Create one at `{baseUrl}/account/security` — a **User Token**, not a project analysis token.*<br>*Also set `SONARQUBE_ORG` to your organization key (the `/organizations/{key}` segment of the sonarcloud.io URL).*<br>*Run `sonarqube-mcp status` to check what this server currently sees."* |
| **400** | *"SonarQube Cloud rejected the request as invalid (400)."* + `\nSonarQube said: {msg}` — **quoted verbatim, because the API's own 400 text is self-documenting** (`'ps' value (501) must be less than 500`; `Value of parameter 'additionalFields' (period) must be one of: [metrics, periods]`) + *"Fix the named argument and call again; retrying unchanged will fail the same way."* Two special cases: a message containing `only the first 10000 results` appends the narrow-your-filter advice from §4; on `transitionIssue`, a message containing `transition` appends *"Call getIssue and use availableTransitions — a transition is only legal from certain states."* |
| **401** | *"SonarQube Cloud rejected the credentials (401 Unauthorized)."* + `msg` **when there is one — a 401 from an invalid bearer token has an empty body (verified), so the composer must not depend on it** + *"The token in SONARQUBE_TOKEN is missing, expired, revoked, or belongs to a different SonarQube instance than SONARQUBE_URL. Replace it and restart this server; `sonarqube-mcp status` reports what it currently sees."* |
| **403** | *"SonarQube Cloud refused this operation (403 Forbidden)."* + msg + *"The token is valid but the account lacks permission on {target}. Browsing a private project needs 'Browse'; transitioning, assigning or commenting on an issue needs 'Administer Issues'; changing a hotspot's status needs 'Administer Security Hotspots'. `getHotspot` reports `canChangeStatus` before you try. Ask a project administrator, or use a token from an account that has the permission."* |
| **404, `sources/*`** | *"SonarQube Cloud has no such source (404)."* + msg + **"SonarQube answers 404 — not 403 — both for a component that does not exist and for one this token may not read, and `api/sources/*` additionally requires the 'See Source Code' permission. So this can mean either 'wrong component key' or 'the file is there and the token cannot read it'. Confirm the key with `listComponents` (it is `projectKey:path/from/the/project/root`); if the key is right, the permission is the problem."** |
| **404, other** | *"SonarQube Cloud has no such resource (404)."* + msg + *"This call looked for {target}. The project key is the `id` in a sonarcloud.io project URL, not the repository name — confirm it with `listProjects`. A branch or pull request that has never been analysed is also a 404; `listBranches` and `listPullRequests` say which exist."* |
| **429** | *"SonarQube Cloud rate-limited this request (429 Too Many Requests)."* + *"It was already retried {n} time(s) with exponential backoff."* + **"SonarQube Cloud publishes no rate limits and sends no `X-RateLimit-*` headers, so there is no number to read — wait about a minute before calling again and cut the request rate: a smaller `pageSize`, fewer `metricKeys`, and a narrower `searchIssues` filter instead of paging through everything."** `Retry-After`, if present, is quoted in preference to the guess. |
| **≥500** | *"SonarQube Cloud failed on its own side (HTTP {n})."* + retried-or-not + *"Nothing in the request needs changing. Try again in a few minutes, and check https://status.sonarcloud.io/ if it persists."* |
| **3xx** | *"SonarQube Cloud redirected this request to {Location}, which this server does not follow — the API is not expected to redirect. Check SONARQUBE_URL."* |
| **other** | *"SonarQube Cloud returned an unexpected HTTP {n}."* + msg |

`Target(context)` renders `issue {key} in project {project}`, `component {component} in project {project}`, or `project {project}`.

---

## 8. Test plan

xunit.v3 + `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`. **No mocking or assertion libraries** — `StubHttpMessageHandler` and `ManualTimeProvider` are lifted from the template unchanged. `JsonSerializerIsReflectionEnabledByDefault=false` in the test csproj too, so a missing `[JsonSerializable]` fails `dotnet test`. Fixtures are `<EmbeddedResource Include="Fixtures/**" />`. Cancellation via `TestContext.Current.CancellationToken` everywhere. Target: ~320 tests over ~26 files.

### Rule-enforcing tests (do not delete one to make a change pass)

1. **`ToolInventoryTests`** — the 19 names in ordinal order; the `Title` per tool; the full annotation table asserted against `tool.ProtocolTool.Annotations` (what a client receives, not the attribute), with `DestructiveHint` asserted **null** on all fifteen read tools; every tool has an `OutputSchema`; all four classes sealed, `[McpServerToolType]`, no public constructor; every method `public static` returning `Task<T>` with `T` in `SonarQube.Mcp.Tools.Models`; `[Description]` on every method and every non-injected parameter; `CancellationToken` last and optional; `SonarApiClient` and `SonarQubeMcpOptions` as parameters 0 and 1.
2. **`ReadOnlyModeTests`** *(new — the flag is the one requirement with no template precedent)* — `McpServerSetup.ToolTypesFor(options)` returns four types normally and three with `ReadOnly = true`; the tool collection built from the read-only set contains **zero** write-tool names; and the write class is still fully constructible so the mode cannot be faked by breaking it. The production `RunStdioAsync` calls that same method, so this cannot drift.
3. **`ToolSchemaTests`** — `TypeInfoResolverChain[0] is SonarToolJsonContext` and `IsReadOnly`; per-tool `[InlineData]` of the exact ordered property list and required list; `NeverInSchema = ["client","options","cancellationToken"]`; camelCase and a description on every input and output property; paginated tools carry `page,pageSize,totalCount,hasMore` and unpaginated ones carry none of them.
4. **`NoStdoutWritesTest`** — verbatim, with `RootMarker = "sonarqube-mcp.slnx"` and `AllowedDirectory = "src/SonarQube.Mcp/Cli/"`, both self-checks retained.
5. **`AgentSkillTests`** — `.claude/skills/sonarqube-code-quality/SKILL.md`, backticked tool-shaped identifiers cross-checked against the reflected inventory in both directions, tool *parameter* names excluded by reflection, frontmatter `name` = directory name and `description` ≤ 1024 chars.
6. **`McpServerManifestTests`** — `.mcp/server.json` `version` and its NuGet entry's `version` both equal `CHANGELOG.md`'s top version; `identifier` equals `<PackageId>`.
7. **`PluginManifestTests`** — marketplace `source: "./"`; the skill path resolves to a real `SKILL.md`; plugin `version` and the `dnx` pin both equal the changelog version; `userConfig` matches `.mcp/server.json` placeholder for placeholder including `isSecret` ⇒ `sensitive`.
8. **`ApiParameterContractTests`** *(new — the structural replacement for `FieldSetTests`)*
   - **Offline (CI):** a `SonarApiParameters` table of `(endpoint, parameter)` pairs; a test drives every client method through `StubHttpMessageHandler` with every optional argument set, collects the query keys actually sent, and asserts each is in the table. Plus: every enum value set in `ToolDefaults` equals the corresponding entry in the table, so a validator cannot accept a value the client never sends or reject one it does. Plus a "the rule is discriminating" guard — inject a bogus parameter and assert the test would fail — so it cannot go vacuous.
   - **Online (opt-in, `SONARQUBE_MCP_LIVE_TESTS=1`, `[Trait("Category","Live")]`, never in the required CI path):** fetch `api/webservices/list` and assert every parameter in the table is documented for that action **or** appears in `UndocumentedButVerified` — today exactly `hotspots/search:branch`, `hotspots/search:pullRequest`, `components/search:qualifiers`, each with the verification date. This catches a parameter SonarSource removes; the exception list stops it from failing on the three they simply never documented.

### Behaviour and client tests

9. **`ToolBehaviourTests`** (~90 tests) — per tool: default `issueStatuses` is sent as `OPEN,CONFIRMED` and an explicit list replaces it; `component="src/Widget.cs"` becomes `componentKeys=proj:src/Widget.cs` while `component="proj:src/Widget.cs"` is sent verbatim; `branch` and `pullRequest` together throw before any request; `page`/`pageSize` clamping; `page*pageSize>10000` throws with **no HTTP request made** (asserted on the stub's request count); `sortBy` default `CREATION_DATE` with `asc=false`; `metricKeys` of 16 entries throws for `listComponentMeasures` and passes for `getComponentMeasures`; `listMetrics` filters DATA/DISTRIB by default and records the count in `note`; `getFileCoverage` returns only uncovered lines by default and never returns `code`; `setHotspotStatus` issues exactly two requests (POST then show).
10. **`SonarApiClientTests`** (~55 tests) — URLs asserted via `Uri.AbsolutePath` and a parsed query, **never** `Uri.ToString()` (which unescapes and would hide whether a hostile component key was escaped); flat-only, nested-only and both paging shapes all produce the same `PagedResult`; the base-URL allowlist; a component key containing `:`, `/`, a space and a `#` is escaped exactly once; form-urlencoded write bodies asserted byte-for-byte including `Content-Type: application/x-www-form-urlencoded` and `+`-for-space, and asserted resendable after a 429; a `204` with no body does not throw; a `401` with an **empty** body produces a message and not an NRE; retry backoff schedule asserted through `ManualTimeProvider` with no real time spent; a 302 becomes an error naming the `Location`.
11. **`ToolErrorMappingTests`** — one test per row of §7, asserting the substring a model needs (`SONARQUBE_TOKEN`, `See Source Code`, `listProjects`, `availableTransitions`, …).
12. **`ResultMapperTests`** — the `components[]` join produces `file` for every issue and falls back to the substring after `:` when the sidecar is missing; a duplicate component key does not throw; deep links are composed with `branch`/`pullRequest` appended.
13. **`MeasureFormattingTests`** — `sqale_rating "1.0"` → `A`, `"5.0"` → `E`, `"3"` → `C`; a non-rating metric is untouched; `periods[0].value` becomes `newCodeValue` and never `value`; `missingMetrics` names exactly what was requested and not returned; a metric returned but not requested does not crash.
14. **`SonarDateTimeOffsetConverterTests`** — `+0000`, `+00:00`, `Z`, `-0500`, null, `""`, garbage (throws a `JsonException` naming the property).
15. **`HtmlToTextTests`** — `<pre><code>` fencing, `<li>` bullets, entity unescaping, tag stripping, the truncation marker at the cap.
16. **`ConfigurationTests`** — every variable's default, clamp and bad-value fallback via the `Func<string,string?>` overload; the `SONARQUBE_URL` allowlist accepts `sonarcloud.io`/`sonarqube.us` and falls back for `evil.example.com`, `http://sonarcloud.io` and a garbage string; `DefaultPageSize` re-clamped when `MaxPageSize` is lowered below it.
17. **`StatusCommandTests`** — no token ⇒ `authentication/validate` is **not** called (asserted on the stub); token ⇒ it is; token+org ⇒ the extra `components/search` probe; no output line ever contains the token value.

### Golden fixtures, captured live and anonymously

Captured from `https://sonarcloud.io`, org `quartznet`, project `quartznet_quartznet` (public and anonymously readable), each with a header comment recording the URL and capture date:

`issues-search-page.json` · `issues-search-empty.json` · `issues-search-single.json` (`issues=` + `additionalFields=transitions,comments`) · `hotspots-search-page.json` · `hotspots-show.json` · `measures-component.json` (**with `new_coverage` requested and absent — the silent-omission case**) · `measures-component-periods.json` (`pullRequest=`, `additionalFields=periods`) · `measures-component-tree.json` · `measures-search-history.json` · `metrics-search.json` (`ps=500`, **no `f`** — C6) · `qualitygates-project-status-none.json` (**status NONE, empty conditions — the normal case**) · `qualitygates-project-status-error.json` (**live after all** — a failing gate on a pull request) · `components-search.json` · `components-tree-leaves.json` · `project-branches-list.json` · `project-pull-requests-list.json` (**trimmed to two of 116 entries** — the endpoint has no paging parameter) · `sources-lines.json` (**live after all** — C10) · `rules-search-rule-key.json` (**the degraded, section-less anonymous response**) · `error-400-page-size.json` · `error-400-result-cap.json` · `error-401-authentication-required.json` · `error-401-empty-body.txt` · `error-404-component-not-found.json`.

**Hand-written and marked `SYNTHETIC` in `Fixtures/MANIFEST.md`**, because they cannot be captured without a token: `rules-search-with-sections.json`, `issues-do_transition.json`, `issues-assign.json`, `issues-add_comment.json`. Phase (f) replaces every one with a real capture and moves its row out of the SYNTHETIC table.

Two of the six the design expected to hand-write turned out to be capturable and **are live**:
`qualitygates-project-status-error.json` (a pull request was failing its gate at capture time) and
`sources-lines.json` (**C10** — anonymous access to `sources/lines` is not refused). And
`hotspots-change_status-204` has **no file at all**: the endpoint answers `204` with no body, so
there is nothing to store and the test enqueues a bodiless 204 instead.

**Provenance lives in `Fixtures/MANIFEST.md`, not in the fixtures.** JSON cannot carry comments, so
each capture's URL, date and any normalisation is recorded there — one table for live captures, one
for the synthetic ones. A `.json` header comment, as originally specified, would simply not parse.

### `SmokeTest` (Fallout target, not xunit)

Publishes the AOT binary and drives real stdio JSON-RPC, **twice**: once with a clean environment asserting `serverInfo.name == "sonarqube-mcp"` and exactly the 19 `ExpectedToolNames`, and once with `SONARQUBE_MCP_READ_ONLY=1` asserting exactly the 15 read names and **none** of the four write names. That second leg is the end-to-end proof that the flag removes the tools from registration rather than rejecting them at call time. Both legs run with no token configured, proving the handshake completes without credentials.

---

## 9. Phased implementation — six delegable tasks

Each phase is self-contained, states its inputs and outputs, and ends at a build/test gate that either passes or does not.

### Phase A — Repository skeleton and build infrastructure
**Depends on:** nothing. **Parallel with:** nothing (everything else needs the solution to exist).
**Input:** `D:\Work\bitbucket-mcp` as the template; the names and licence from the decisions.
**Do:** create `global.json`, `nuget.config`, `Directory.Build.props` (product strings updated), `Directory.Packages.props` (**three app packages — no `ProtectedData`**), `.editorconfig`, `.gitattributes`, `.gitignore` (**no bare `build` rule**), `sonarqube-mcp.slnx`, `build.{ps1,sh,cmd}`, `.config/dotnet-tools.json`, the whole `build/` orchestrator (`Build.cs` with `ExpectedToolNames` as an empty-for-now array and the two-leg `SmokeTest`, `Build.Publish.cs` with the `User-Agent`, `Build.CI.GitHubActions.cs`), `.github/workflows/release.yml`, `LICENSE` (MIT), `CHANGELOG.md` starting `# 1.0.0`, and a stub `src/SonarQube.Mcp/SonarQube.Mcp.csproj` + `Program.cs` + `ServerVersion.cs` + `tests/SonarQube.Mcp.Tests/*.csproj` that compile to an empty server.
**Done when:** `.\build.ps1 Test` restores, compiles and runs zero tests green; `dotnet fallout --generate-configuration GitHubActions_build --host GitHubActions` produces `build.yml` with no diff on a second run; `.\build.ps1 PublishAot` produces an archive.

### Phase B — HTTP and wire layer
**Depends on:** A. **Parallel with:** nothing (C depends on it).
**Input:** the endpoint list in §1, the DTO inventory in §5, §3's option surface.
**Do:** `SonarQubeMcpOptions`, `StaticTokenCredential` + `AuthenticationRequiredException`, `AuthenticationHandler` (simplified), `RetryHandler` (verbatim), `SonarRequestBuilder`, `FormBody`, `PagedResult`, `SonarApiException`, `SonarDateTimeOffsetConverter`, every wire DTO, `SonarWireJsonContext`, and `SonarApiClient` with one method per endpoint returning wire DTOs. Capture the live fixtures. Write `SonarApiClientTests`, `ConfigurationTests`, `SonarDateTimeOffsetConverterTests`.
**Done when:** `dotnet test` green with ≥90 tests; every fixture deserialises; the `+0000` timestamp round-trips; a form-urlencoded body is byte-asserted; the 10,000 and page-size clamps are enforced in the client as well as at the tool layer.

### Phase C — Tools, mappers, server setup and CLI
**Depends on:** B. **Parallel with:** E (docs) once the tool table is frozen.
**Input:** §1's tool table verbatim; §6's validation inventory; §7's error table.
**Do:** `ToolDefaults`, `ToolErrors`, `ComponentKeys`, `MeasureFormatting`, `HtmlToText`, `ResultMapper`, all result records + `SonarToolJsonContext`, the four tool classes, `ServerInstructions`, `McpServerSetup` (including `ToolTypesFor`), `Cli/{CliDispatcher,CliRuntime,StatusCommand}`. Fill in `Build.cs`'s `ExpectedToolNames`.
**Done when:** `.\build.ps1 SmokeTest` passes **both** legs (19 tools, then 15 with `SONARQUBE_MCP_READ_ONLY=1`); every tool method compiles under `TreatWarningsAsErrors` with the AOT/trim analyzers on.

### Phase D — The test suite
**Depends on:** C. **Parallel with:** E.
**Do:** every test in §8 not already written in B — `ToolInventoryTests`, `ReadOnlyModeTests`, `ToolSchemaTests`, `NoStdoutWritesTest`, `ApiParameterContractTests` (both flavours), `ToolBehaviourTests`, `ToolErrorMappingTests`, `ResultMapperTests`, `MeasureFormattingTests`, `HtmlToTextTests`, `StatusCommandTests`.
**Done when:** ~320 tests green; the offline drift test's discriminating-guard is proven by temporarily injecting a bogus parameter; `NoStdoutWritesTest` scans >30 files and still finds usages in `Cli/`.

### Phase E — Docs, manifests and the agent skill
**Depends on:** C's frozen tool table. **Parallel with:** D.
**Do:** `AGENTS.md` (hard rules incl. **the no-transcription rule**, D1–D29, the tool table with the four judgement calls argued, layout, build, testing, release engineering); `CLAUDE.md` = `@AGENTS.md`; `README.md` (tool table, install, token creation walkthrough, client config snippets for Claude Code / VS Code / Claude Desktop, the env var table from §3, a worked "triage this project" walkthrough, troubleshooting, security, building); `CHANGELOG.md` 1.0.0 entry naming all 19 tools; `.mcp/server.json`; `.claude-plugin/{marketplace.json,plugin.json}`; `.claude/skills/sonarqube-code-quality/SKILL.md` — the call *order* (`listProjects`→`getQualityGateStatus`→`searchIssues`→`getRule`→fix; hotspots as a separate pass; `getIssue` before `transitionIssue`), the recovery moves, and **when not to call the server because the local checkout already knows**.
**Done when:** `AgentSkillTests`, `McpServerManifestTests` and `PluginManifestTests` are green; `claude plugin validate . --strict` passes.

### Phase F — Live verification and release readiness
**Depends on:** D and E. **Parallel with:** nothing.
**Input:** a real SonarQube Cloud user token with issue-administration rights on a scratch project.
**Do:** run every read tool against a real token and diff the result against the fixtures; **prove or disprove that `rules/search?f=descriptionSections` returns sections to an authenticated token** and update `getRule`, its `note` and the changelog accordingly; exercise all four write tools on a scratch issue and hotspot and replace every `SYNTHETIC` fixture with a real capture; confirm `setHotspotStatus` is genuinely idempotent (and add the 400-swallow if not); confirm `hotspots/show`'s object-shaped `component`/`project`; run the opt-in `ApiParameterContractTests` against `webservices/list`; run `sonarqube-mcp status` in all four credential states; run `.\build.ps1 SmokeTest --runtime <rid>` on each of the five RIDs (or let `release.yml` do it on a throwaway `v0.0.1-test` tag, deleted afterwards); set up the nuget.org trusted-publishing policy (workflow file `publish.yml`, environment `nuget`) **before** the first real tag.
**Done when:** no fixture carries the `SYNTHETIC` marker; all five release legs are green on their own architecture; `dotnet fallout Publish --skip` fails with *"GitHub OIDC is unavailable"* under the three faked release variables — which is the success signal that the tagged path routes all the way to the token exchange.

---

## 10. Risks and gotchas the implementers must not miss

**Legal**
1. **Never transcribe SonarSource's official MCP server.** It is SSAL-licensed source-available software, not OSS. Env-var *names* and the *choice* of endpoints are facts about the API and are fine; code, comments, error strings, tool descriptions and result shapes are not. Every line here comes from `api/webservices/list`, the public docs, live probes and this repo's own patterns. This is hard rule 1 of the new AGENTS.md, above the package budget.

**API shape**
2. `+0000` timestamps break `System.Text.Json`'s default `DateTimeOffset` reader — one custom converter, or every fixture test fails on day one.
3. `api/rules/show` has **no `f` parameter** on Cloud (**C1**); rule descriptions come from `rules/search?rule_key=&f=descriptionSections`. And it may return nothing without an entitled token — `getRule` must degrade with a `note`, not throw.
4. `hotspots/change_status` rejects `ACKNOWLEDGED` on Cloud (**C2**); `hotspots/search` rejects it too, but for a different reason. Two different `resolution` value sets, one per endpoint.
5. A 401 from a **bad token** has an **empty body** (**C3**); a 401 from **no token** has the `errors` envelope. Never dereference the envelope unconditionally.
6. `authentication/validate` answers `{"valid":true}` **anonymously** (**C4**). It is a token-*rejection* probe, not a token-*presence* probe.
7. `webservices/list` is incomplete: `hotspots/search` takes `branch`/`pullRequest` and `components/search` takes `qualifiers`, undocumented but verified (**C5**). Worse, **`api/sources/lines` is not in the catalogue at all**, with or without `include_internals=true`, yet answers 200 (**C11**). The drift test must be a subset assertion with an exception list, never equality, and it must tolerate an *action* it cannot look up rather than only a parameter.
8. `measures/component_tree` caps `metricKeys` at **15** (`maxValuesAllowed`). Exceeding it is a 400 that reads like a value error.
9. `measures/search_history`'s parameter is `metrics`, not `metricKeys`; its `total` counts **analyses**, not measures.
10. `additionalFields` on measures is `[metrics, periods]` — `period` singular is a 400 (verified).
11. A metric with no data is **silently omitted**. Absent ≠ zero. `missingMetrics` exists for exactly this.
12. New-code values are in `periods[].value`, never `value`. A tool that reads `value` reports the overall number as if it were the new-code number.
13. Measure values are **always strings**, including for `INT` and `PERCENT`. `*_rating` `"1.0"` means **A**.
14. `issue.component` is a *key* (`project:path`), not a path. Join through the sibling `components[]` array or the model reads keys all day.
15. Issue `assignee` is a **login**; `hotspots/search`'s `assignee` is a **UUID**; `hotspots/show`'s `assignee` is a **login again** (**C7**). Three fields, one name, two meanings — the result records must name them differently.
16. `hotspots/show` returns `component`/`project` as **objects**; `hotspots/search` returns them as **strings**. Two DTOs. `show` also spells its comments array `comment`, singular (**C8**).
17. `sources/lines`'s `code` is **syntax-highlighted HTML**. Dropped, not parsed.
18. `sources/*` answers **404** — not 403 — for a permission problem. The error message must say so.
19. `api/projects/search` requires org-admin. `components/search` is the list-projects endpoint.
20. ~~`components/tree`'s `q` only matches within the chosen `strategy`; a filename query under the default `all` returns zero.~~ **Disproved 2026-08-11 (C9)**: the same query under the default `all` returned ten matches. `scope` is kept for ergonomics, not because of this trap; do not repeat the claim in a tool description without re-verifying it (it may hold for `strategy=children`, which is not the default).
21. `branch` and `pullRequest` are mutually exclusive **everywhere**; `pullRequest` is the SCM number as a string.
22. `createdAfter` and `createdInLast` are mutually exclusive.
23. Resolution `FALSE-POSITIVE` has a hyphen in the legacy vocabulary while `issueStatuses` uses `FALSE_POSITIVE` with an underscore. We only expose the underscore form; the validator's rejection message must not confuse the two.
24. `ps > 500` is a 400; `p * ps > 10000` is a 400. Both enforced client-side, with the second turned into an instruction.
25. POST bodies must be `application/x-www-form-urlencoded` — **not** query parameters and **not** JSON.
26. `hotspots/change_status` returns **204 with no body**. Do not try to deserialise it.
27. 429 carries **no** rate-limit headers and there are no published limits. Backoff blind, and say so in the message.
28. `qualitygates/list` uses `op`/`error`; `qualitygates/project_status` uses `comparator`/`errorThreshold` — same concept, different names. (We only use the latter, but a future `listQualityGates` must not share a DTO.)
29. `projectStatus.status = "NONE"` with empty conditions is **normal**, not an error.

**Architecture**
30. `WithToolsFromAssembly()` is IL2026 — one `WithTools<T>(jsonOptions)` per class, and the read-only mode is *the absence of a call*, not a runtime check.
31. `Destructive` defaults to **true** in the SDK. All four write tools must say `false` explicitly, and the fifteen read tools must say nothing at all — `ToolInventoryTests` asserts the *absence*.
32. Our `JsonSerializerContext` goes **first** in the chain, after `Clear()`, then `MakeReadOnly()`. Ours-second silently changes AOT behaviour.
33. The `Authorization` header goes on **each request** via the handler, never on `DefaultRequestHeaders`.
34. `TarGZipTo` hard-codes mode `0700` (R2) — the `tar` CLI, always, for the non-Windows RIDs.
35. `.gitignore` must never contain a bare `build` rule.
36. Never hand-edit `build.yml` or `publish.yml`; a run whose generated output differs *aborts*.
37. The nuget.org token exchange **requires a `User-Agent`** or Azure Front Door answers 400 before the policy is ever consulted; the audience is `https://www.nuget.org` and the field is `apiKey`.
38. `CHANGELOG.md`'s first line must parse as `# 1.0.0` — a `# Changelog` title aborts the build.
39. Adding a tool means editing **four** places: the AGENTS.md tool table, `ToolInventoryTests.ExpectedToolNames`, `Build.cs`'s `ExpectedToolNames`, and `SKILL.md` — or the build fails. In this repo it is arguably five: a *write* tool must also be added to the read-only smoke-test leg's exclusion list.

---

### Critical files for implementation

- `D:\Work\bitbucket-mcp\AGENTS.md` — the rule set, hard rules and D-decision table this repo's own AGENTS.md is derived from
- `D:\Work\bitbucket-mcp\src\Bitbucket.Mcp\Tools\PullRequestReadTools.cs` — the exact tool-method shape (attributes, parameter order, `ToolErrors.ExecuteAsync` funnel) every one of the 19 tools copies
- `D:\Work\bitbucket-mcp\src\Bitbucket.Mcp\Http\BitbucketApiClient.cs` — the handler chain, `SendJsonAsync`/`GetPageAsync` and the source-gen-only serialization discipline `SonarApiClient` adapts
- `D:\Work\bitbucket-mcp\src\Bitbucket.Mcp\Tools\ToolDefaults.cs` and `...\Tools\ToolErrors.cs` — the validation and error-funnel templates §6 and §7 extend
- `D:\Work\bitbucket-mcp\tests\Bitbucket.Mcp.Tests\Tools\ToolInventoryTests.cs` and `...\Tools\ToolSchemaTests.cs` — the rule-enforcing test shapes, including the `ToolTestHost` construction through the production serializer factory
- `D:\Work\bitbucket-mcp\build\Build.cs` — the `PublishAot` + two-leg `SmokeTest` orchestration and the `CHANGELOG.md`-as-version-authority wiring
