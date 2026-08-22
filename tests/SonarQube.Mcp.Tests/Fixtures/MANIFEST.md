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

**`hotspots/change_status` has no fixture.** It answers `204` with no body, so there is nothing to
store; a test that needs it enqueues a bodiless 204, which is precisely what the wire carries.

**The plural `comments` spelling has no fixture either**, and cannot have one: SonarQube never sends
it. `ToolPayloads.HotspotShowWithBothCommentSpellings` carries both spellings by hand, because the
only way to prove the plural is ignored is to send it.

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
