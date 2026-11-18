---
title: "1.2.0: Fallout 10.5.0, dependency refresh, API deprecations and gaps (.NET 11 deferred)"
status: in-progress
created: 2026-11-18
summary: "Stay on the .NET 10 SDK and net10.0, as agreed with bitbucket-mcp: .NET 11 gains nothing measurable here. Move to Fallout 10.5.0 and drop the NuGet.Frameworks pin, refresh packages (MCP SDK 2.4.0), move the tests to NUnit on Microsoft.Testing.Platform, harden the release path, move off the two deprecated SonarQube endpoints, close the cheap gaps, and make the package listable in the MCP Registry."
---

# 1.2.0: Fallout 10.5.0, dependency refresh, API deprecations and gaps (.NET 11 deferred)

## Goal

.NET 11 shipped on 2026-11-10. For this server, the optimal next release does **not** move to it. Measured below, it gains nothing here and costs a build pin, raised platform floors and `dnx` reach.

The next release (1.2.0) instead:

- moves to Fallout 10.5.0 and removes the `NuGet.Frameworks` pin;
- refreshes every package;
- moves the tests to NUnit on Microsoft.Testing.Platform (MTP);
- hardens the release path against a silent glibc rise;
- stops depending on the two SonarQube Cloud v1 endpoints that are now deprecated;
- closes the gaps that are cheap and useful to a coding agent;
- ships the manifest fields the MCP Registry requires.

The bitbucket-mcp plan session reached the same conclusions independently, on the same stack (see *Shared with bitbucket-mcp*). Each phase can be committed and verified on its own, in order.

> [!NOTE]
> Approved 2026-11-18 as recommended, with one change: the tests move to NUnit on MTP rather than to xunit 4 under VSTest. Every phase is in 1.2.0, Phase 4b included. Phase 6 and the automated registry publishing stay deferred.

## Evidence (gathered 2026-11-18)

Measured in this worktree, and in bitbucket-mcp by its session, with the locally installed SDKs 10.0.404 and 11.0.100. Every experimental edit was reverted afterwards.

| Configuration | Result |
|---|---|
| SDK 10 + Fallout 10.5.0, no pin, `GITHUB_ACTIONS=true` | `Test` green (both repos). Restore no longer reports NU1902/NU1903 against System.Security.Cryptography.Pkcs/Xml 10.0.10: Fallout 10.4.0 brings those, 10.5.0 brings 10.0.13 |
| SDK 10 + Fallout 10.4.0, no pin, `GITHUB_ACTIONS=true` | Reproduces the August CI failure, so the local simulation does reach the in-process evaluation |
| SDK 11 + Fallout 10.5.0, no pin, `GITHUB_ACTIONS=true` | Load-order dependent. Passed here; bitbucket-mcp fails with `Could not load … NuGet.Frameworks, Version=7.11.0.0 … conflicts with what is already bound`. With a pin at 8.0.0, green in both |
| SDK 11 + `net11.0`, packages unchanged | Build clean under `TreatWarningsAsErrors` + `latest-recommended` (SDK 11's six new CA rules don't fire); 637/637 tests pass under VSTest |
| SDK 11 + `net10.0` + Fallout 10.5.0 + pin 8.0.0 + xunit 4 (`mtp-off`) + MCP 2.4.0 + M.E 10.0.15, `GITHUB_ACTIONS=true` | `Test` (Fallout reads the TRX: 637 passed), `PublishAot` and `SmokeTest` (23 tools / 18 read-only) all green |

**AOT binary, win-x64, same code:**

| Build | Size | Handshake (`initialize` + `tools/list`), median of 15 |
|---|---|---|
| net10, today's packages | 20,309,504 B | 88–90 ms |
| net11 | 20,698,624 B (+1.9%) | 96–97 ms |
| net10, refreshed packages | 20,304,896 B | 88.9 ms |

bitbucket-mcp measured the same +2% (all from the runtime; MCP 2.4.0 adds 14 KB) and equal startup.

**The `dnx` channel** (how the Claude Code plugin starts the server): `dnx mcp-sonarqube@1.1.1` takes **653 ms** to answer `tools/list`. Of that, 375 ms is JIT startup of the framework-dependent tool and about 280 ms is `dnx` itself.

**Facts that shape the decisions:**

