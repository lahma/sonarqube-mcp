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

## Live captures — 2026-08-11

| Fixture | Request | Notes |
|---|---|---|
| `issues-search-page.json` | `GET api/issues/search?componentKeys=quartznet_quartznet&ps=2&p=1&additionalFields=rules&issueStatuses=OPEN,CONFIRMED&s=CREATION_DATE&asc=false` | Carries **both** paging shapes at once — flat `total`/`p`/`ps` *and* a `paging` object. The first issue has 14 `flows`, which is why two issues are 5.7 KB. |
| `issues-search-empty.json` | `GET api/issues/search?componentKeys=quartznet_quartznet&ps=2&p=1&additionalFields=rules&tags=this-tag-does-not-exist-anywhere` | Empty result, both paging shapes, `total: 0`. |
| `issues-search-single.json` | `GET api/issues/search?issues=AZ_xePOumT_q4T_1FWf8&additionalFields=transitions,comments,rules,users&ps=1` | The `getIssue` shape. `transitions` and `comments` are both `[]` — anonymously there are no legal transitions, which is itself the degraded case the tool layer has to render. |
| `hotspots-search-page.json` | `GET api/hotspots/search?projectKey=quartznet_quartznet&ps=2&p=1` | `component`/`project` are **strings**; `assignee` is a **UUID** (`AYgE5F7pEoXHSow6lKjD`). |
| `hotspots-show.json` | `GET api/hotspots/show?hotspot=AZvu8ZyfNsnCVHe5poFs` | `component`/`project` are **objects**; `assignee` is a **login** (`lahma@github`); the comments array is spelled **`comment`**, singular; carries a `users[]` sidecar and `canChangeStatus: false`. |
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
| `rules-search-rule-key.json` | `GET api/rules/search?organization=quartznet&rule_key=csharpsquid:S2259&f=descriptionSections,name,cleanCodeAttribute,impacts,repo,langName,securityStandards,tags&ps=1` | **The degraded anonymous response**: the `f` list is accepted, the request succeeds, and `descriptionSections` is simply not in the answer. `requiredEntitlements: []` is present. Flat paging only. |
| `error-400-page-size.json` | `GET api/issues/search?componentKeys=quartznet_quartznet&ps=501` | `'ps' value (501) must be less than 500`. |
| `error-400-result-cap.json` | `GET api/issues/search?componentKeys=quartznet_quartznet&ps=100&p=101` | `Can return only the first 10000 results. 10100th result asked.` |
| `error-401-authentication-required.json` | `GET api/projects/search?organization=quartznet&ps=1` | The **no-credential** 401: `{"errors":[{"msg":"Authentication is required"}]}`. Also the proof that `projects/search` needs org-admin, which is why `components/search` is the list-projects endpoint. |
| `error-404-component-not-found.json` | `GET api/measures/component?component=quartznet_quartznet:no/such/File.cs&metricKeys=ncloc` | `Component key 'quartznet_quartznet:no/such/File.cs' not found`. |
| `error-401-empty-body.txt` | `GET api/issues/search?…` with `Authorization: Bearer squ_0000…` | Not JSON, and not a response body: a **bad-token** 401 has `Content-Length: 0` and no body at all. The file records the exchange so the difference from the row above is written down somewhere. |

## SYNTHETIC — hand-written, replace in Phase F

These four need a token with issue-administration rights on a scratch project, which Phase B does
not have. Each is written to the wire shape the API documents and the live read fixtures confirm —
`issues/do_transition`, `issues/assign` and `issues/add_comment` all answer with the *same*
`{issue, components, rules, users}` envelope — using real keys and real component paths from the
live captures so nothing about them is shaped differently from a genuine response.

| Fixture | Stands in for | Why it cannot be captured yet |
|---|---|---|
| `issues-do_transition.json` | `POST api/issues/do_transition` (`transition=accept`) | Needs `Administer Issues` on the project. Shows the accepted state: `issueStatus: ACCEPTED`, legacy `status: RESOLVED` + `resolution: WONTFIX`, and `transitions: ["reopen"]`. |
| `issues-assign.json` | `POST api/issues/assign` | Same permission. |
| `issues-add_comment.json` | `POST api/issues/add_comment` | Same permission. Two comments, so "the newest one is the one just posted" is testable. |
| `rules-search-with-sections.json` | `GET api/rules/search?rule_key=…&f=descriptionSections,…` **with an entitled token** | Whether an ordinary authenticated token gets `descriptionSections` at all is **unproven** — see the live `rules-search-rule-key.json`, which does not. This fixture is what the tool layer's non-degraded path is written against; if Phase F finds that no ordinary token gets sections either, this fixture and that path both go. It includes a section with a `context`, which is the shape a multi-framework rule uses. |

**`hotspots/change_status` has no fixture.** It answers `204` with no body, so there is nothing to
store; a test that needs it enqueues a bodiless 204, which is precisely what the wire carries.

## Corrections to `docs/DESIGN.md` found while capturing

- **§1c tool 14 (`listMetrics`) sends an invalid `f`.** The design specifies
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
