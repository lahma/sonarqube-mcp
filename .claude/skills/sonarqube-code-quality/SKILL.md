---
name: sonarqube-code-quality
description: >-
  Triage and fix SonarQube Cloud findings through the sonarqube-mcp MCP server. Use when a task
  touches a quality gate, a Sonar issue, a security hotspot, coverage or a code metric — a red or
  missing gate on a pull request, a CI Sonar step whose result has not appeared yet, "what does
  Sonar say about this file", where a project's technical debt is concentrated, writing tests for
  uncovered lines, reviewing hotspots, or dismissing a false positive — and the sonarqube-mcp tools
  (`getAnalysisStatus`, `getQualityGateStatus`, `summarizeIssues`, `searchIssues`, `getRule`,
  `getFileCoverage`, `searchHotspots`, `transitionIssue` and the rest) are attached. Covers the
  order the calls go in: whether the analysis has finished before reading anything from it, the gate
  before the issue list, every call scoped to the same branch or pull request, one rule explanation
  per rule rather than per issue, and when the checkout already has the answer.
license: MIT
compatibility: Requires the sonarqube-mcp MCP server, attached to the client with SONARQUBE_TOKEN set. SonarQube Cloud only.
---

# SonarQube code quality

The server's `initialize` instructions already state the conventions — project keys, exclusive
`branch`/`pullRequest`, absent-is-not-zero, ratings as letters — and every tool's schema carries its
own rules. This file holds what neither can: the order the calls go in, and what to do when one of
them fails.

## A pull request whose analysis runs in CI

This is the common case and the one with a trap in it. A green Sonar step in Bitbucket Pipelines,
GitHub Actions or any other CI only means the report was **uploaded**; SonarQube processes it
afterwards. Read the gate before that finishes and it answers from the previous analysis, or from
none at all, without saying which.

1. `getAnalysisStatus` — first, whenever a build just ran. `analysisInProgress` says something is
   still queued or running; `latest` says which branch or pull request the newest finished analysis
   was for. Wait and call it again rather than trusting a gate that is about to change. A `FAILED`
   status is the other reason a pull request appears to have no findings, and it is the only place
   the scanner's error message is visible.
2. `listPullRequests` — where the `pullRequest` value comes from. It is the SCM number as a string,
   not the branch name.
3. `getQualityGateStatus` with that `pullRequest`.
4. `summarizeIssues` with the same `pullRequest` — what the change introduced, grouped, in one call.
   On a pull request the list is usually short enough to read directly with `searchIssues`; on a
   whole project this is the call that replaces paging through thousands.
5. `searchIssues`, then `getIssue` — and **pass `getIssue` the same `projectKey` and `pullRequest`**.
   An issue key belongs to one analysis scope, and SonarQube only applies the scope to a key lookup
   when the project is named with it; without both, a pull request's issue reads as "not found".

## Triage a project or a pull request

1. `listProjects` — only when the project key is unknown. It is not the repository name. Skip this
   entirely when `SONARQUBE_MCP_DEFAULT_PROJECT` is set or the key is already in the conversation.
2. `getQualityGateStatus` — **always before the issue list.** It answers whether anything is wrong
   at all, and `failingConditions` names the metrics that failed with their thresholds, which is
   what decides where to look next. `NONE` means no gate has been computed for that scope; it is not
   a failure. Pass `pullRequest` when reviewing a pull request — a gate on a pull request judges the
   change, not the project's history.
3. `searchIssues`, scoped to the same `branch` or `pullRequest` as the gate, and narrowed to what
   the gate complained about. The default is the outstanding work (`OPEN`, `CONFIRMED`). Narrow with
   `component` (a file or a directory), `impactSeverities`, `rules` or `createdInLast` rather than
   paging: only the first 10,000 results of any search are reachable.
   `summarizeIssues` first when the project is large and the question is *where* rather than
   *what*: one call returns the counts per rule, per file and per severity, and the answer usually
   names the two or three rules worth a `searchIssues` of their own.
