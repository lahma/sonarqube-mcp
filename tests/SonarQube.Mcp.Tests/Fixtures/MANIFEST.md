# Golden fixtures

Every `.json` file in this directory is a SonarQube Cloud response body. JSON cannot carry
comments, so this file is where each one's provenance lives: what was requested, when, and whether
the bytes came off the wire or were written by hand.

Unless a row says otherwise, a fixture is the **unmodified response body**, captured
**anonymously** (no `Authorization` header) from `https://sonarcloud.io`, organization `quartznet`,
project `quartznet_quartznet` — a public project that anyone can re-capture from. The only
normalisation applied is CRLF → LF and a trailing newline.

Two fixtures the design expected to be synthetic turned out to be capturable, and one it expected
to be capturable turned out to need no file at all. Both are noted below.

The six fixtures in the second table were captured on **2026-08-22** with a **SonarQube Cloud user
token** (login `lahma@github`, an owner of the `quartznet` organization), because none of them can
be obtained without one: three are write responses, two are rule descriptions that an anonymous
request is not given, and one is a hotspot carrying a review comment. Their one extra normalisation
is that the issue and hotspot `author` field — the SCM author of the line, which the anonymous
captures do not carry at all — was replaced with `author@example.com`. Nothing in the codebase reads
it, and a personal email address does not need a second home in a public repository.

## Live captures — 2026-08-11