- **Support:** .NET 11 is STS and ends 2028-11-14, the same day as .NET 10 LTS. Moving gains no support time.
- **New floors on the .NET 11 runtime:** macOS 15, x86-64-v2 on x64, LSE on Windows Arm64.
- **ModelContextProtocol has no `net11.0` build.** 2.4.0 targets net10/9/8 and netstandard2.0.
- **The one real gain of `net11.0`,** and only of `net11.0` alone (bitbucket-mcp, measured): the restored server package graph shrinks from 21 to 12 packages, because the Microsoft.Extensions abstractions, Options and Primitives move into the shared framework.
- **Multi-targeting:** `net10.0;net11.0` keeps the old graph, but works through `dnx`. SDK 10 picks `tools/net10.0` and SDK 11 picks `tools/net11.0` (bitbucket-mcp, measured).
- **Roll-forward only ever goes to a newer runtime.** A `net11.0`-only `dnx` package would not start where only .NET 10 is installed.
- **The SDK 11 pin mechanism:** SDK 11's `Microsoft.Build.dll` references `NuGet.Frameworks` **7.11.0.0**, while the SDK ships 8.0.0.0 and Fallout 10.5.0 brings 7.9.0. A pin at 8.0.0 satisfies both SDKs. Upstream: Fallout#731, fix in PR #736 (unmerged).
- **Fallout's `ITest` speaks VSTest only** (MTP support is the open Fallout#674). In MTP mode `dotnet test` rejects its positional project argument and `--logger trx`, so under MTP `Test` becomes a hand-written target, like `PublishAot` and `SmokeTest`. jint already runs NUnit 5 on MTP (`global.json` `test.runner`, `EnableNUnitRunner`, `dotnet test --project`).
- **`ubuntu-latest` moves to Ubuntu 26.04** (actions/runner-images#14748). A Linux AOT binary only runs on the same or a newer glibc than the one it was built on. bitbucket-mcp's released linux-x64 binary needs `GLIBC_2.34` at most (read from its ELF version-needs table).
- **The two deprecated endpoints:**
  - `api/qualitygates/project_status` has been deprecated since 16 September 2025, with no v2 replacement.
  - `api/hotspots/{search,show,change_status}` has been deprecated since 16 June 2026. Hotspots are migrating to issues tagged `former-hotspot`, after which the endpoints return nothing; removal follows 180 days later.
- **The quality-gate replacement is equivalent:** `measures/component?metricKeys=alert_status,quality_gate_details` carries the same conditions as `project_status`. Probed on `quartznet_quartznet` PR 4358: four identical conditions. On main, the measure is absent exactly where `project_status` answers `NONE` with no conditions.
- **MCP Registry:** neither `io.github.lahma/sonarqube-mcp` nor bitbucket-mcp is listed. Listing requires:
  - the NuGet `registryBaseUrl` to be exactly `https://api.nuget.org/v3/index.json`, or omitted (ours is rejected);
  - a `description` of at most 100 characters (ours is 114);
  - an `mcp-name:` marker in the README of the *published package version*;
  - the current schema, `2025-12-11` (older ones only warn).

## Decisions (approved 2026-11-18)

1. **SDK and TFM.** Recommended, and agreed with bitbucket-mcp: **stay on SDK 10 (`global.json` 10.0.100) and `net10.0`** for 1.2.0.
   - SDK 11 with `net10.0` would buy only a compiler and analyzers we don't use yet, and it would bring the pin back at 8.0.0.
   - `net11.0` would buy only the smaller package graph, at the cost of `dnx` reach and the raised floors.

   Move when one of these happens:
   - .NET 12 LTS ships;
   - RID-specific AOT tool packages (decision 3) take the `dnx` channel off the user's runtime, after which `net11.0`, or a multi-target, costs no reach;
   - a .NET 11 feature is actually needed.

   If SDK 11 is chosen anyway: `global.json` 11.0.100 plus a `NuGet.Frameworks` 8.0.0 pin in `build/_build.csproj`. Both were verified green above.
2. **Gap scope.** Recommended: Phase 4a (deprecations and correctness) is in; Phase 4b (four small additions to the tool surface) is in only if wanted. Each 4b item adds parameters every call pays for (*budget of attention*).
3. **RID-specific AOT tool packages** (`mcp-sonarqube.<rid>`, plus a pointer package and an `any` fallback). They cut `dnx` startup from about 650 ms to about 370 ms and decouple the `dnx` channel from the TFM, but they restructure the release path. Recommended: **a separate plan for 1.3.0**, which bitbucket-mcp also defers (Phase 6 is the sketch).

## Phase 1: Fallout 10.5.0, drop the pin

Lands first and alone: it fixes the build-only advisories even if nothing else ships.

- [x] `build/_build.csproj`: `Fallout.Common` and `Fallout.Components` to 10.5.0. Remove the `NuGet.Frameworks` 7.9.0 reference and its comment.
- [x] `.config/dotnet-tools.json`: `fallout.globaltool` 10.5.0.
- [x] `build.ps1`: add `$env:DOTNET_INSTALL_SKIP_TAR = "1"` from Fallout 10.5.0's bootstrap template (Fallout#730). The repo's copy is a one-time snapshot of the template.
- [x] Commit the regenerated `.fallout/build.schema.json` (adds the `Information` verbosity; the description now says Fallout instead of NUKE). `build.yml` and `publish.yml` regenerate unchanged.
- [x] AGENTS.md:
  - *Package budget*, `build/` line: Fallout 10.5.0, no `NuGet.Frameworks`.
  - *Package budget changes*: a new dated entry saying the pin was removed, why (Fallout#638 fixed in 10.5.0 by #677), that NU1902/NU1903 are gone, and that SDK 11 would need a pin at 8.0.0 (Fallout#731). The 2026-08-22 entry's "remove once" condition is now met; say so there.
  - *Build*: 10.4.0 → 10.5.0.
- [x] Verify: `dotnet fallout Test` both plainly and on the CI path (recipe below); the restore shows no NU190x.

## Phase 2: Release-path hardening

- [ ] `.github/workflows/release.yml` (hand-written): run `linux-x64` on `ubuntu-24.04`, not `ubuntu-latest`, with a comment saying why. Update the RID table in AGENTS.md.
- [ ] A glibc floor check in `Build.cs`'s `PublishAot` for `linux-*` RIDs. Read the `GLIBC_x.y` version requirements from the published binary and fail when the highest exceeds a recorded constant. Take the baseline from the v1.1.1 linux assets.
  - Without the check, a runner change raises the floor silently.
  - bitbucket-mcp is building the same check; share one implementation shape.
  - Record the floor as an R-property in *Release engineering*.
- [ ] `release.yml`: add `workflow_dispatch` and gate the `release` job on `startsWith(github.ref, 'refs/tags/')`. That proves all five legs (handshake and glibc check) before a tag exists. Today a broken leg first shows up at release time.
- [ ] AGENTS.md: add **D32**. The SDK and `TargetFramework` follow the LTS line until a measured gain or the `dnx` channel allows otherwise (decision 1). Update the heading to D1–D32.
- [ ] Verify: `SmokeTest` locally; on GitHub, a manual `release` dry run with all five legs green.

## Phase 3: Package refresh

- [ ] `Directory.Packages.props`, app group: `ModelContextProtocol` `[2.4.0]` (stays an exact pin), and `Microsoft.Extensions.DependencyInjection` / `Logging.Console` 10.0.15. That's the TFM's servicing line; 11.0.0 would drag the 11.x abstractions in as packages.
- [ ] MCP SDK behaviour to pin down with tests (written in NUnit, after Phase 3b):
  - 2.4.0 matches protocol property names case-sensitively (#1934). Add a test that a mis-cased argument does not silently bind.
  - Since 2.3.0, `tools/list` keeps registration order. `SmokeTest` compares sets, so nothing depends on order; leave it that way.
- [ ] Verify: Test plus SmokeTest on the CI path.

## Phase 3b: NUnit on Microsoft.Testing.Platform

- [ ] `global.json`: `"test": { "runner": "Microsoft.Testing.Platform" }`.
- [ ] Test packages: `NUnit` 5.0.0 and `NUnit3TestAdapter` 6.3.1 (`EnableNUnitRunner`) replace `xunit.v3`, `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk`. Still no mocking or assertion libraries.
- [ ] Convert every test to NUnit's constraint model, with the same class and test names and the same count (637 run, 2 skipped).
  - `[assembly: FixtureLifeCycle(LifeCycle.InstancePerTestCase)]` keeps xunit's one-instance-per-test semantics.
  - The live-test gate becomes `Assert.Ignore`.
- [ ] `build/Build.cs`: drop `ITest`. A hand-written `Test` target runs `dotnet test --project` after `Compile` and fails on a non-zero exit. The target name stays `Test`, so the generated workflows don't change.
- [ ] AGENTS.md: rewrite D11 and *Testing*, the `tests/` budget line, and a *Package budget changes* entry for the swap. That entry retires the 2026-09-09 reasoning for holding xunit at 3.x.
- [ ] Verify: `dotnet test --project tests/SonarQube.Mcp.Tests`, then `dotnet fallout Test SmokeTest` plainly and on the CI path.

## Phase 4a: Deprecations and correctness

- [ ] **`getQualityGateStatus` reads `measures/component`** with `metricKeys=alert_status,quality_gate_details` and the same branch/pullRequest scope. Name and `QualityGateResult` stay; the mapping is field for field.
  - `quality_gate_details` is JSON inside a string: a second source-generated `Deserialize`, no converter (D6, D7).
  - An absent measure means `NONE` with the existing note (D25).
  - Remove `GetQualityGateProjectStatusAsync`, `ProjectStatusResponseDto` and their fixture and contract rows, unless a test still needs the old shape.
  - Capture the new fixtures live (a PR scope and a `NONE` scope), with `Fixtures/MANIFEST.md` rows.
  - Add an *API gotchas* entry with the deprecation date and the probe.
- [ ] **Hotspots are being retired.**
  - Reword `ServerInstructions.cs:32` and `SKILL.md:109`, which say hotspots never appear in `searchIssues`. After migration they do, tagged `former-hotspot`.
  - An empty `searchHotspots` result gets a `note` pointing to `searchIssues` with `tags=former-hotspot` and `impactSoftwareQualities=SECURITY`.
  - The three hotspot tool descriptions say the endpoints are deprecated.
  - Record the dates and the status mapping in *API gotchas*.
  - Removing the three tools is **not** part of 1.2.0. It happens when the endpoints come back empty for a migrated project (23 → 20 tools, 5 → 4 writes, every count and both SmokeTest legs).
- [ ] **SCA ratings become letters.** `MeasureFormatting` translates only keys ending `_rating`, so the ten `sca_rating_*` / `new_sca_rating_*` metrics reach the model as `"1.0"`–`"5.0"` (D25). Match the `_rating_` infix as well (or decide by metric type), with a unit test.
- [ ] **`transitionIssue`'s fallback re-read is unscoped** (`IssueWriteTools.cs:252–271`). It sends `issues=KEY` alone, the C13 trap, so `availableTransitions` silently comes back null for a pull-request issue. Pass the project and scope through, or return a note naming `getIssue`, and add a PR-scoped test next to `ToolBehaviourTests.cs:2029`.
- [ ] AGENTS.md *API gotchas*: `facetMode` (sent by `summarizeIssues` `countBy=effort`) has been deprecated since 7.9; note it.

## Phase 4b: Small additions (only if decision 2 says so)

Every item touches the frozen schema table in `ToolSchemaTests` and `ApiParameterContractTests`' `SonarApiParameters.Table`, and, where it changes what a model should do, the skill and README.

- [ ] `fixableByAgent` (bool) on `searchIssues` and `summarizeIssues`. It's undeclared in the catalogue, so add it to `UndocumentedButVerified`. Probed: `true` gives 796 of 5,258 issues, while `false` doesn't filter at all; the description must say both.
- [ ] `ruleContext` on issue results (from `ruleDescriptionContextKey`, already deserialised and dropped), plus a `context` argument on `getRule` that keeps only the matching `how_to_fix` section (D26).
- [ ] An `authors` filter on `searchIssues`, and `author` on the issue result. The scenario is "the issues I introduced". Anonymous responses omit `author`, so the description says it needs a token.
- [ ] `summarizeIssues` `groupBy` gains the security-standard facets (`owaspTop10-2021`, `cwe`, `sonarsourceSecurity`). That's a new value, not a new parameter. These grow in importance as hotspots become issues.

## Phase 5: MCP Registry readiness

The registry reads the README of the version being published, so the marker has to ship inside a package. 1.2.0 carries the manifest fixes; the publishing itself follows.

- [ ] `.mcp/server.json`:
  - `$schema` → `2025-12-11`;
  - `registryBaseUrl` → `https://api.nuget.org/v3/index.json`;
  - `description` at most 100 characters.
- [ ] `McpServerManifestTests` asserts those three.
- [ ] `README.md`: `<!-- mcp-name: io.github.lahma/sonarqube-mcp -->`. It's packed into the nupkg.
- [ ] *Follow-up, shared with bitbucket-mcp; not in 1.2.0:* a `PublishMcpRegistry` target that does the GitHub OIDC → registry token exchange in C# inside the build, the way D17's NuGet exchange works, rather than downloading `mcp-publisher`. It waits until nuget.org serves the version, then publishes. Verify the registry's auth and publish endpoints against its source before writing it. Registry versions are immutable and the registry is still a preview. Until then a first listing can be done by hand with `mcp-publisher`.

## Phase 6: RID-specific AOT tool packages (a separate plan, 1.3.0)

Sketch, to be planned on its own (decision 3):

- Each `release.yml` leg also runs `dotnet pack -r <rid>` with `ToolPackageRuntimeIdentifiers`.
- One leg packs the pointer package and an `any` framework-dependent fallback (`-r any -p:PublishAot=false`).
- The trusted-publishing job pushes all of them, RID packages first and the pointer last.

The open design question is how that job gets the nupkgs. The policy names `publish.yml`, D17 keeps it independent of `release.yml`, and a Fallout-generated workflow can't build a matrix. Once this ships, the `dnx` channel no longer depends on the user's runtime, and decision 1 can be revisited.

## Phase 7: Release 1.2.0

- [ ] `CHANGELOG.md` `# 1.2.0` entry. Bullets only; it is the version authority.
- [ ] Versions: `.mcp/server.json` (both fields), `.claude-plugin/plugin.json` (version and `dnx` pin), and `VersionPrefix` in `Directory.Build.props`. `PluginManifestTests` and `McpServerManifestTests` enforce the first two.
- [ ] `README.md`: the hotspot deprecation, any 4b parameters, and one sentence that `HTTPS_PROXY`/`NO_PROXY` are honoured.
- [ ] CI green, `release` dry run green on all five legs. The user tags.

## Verification recipe (CI path, locally)

`ITest` evaluates projects in-process only when GitHub Actions is detected. Set these for **one** process (PowerShell, one tool call):

`GITHUB_ACTIONS=true`, plus `GITHUB_WORKSPACE`, `GITHUB_REPOSITORY`, `GITHUB_REF`, `GITHUB_REF_NAME`, `GITHUB_SHA`, `GITHUB_RUN_ID`, `GITHUB_RUN_NUMBER`, `GITHUB_WORKFLOW`, `GITHUB_JOB`, `GITHUB_SERVER_URL`, and `GITHUB_STEP_SUMMARY`/`GITHUB_OUTPUT`/`GITHUB_ENV` pointing at temporary files.

Then run `dotnet fallout Test SmokeTest`. The local AOT link needs `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on `PATH`.

## Shared with bitbucket-mcp

The bitbucket-mcp plan session (`01a1ef8d`) planned the same move on the same stack, and the two sessions exchanged findings.

**Agreed:**
- SDK 10 and `net10.0` for the next release;
- Fallout 10.5.0 with the pin removed (8.0.0 if a repo moves to SDK 11);
- Microsoft.Extensions 10.0.15;
- MCP SDK 2.4.0;
- xunit 4 through `mtp-off`;
- `ubuntu-24.04` plus a glibc floor check;
- the `DOTNET_INSTALL_SKIP_TAR` bootstrap line;
- RID-specific tool packages as a separate later plan;
- registry publishing by an in-build exchange, later.

**Differs:** this plan ships the registry manifest fixes and the README marker in 1.2.0, because the marker must be inside a published package; bitbucket-mcp defers the whole registry topic.

The glibc check and the registry exchange are the same code in both repos: build it once and port it.

## Out of scope

- **v2-only Cloud features** (`api.sonarcloud.io`): the dependency-risk list, architecture data, AI Code Assurance, Quality Gates v2.
- **AI CodeFix:** no v1 action, and it needs a paid plan.
- **MCP features:** prompts, resources and completions would duplicate the skill; progress contradicts D30; logging, sampling and roots are deprecated by spec 2026-07-28.
- **Runtime async:** still a preview on .NET 11 and requires `net11.0`.
- **Organization-wide issue search:** D20 stands.