4. `listBranches` or `listPullRequests` when the scope itself is in doubt — a branch that exists in
   git but has never been analysed is a 404 here, not an empty list, and `listPullRequests` is where
   the `pullRequest` value comes from.
5. `getRule` once per **distinct rule**, never per issue. The explanation is identical for every
   issue that rule raised, and fetching it per issue is the fastest way to fill a context window
   with duplicates.
6. `getIssue` for anything ambiguous: it adds the comments, the secondary locations that explain how
   a data-flow rule reached its conclusion, and `availableTransitions`. Pass the `projectKey` and the
   same scope the search used. `getIssueChangelog` when an issue is already `ACCEPTED`,
   `FALSE_POSITIVE` or `CONFIRMED` — somebody decided that, and the history says who and why before
   you undo it.
7. **Fix it in the checkout.** Editing the code is the outcome; the server has no fix to apply and
   nothing here changes a file.
8. `transitionIssue` only for an issue that is genuinely not a defect — `falsepositive` when the
   rule is wrong about this code, `accept` when it is right and the code stays as it is. Always with
   a `comment` explaining why: the next reader has nothing else. Never transition an issue because
   fixing it is inconvenient. `bulkUpdateIssues` when the *same* judgement applies to many issues of
   one rule — one call instead of a loop — but only after reading enough of them to know the
   judgement really is the same. It reports counts and names no issue, so an issue the transition
   was not legal from comes back as `ignored` with no way to say which.

`listComponents` turns a file you have on disk into the component key the measures tools take, and
confirms a file was analysed at all — a path SonarQube never saw is the usual reason a measures call
comes back empty.

## A coverage pass

1. `listComponentMeasures` with `sortByMetric="uncovered_lines"` and the default `ascending=false` —
   the biggest gaps first, one row per file, and no direction to get wrong: more uncovered lines is
   always worse. **Ranking by `coverage` needs `ascending=true`.** The default puts the largest
   values first, and the largest coverage is the *best* file — and even done right it answers with a
   page of tiny 0% files ahead of the one file with 173 uncovered lines that is actually the work.
   An empty ranking means nothing under that scope measures the metric at all; the note says so.
   `listMetrics` is how to find a metric key rather than guessing one; a key that does not exist is
   not an error, it is a silently missing measure.
2. `getComponentMeasures` on the project for the headline numbers, and on a single file when only
   one file is in question. Read `missingMetrics` before drawing a conclusion: a metric that is
   absent was not measured, and `newCodeValue` is where every `new_*` number lives.
3. `getFileCoverage` on the worst file — the uncovered and partially covered line numbers, which is
   the actionable form of "coverage is too low". The source text is never returned; read the file
   from disk.
4. **Write the tests**, then let the next analysis move the number. `getMeasuresHistory` answers
   whether a metric is trending the right way, which is a different question from what it is now.

## A hotspot pass

Hotspots are a separate list from issues and never appear in `searchIssues`. They are security
decisions, and the decision is a human's.

1. `searchHotspots` with `status="TO_REVIEW"` — the ones nobody has looked at.
2. `getHotspot` — the risk description, what an attacker could do, how to make it safe, and
   `canChangeStatus`, which is `false` when this token may not record a review.
3. **Read the code in the checkout** and decide.
4. `setHotspotStatus` with `REVIEWED` plus `SAFE` (looked at, not a problem here) or `FIXED` (the
   risky code was changed), always with a `comment` giving the justification. Use `TO_REVIEW` to put
   one back on the list. `ACKNOWLEDGED` is not accepted on SonarQube Cloud.

`assignIssue` and `addIssueComment` are the two ways to hand work over rather than dispose of it:
assign to a login (`ada@github`, or `__me__`), comment when the finding needs discussion. Prefer
either over a transition whenever the right answer is "someone should look at this".

## Discipline

- **The project key is not the repository name.** It is the `id` in a sonarcloud.io project URL.
  A 404 on a project you can see in a browser is almost always this.