| Fixture | Request | Notes |
|---|---|---|
| `issues-search-page.json` | `GET api/issues/search?componentKeys=quartznet_quartznet&ps=2&p=1&additionalFields=rules&issueStatuses=OPEN,CONFIRMED&s=CREATION_DATE&asc=false` | Carries **both** paging shapes at once — flat `total`/`p`/`ps` *and* a `paging` object. The first issue has 14 `flows`, which is why two issues are 5.7 KB. |
| `issues-search-empty.json` | `GET api/issues/search?componentKeys=quartznet_quartznet&ps=2&p=1&additionalFields=rules&tags=this-tag-does-not-exist-anywhere` | Empty result, both paging shapes, `total: 0`. |
| `issues-search-single.json` | `GET api/issues/search?issues=AZ_xePOumT_q4T_1FWf8&additionalFields=transitions,comments,rules,users&ps=1` | The `getIssue` shape. `transitions` and `comments` are both `[]` — anonymously there are no legal transitions, which is itself the degraded case the tool layer has to render. |
| `hotspots-search-page.json` | `GET api/hotspots/search?projectKey=quartznet_quartznet&ps=2&p=1` | `component`/`project` are **strings**; `assignee` is a **UUID** (`AYgE5F7pEoXHSow6lKjD`). |
| `hotspots-show.json` | `GET api/hotspots/show?hotspot=AZvu8ZyfNsnCVHe5poFs` | `component`/`project` are **objects**; `assignee` is a **login** (`lahma@github`); the comments array is spelled **`comment`**, singular; carries a `users[]` sidecar and `canChangeStatus: false`. **Re-capturing this will not match**: on 2026-08-22 the verification below left one review comment and four changelog entries on that hotspot, which no API can remove. The file is kept as captured because the empty `comment` array and the anonymous `canChangeStatus: false` are what it is here to show. |
| `measures-component.json` | `GET api/measures/component?component=quartznet_quartznet&metricKeys=ncloc,coverage,new_coverage,sqale_rating&additionalFields=periods` | **The silent-omission case.** Four metrics requested, two returned: `coverage` and `new_coverage` are simply absent, not zero. Also shows `sqale_rating: "1.0"` (meaning A) and `component.id` rather than `component.uuid`. |
| `measures-component-periods.json` | `GET api/measures/component?component=quartznet_quartznet&pullRequest=3267&metricKeys=new_coverage,new_violations,coverage&additionalFields=periods` | The new-code case: `new_violations` arrives as `periods:[{index:1,value:"13"}]` with **no** `value` property at all. |
| `measures-component-tree.json` | `GET api/measures/component_tree?component=quartznet_quartznet&metricKeys=ncloc,complexity&strategy=leaves&qualifiers=FIL&ps=2&p=1&s=metric&metricSort=ncloc&metricSortFilter=withMeasuresOnly&asc=false` | The metric-sorted "worst files" query. |
| `measures-search-history.json` | `GET api/measures/search_history?component=quartznet_quartznet&metrics=ncloc,coverage&ps=3&p=1` | `coverage`'s history entries have a `date` and **no `value`** — a gap, not a zero. `total` is 29, which counts *analyses*, not measures. |
| `metrics-search.json` | `GET api/metrics/search?ps=500` | All 155 metric definitions, 32 KB, untrimmed. **No `f` parameter** — see the corrections section. Flat paging only. |
| `qualitygates-project-status-none.json` | `GET api/qualitygates/project_status?projectKey=quartznet_quartznet` | `{"status":"NONE","conditions":[],"periods":[]}` — the normal answer for an ungated scope. |
| `qualitygates-project-status-error.json` | `GET api/qualitygates/project_status?projectKey=quartznet_quartznet&pullRequest=3267` | **Live, not synthetic** (the design expected this one to need a token). Pull request 3267 was failing its gate at capture time: `status: ERROR` with five conditions, two of them failing, all with `periodIndex: 1`. |
| `components-search.json` | `GET api/components/search?organization=quartznet&qualifiers=TRK&ps=3&p=1` | Confirms `qualifiers` works on this action despite being undocumented. |
| `components-tree-leaves.json` | `GET api/components/tree?component=quartznet_quartznet&strategy=leaves&qualifiers=FIL,UTS&ps=3&p=1` | Nested paging only; `baseComponent` carries `tags` and `visibility` that the file-level entries do not. |
| `project-branches-list.json` | `GET api/project_branches/list?project=quartznet_quartznet` | Not paginated. The main branch's `status` has the three issue counts and **no** `qualityGateStatus`. |
| `project-pull-requests-list.json` | `GET api/project_pull_requests/list?project=quartznet_quartznet` | **Trimmed.** The endpoint takes no paging parameter and returned all 116 analysed pull requests (90 KB); the first two entries are kept verbatim and re-wrapped in `{"pullRequests":[…]}`. Nothing inside an entry was altered. Between them they cover both `qualityGateStatus` values. |
| `sources-lines.json` | `GET api/sources/lines?key=quartznet_quartznet%3Asrc%2FQuartz%2FCore%2FQuartzScheduler.cs&from=940&to=945` | **Live, not synthetic** (the design warned this might 404 anonymously; it does not — `api/sources/raw` is the one that 404s). `code` is syntax-highlighted HTML. `lineHits`/`conditions`/`coveredConditions` are absent because this project publishes no coverage. |
| `rules-search-rule-key.json` | `GET api/rules/search?organization=quartznet&rule_key=csharpsquid:S2259&f=descriptionSections,name,cleanCodeAttribute,impacts,repo,langName,securityStandards,tags&ps=1` | **The degraded anonymous response**: the `f` list is accepted, the request succeeds, and `descriptionSections` is simply not in the answer. `requiredEntitlements: []` is present. Flat paging only. `rules-search-with-sections.json` below is the identical request with a token, and it does have the sections — the pair is what closes C1. |
| `error-400-page-size.json` | `GET api/issues/search?componentKeys=quartznet_quartznet&ps=501` | `'ps' value (501) must be less than 500`. |
| `error-400-result-cap.json` | `GET api/issues/search?componentKeys=quartznet_quartznet&ps=100&p=101` | `Can return only the first 10000 results. 10100th result asked.` |
| `error-401-authentication-required.json` | `GET api/projects/search?organization=quartznet&ps=1` | The **no-credential** 401: `{"errors":[{"msg":"Authentication is required"}]}`. Also the proof that `projects/search` needs org-admin, which is why `components/search` is the list-projects endpoint. |
| `error-404-component-not-found.json` | `GET api/measures/component?component=quartznet_quartznet:no/such/File.cs&metricKeys=ncloc` | `Component key 'quartznet_quartznet:no/such/File.cs' not found`. |
| `error-401-empty-body.txt` | `GET api/issues/search?…` with `Authorization: Bearer squ_0000…` | Not JSON, and not a response body: a **bad-token** 401 has `Content-Length: 0` and no body at all. The file records the exchange so the difference from the row above is written down somewhere. |

## Authenticated live captures — 2026-08-22

Captured with a user token, and with the `author` normalisation described above. The three write
responses come from one issue — `AaAmMrFelhOWn70x31R7`, an open `csharpsquid:S8970` code smell in
`src/Quartz.HttpClient/QuartzHttpClientServiceCollectionExtensions.cs` — which was transitioned,
assigned, commented on and then put back exactly as it was found. Every comment those captures
created was deleted afterwards through `api/issues/delete_comment`; the issue's `status` reads
`REOPENED` rather than `OPEN` because that is SonarQube's own record of the reopen, and there is no
way to erase it.

Note that this is a **different issue** from the one the anonymous read fixtures use
(`AZ_xePOumT_q4T_1FWf8`): the write leg needed an issue that was still open, still trivial, and
still transitionable at capture time.

| Fixture | Request | Notes |
|---|---|---|
| `issues-do_transition.json` | `POST api/issues/do_transition` with `issue=AaAmMrFelhOWn70x31R7&transition=accept` | The accepted state, exactly as the design predicted it: modern `issueStatus: ACCEPTED`, legacy `status: RESOLVED` + `resolution: WONTFIX`, and `transitions: ["reopen"]` — reopen is the only way back, which is the whole argument for the tool's non-destructive annotation. The envelope is `{issue, components, rules, users}`. |
| `issues-assign.json` | `POST api/issues/assign` with `issue=AaAmMrFelhOWn70x31R7&assignee=lahma%40github` | **Re-applying the assignee the issue already had**, which is what makes it the live proof that `assignIssue` is idempotent: `200`, same state, no error. Same envelope. |
| `issues-add_comment.json` | `POST api/issues/add_comment` with `issue=AaAmMrFelhOWn70x31R7&text=…` | The response of the **second** of two comments posted three seconds apart, so it carries both — the endpoint answers with every comment on the issue, not with the one just posted, and the mapper has to pick. Ascending by `createdAt`; the comment objects carry an `isFeedback` flag the DTO ignores. Both comments were deleted after capture. |
| `hotspots-show-with-comment.json` | `GET api/hotspots/show?hotspot=AZvu8ZyfNsnCVHe5poFs` | The same hotspot as `hotspots-show.json`, read back after a `REVIEWED`/`SAFE` review: `canChangeStatus` is now **`true`** (it is `false` anonymously), the `comment` array — singular, C8 — has a real entry, and the `changelog` is four entries deep. Worth reading before changing the changelog mapper: a status change carries **two** diffs (`status` and `resolution` move together), a diff that *sets* a value has `newValue` and no `oldValue` while one that *clears* it has the reverse, and the oldest entry is SonarQube's own severity recalculation with **no `user` at all**. |
| `rules-search-with-sections.json` | `GET api/rules/search?organization=quartznet&rule_key=csharpsquid:S2259&f=descriptionSections,name,cleanCodeAttribute,impacts,repo,langName,securityStandards,tags&ps=1` | **C1, closed.** Byte for byte the same request as the anonymous `rules-search-rule-key.json` above; the only difference is the `Authorization` header, and this one has `descriptionSections`. So the empty response is an anonymous-access restriction, not an entitlement and not a parameter error. The wire order is `how_to_fix, root_cause, resources` — there is no `introduction`, and no section carries a `context`. |
| `rules-search-with-contexts.json` | `GET api/rules/search?organization=quartznet&rule_key=javasecurity:S2076&f=…&ps=1` (same `f` list) | The per-framework shape, which `csharpsquid:S2259` does not have: `how_to_fix` appears **twice**, once per `context` (`java_lang_package` / *Java Lang Package*, `apache_commons` / *Apache Commons*), while `root_cause` and `resources` carry none. A Java rule because no C# rule in this organization's profiles has contexts. |

## Anonymous coverage captures — 2026-08-22