- **`branch` and `pullRequest` are mutually exclusive**, on every tool, and omitting both reads the
  main branch. Reviewing a pull request while reading the main branch's issues is the easiest wrong
  answer available here.
- **An issue key carries its scope with it.** Whatever `projectKey` and `branch`/`pullRequest` the
  search that produced a key was given, the read of that key needs them too. Getting this wrong does
  not raise an error; it reports the issue as missing.
- **Numbers are as fresh as the last analysis.** After a push, `getAnalysisStatus` before anything
  else — a gate, an issue count or a coverage figure read mid-analysis is the previous commit's.
- **Never page past what you need.** Narrow instead: a component, a rule, a severity, a date. Fetch
  a second page because the first one ran out, not to be thorough.
- **An absent measure means not computed, not zero.** `missingMetrics` lists them. Reporting "0%
  coverage" for a project that measures no coverage is the opposite of the truth.
- **Use the modern vocabulary.** `impactSeverities` (`INFO`, `LOW`, `MEDIUM`, `HIGH`, `BLOCKER`) and
  `issueStatuses` (`OPEN`, `CONFIRMED`, `FALSE_POSITIVE`, `ACCEPTED`, `FIXED`). `MAJOR`,
  `CODE_SMELL` and `RESOLVED` are rejected, with the replacement named.
- **Writes land on the real organization immediately** and everyone in it sees them. Accepting or
  dismissing an issue changes the organization's counts and can move a quality gate. There is no dry
  run and no staging step, and `addIssueComment` is not idempotent — two calls make two comments.

## When not to call the server

The checkout is free and instant; every tool here is a network round trip.

- **File contents, whether a file exists, what a branch changed, who wrote a line, whether it
  compiles, whether the tests pass** — the checkout, `git` and the local build answer all of these.
- **What SonarQube alone knows** is its own analysis: the issues it raised, the rules behind them,
  the measures and their history, the quality gate verdict, the security hotspots and their review
  state, and per-line coverage from the last analysis. That is what the tools are for.
- Analysis results are as old as the last analysis. When the code has changed since, the fix is
  another analysis, not another query.

## When a call fails

- **"No SonarQube token is configured".** Set `SONARQUBE_TOKEN` in the environment the MCP client
  launches the server with and restart it — a running server never picks up a new value.
  `sonarqube-mcp status` reports what it currently sees.
- **404.** The project key, the branch or the pull request does not exist under that name.
  `listProjects`, `listBranches` and `listPullRequests` say what does. From `getFileCoverage` a 404
  can also mean the token lacks *See Source Code* on that project — SonarQube answers 404 rather
  than 403 there.
- **"only the first 10000 results".** Do not page; narrow. Add `component`, `rules`,
  `impactSeverities` or `createdInLast` and start again from page one.
- **400 on a transition.** The transition is not legal from the issue's current state. Call
  `getIssue` and pick from `availableTransitions`.
- **403 on a write.** The token is valid and the account lacks the permission: *Administer Issues*
  for transitioning, assigning and commenting, *Administer Security Hotspots* for a hotspot review.
  `getHotspot` reports `canChangeStatus` before the attempt.
- **429.** The client already retried with backoff, so one that reaches you means slow down: fewer
  metrics per call, a smaller `pageSize`, a narrower search.
- **An empty `getIssueChangelog`.** With no token configured this means the history could not be
  read, not that nothing happened — SonarQube answers an anonymous changelog request with an empty
  list rather than refusing it. The `note` says which case it is.
- **A gate that says `NONE`, or a pull request with no issues at all.** Check `getAnalysisStatus`
  before concluding the change is clean: an analysis still running, or one that failed, looks
  exactly like this.
- **A rule with no description sections.** The server has no token — SonarQube sends rule
  descriptions only to authenticated requests, and the `note` says so. Do not retry; the name,
  impacts and clean-code attribute are still returned, and the issue's own message is usually enough
  to act on.