**A different organization and project from every other row here**: organization `apache`, project
`apache_creadur-rat` (Apache Creadur RAT — public, anonymously readable, 17 522 ncloc, `coverage`
75.9 %). The reason is simple and worth writing down rather than rediscovering: **`quartznet`
publishes no coverage report at all**, so no request against it can produce a `lineHits`, a
`conditions` or a coverage-ranked component tree. The shapes below only exist in a project that
measures coverage, and the coverage path is the one thing SonarQube knows that a checkout does not
(`getFileCoverage`'s whole reason to exist), so it is worth a second project to have them off the
wire rather than written to suit the mapper. Creadur RAT is small enough that a fifteen-line window
contains every per-line state at once.

Captured anonymously, no `Authorization` header, same CRLF → LF normalisation and nothing else.

| Fixture | Request | Notes |
|---|---|---|
| `sources-lines-with-coverage.json` | `GET api/sources/lines?key=apache_creadur-rat%3Aapache-rat-core%2Fsrc%2Fmain%2Fjava%2Forg%2Fapache%2Frat%2Fconfiguration%2Fbuilders%2FRegexBuilder.java&from=43&to=57` | **All four per-line coverage states in one 15-line window.** Partially covered: 43 and 49 (`lineHits:1`, `conditions:2`, `coveredConditions:1`). Uncovered: 50 (`lineHits:0`, no conditions) and 57 (`lineHits:0` **with** `conditions:2`, `coveredConditions:0`) — 57 is the one that proves an unexecuted branch line is uncovered and *not* also partial. Covered: 44 and 52. And nine lines — 45–48, 51, 53–56 — inside a measured file with **no `lineHits` property at all**, which is the non-executable case that must land in neither array. Every line also carries the `ut*` twins (`utLineHits`, `utConditions`, `utCoveredConditions`) that the DTO ignores. |
| `sources-lines-fully-covered.json` | `GET api/sources/lines?key=apache_creadur-rat%3Aapache-rat-core%2Fsrc%2Fmain%2Fjava%2Forg%2Fapache%2Frat%2Fannotation%2FApacheV2LicenseAppender.java&from=40&to=54` | **Measured and clean**, which is the case that used to be indistinguishable from never-measured: five lines at `lineHits:1`, ten with no `lineHits`, nothing uncovered and nothing partial. Both arrays come back empty here and in the unmeasured case, and only the `note` tells them apart — this fixture is what pins the affirmative wording. |
| `measures-component-tree-coverage-sorted.json` | `GET api/measures/component_tree?component=apache_creadur-rat&metricKeys=coverage,uncovered_lines,uncovered_conditions,ncloc&strategy=leaves&qualifiers=FIL,UTS&s=metric&metricSort=coverage&metricSortFilter=withMeasuresOnly&asc=true&additionalFields=periods&p=1&ps=5` | The `listComponentMeasures(sortByMetric:"coverage", ascending:true, scope:"files")` request, byte for byte. `paging.total` is **168**; the same query without `metricSortFilter` reports **340**, so the filter is provably doing the excluding the design relies on — and an empty page under it is a statement about the sort metric alone. `baseComponent.measures` is `[]`, and the five components come back in the order the API ranked them, which the mapper must not re-sort. |

**`quartznet` is still the default.** Only these three rows come from `apache`, and nothing in the
code cares — the fixtures are response bodies, and the project key inside them is data.

**`hotspots/change_status` has no fixture.** It answers `204` with no body, so there is nothing to
store; a test that needs it enqueues a bodiless 204, which is precisely what the wire carries.

**The plural `comments` spelling has no fixture either**, and cannot have one: SonarQube never sends
it. `ToolPayloads.HotspotShowWithBothCommentSpellings` carries both spellings by hand, because the
only way to prove the plural is ignored is to send it.

## Live captures — 2026-09-09 (1.1.0)

Captured anonymously from `https://sonarcloud.io` the same way as the 2026-08-11 set, except where
a row says otherwise. Pull request **3735** of `quartznet_quartznet` was chosen because it was the
most recently analysed pull request that still had findings (13 issues); pull-request data is purged
after about thirty days, so **these three will not re-capture** once that window passes. Re-capture
against whatever pull request is current instead, and update this table.

| Fixture | Request | Notes |
|---|---|---|
| `issues-search-by-key-pullrequest.json` | `GET api/issues/search?issues=AaCDWeEWg4L35vfD5PUF&componentKeys=quartznet_quartznet&pullRequest=3735&additionalFields=transitions,comments,rules,users&ps=1` | **The 1.1.0 bug fix's repro.** The same request without `componentKeys` answers `200` with `total: 0` — see C13. |
| `ce-component.json` | `GET api/ce/component?component=quartznet_quartznet` | Answers **anonymously** (Browse is enough), unlike `ce/activity` (401) and `ce/activity_status` (403). `current` carries `pullRequest`, `warningCount` and — note the spelling — `executedAt`, where the endpoint's own published response example says `finishedAt`. |
| `issues-search-facets.json` | `GET api/issues/search?componentKeys=quartznet_quartznet&ps=1&facets=rules,fileUuids,directories,tags,impactSeverities,impactSoftwareQualities,issueStatuses,assignees&additionalFields=rules&impactSeverities=BLOCKER` | **The facet-ignores-its-own-filter case**, and the reason this one is narrowed to `BLOCKER` rather than being the whole project: `total` is 80, while the `impactSeverities` facet reports MEDIUM in the thousands, because SonarQube computes each facet with its own filter removed. `issueStatuses` *does* reflect the filter (66 + 13 + 1 = 80), which is what makes the asymmetry visible in one file. Also carries the empty-string `assignees` bucket for unassigned issues, and the component sidecar that resolves every `fileUuids` value to a path. |
| `issues-search-facets-pullrequest.json` | `GET api/issues/search?componentKeys=quartznet_quartznet&pullRequest=3735&ps=1&facets=…&additionalFields=rules` | The same eight facets under a pull-request scope. Small, and shows a vocabulary facet returning **zero-count** buckets (`HIGH: 0`). |
| `issues-changelog-anonymous.json` | `GET api/issues/changelog?issue=AYpZ_apQ1HKzD9aWZHOq` | `{"changelog":[]}` for an issue that is `RESOLVED`/`WONTFIX` and therefore certainly *does* have history — the same anonymous-access restriction that empties a rule's description sections. This is why an empty changelog is reported with a note rather than as "nothing happened". |

### Written by hand from the API's own published response examples

Three shapes could not be captured anonymously, and two of them cannot be captured at all without
provoking a failure on a real project. Rather than guess at field names, they were taken from
`api/webservices/response_example`, which is SonarQube's own documentation endpoint — these are API
facts, retrieved on 2026-09-09, not invented shapes:

| Fixture | Source | Notes |
|---|---|---|
| `issues-changelog.json` | `response_example?controller=api/issues&action=changelog`, with the example's placeholder user and dates replaced by this organization's own | Two entries, the first carrying the two diffs a single transition produces (`resolution` and `status`). **Needs re-capturing with a token** to confirm against a live body. |
| `issues-bulk-change.json` | `response_example?controller=api/issues&action=bulk_change` | `{"total":2,"success":1,"ignored":1,"failures":0}` — counts only. The endpoint names no issue, which is why `bulkUpdateIssues` says so rather than attributing them. |
| `ce-component-failed.json` | `ce-component.json` with the `status`, `errorMessage`, `errorType` and `hasErrorStacktrace` fields of `response_example?controller=api/ce&action=component`'s failed task | The `FAILED` branch of `getAnalysisStatus`. A real capture would need a deliberately broken analysis on a public project. |

## Corrections to the implementation design found while capturing

These were found against the pre-implementation design document (since folded into `AGENTS.md`,
whose *API gotchas* section records them as C6–C11).

- **`listMetrics` was designed to send an invalid `f`.** The design specified
  `api/metrics/search?ps=500&f=name,description,domain,type,hidden`. `type` is **not** an accepted
  value: the API's own metadata lists `f` as
  `name|description|domain|direction|qualitative|hidden|decimalScale`, and the request returns
  `400 Value of parameter 'f' (type) must be one of: [...]`. Omitting `f` entirely returns every
  field including `type`, so the client sends no `f` at all. Dated note added to the design doc.
- **§1c tool 2's stated reason for the `scope` parameter is wrong.**
  `api/components/tree?component=quartznet_quartznet&q=QuartzScheduler&ps=2` — no `strategy`, so the
  API default `all` applies — returned 10 matches, not 0. The `scope` parameter is still worth
  having (it bundles `strategy` and `qualifiers` into one comprehensible choice) but the trap it is
  justified by does not reproduce as described. Phase C should re-verify before repeating the claim
  in a tool description.
- **`api/sources/lines` is absent from `api/webservices/list`** with *and* without
  `include_internals=true`, yet answers 200 anonymously. Phase D's drift test must treat it as
  verified-but-undocumented rather than expecting to find it in the catalogue.
