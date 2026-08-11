I have a complete picture. Here is the blueprint report.

---

# `bitbucket-mcp` — Architecture Blueprint Report

Repo root: `D:\Work\bitbucket-mcp` (git, branch `main`, ~16 commits, latest `4ac531f Make the skill and server installable as a Claude Code plugin`)

---

## 1. Language, runtime, target framework, project layout

- **C# (LangVersion `latest`) on .NET 10**, single TFM `net10.0`, no multi-targeting.
- **SDK pin** — `D:\Work\bitbucket-mcp\global.json`:
  ```json
  { "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }
  ```
- **Solution is the new XML format**: `D:\Work\bitbucket-mcp\bitbucket-mcp.slnx` — folders `/solution items/`, `/build/`, `/src/`, `/tests/`. The build project is included with `<Build Project="false" />` because the Fallout orchestrator is already running out of `build/bin` when `Compile` builds the solution.
- **One production project + one test project** (design decision D1).

### Directory tree (tracked files only)

```
.claude-plugin/            marketplace.json, plugin.json   (repo is its own Claude Code plugin marketplace)
.claude/skills/bitbucket-pull-requests/SKILL.md
.config/dotnet-tools.json  fallout.globaltool 10.4.0
.editorconfig
.fallout/                  parameters.json, build.schema.json, temp/ (gitignored)
.github/workflows/         build.yml (generated), publish.yml (generated), release.yml (hand-written)
.mcp/server.json           MCP server manifest, packed into the nupkg at /.mcp/server.json
AGENTS.md                  32 KB — the design document / rules (CLAUDE.md is literally `@AGENTS.md`)
CHANGELOG.md               THE VERSION AUTHORITY
CLAUDE.md                  one line: `@AGENTS.md`
Directory.Build.props
Directory.Packages.props   central package management
LICENSE  README.md (36 KB)
bitbucket-mcp.slnx
build.cmd  build.ps1  build.sh   bootstrap scripts
global.json  nuget.config
build/                     Fallout orchestrator
  Build.cs, Build.Publish.cs, Build.CI.GitHubActions.cs, Configuration.cs,
  ReleaseNotes.cs, ReleaseNotesParser.cs, SemVersion.cs, Extensions/StringExtensions.cs,
  _build.csproj, Directory.Build.props, Directory.Build.targets
src/Bitbucket.Mcp/
  Program.cs                two lines
  McpServerSetup.cs         DI wiring + JSON options + tool registration + stdio run loop
  ServerVersion.cs
  Bitbucket.Mcp.csproj
  Authentication/           13 files: ICredentialProvider, StaticCredentialProvider,
                            OAuthCredentialProvider, OAuthTokenClient, TokenStore, TokenSet,
                            TokenFileEnvelope, InteractiveAuthenticator, NullInteractiveAuthenticator,
                            IInteractiveAuthenticator, LoopbackCallbackListener, BrowserLauncher,
                            CredentialProviderFactory, AuthenticationRequiredException
  Cli/                      CliDispatcher, LoginCommand, LogoutCommand, StatusCommand, CliRuntime
  Configuration/            BitbucketMcpOptions.cs  (the entire config surface)
  Diffs/                    UnifiedDiffParser, DiffTruncator, DiffFile, TruncatedDiff,
                            InlineAnchor, InlineAnchorResolver, InlineAnchorException
  Http/                     BitbucketApiClient, BitbucketRequestBuilder, AuthenticationHandler,
                            RetryHandler, BitbucketCursor, FieldSets, Page, BitbucketApiException,
                            InvalidCursorException
  Http/Models/              19 wire DTOs + BitbucketWireJsonContext
  Tools/                    PullRequestReadTools, PullRequestWriteTools, ToolDefaults, ToolErrors,
                            ResultMapper, ServerInstructions
  Tools/Models/             7 result-record files + BitbucketToolJsonContext
tests/Bitbucket.Mcp.Tests/  28 files, ~9,600 lines, ~340 [Fact]/[Theory]
  Fixtures/                 golden .json and .diff files, embedded resources
```

### `Directory.Build.props` (verbatim, key part)

```xml
<TargetFramework>net10.0</TargetFramework>
<LangVersion>latest</LangVersion>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<AnalysisLevel>latest-recommended</AnalysisLevel>
<NoWarn>$(NoWarn);CS1591</NoWarn>
...
<Deterministic>true</Deterministic>
<ContinuousIntegrationBuild Condition="'$(GITHUB_ACTIONS)' == 'true'">true</ContinuousIntegrationBuild>
<PublishRepositoryUrl>true</PublishRepositoryUrl>
<EmbedUntrackedSources>true</EmbedUntrackedSources>
<!-- CHANGELOG.md is the version authority ... The value below is only the local fallback. -->
<VersionPrefix>1.0.0</VersionPrefix>
```
No SourceLink `PackageReference` — "SourceLink ships in the SDK on .NET 8+".

---

## 2. MCP SDK and server wiring

### Packages — `D:\Work\bitbucket-mcp\Directory.Packages.props` (the complete budget)

```xml
<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
...
<!-- Exact pin: the MCP SDK's AOT/serializer contract is verified against this version only. -->
<PackageVersion Include="ModelContextProtocol" Version="[2.1.0]" />
<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.10" />
<PackageVersion Include="Microsoft.Extensions.Logging.Console" Version="10.0.10" />
<PackageVersion Include="System.Security.Cryptography.ProtectedData" Version="10.0.10" />
<!-- Test -->
<PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
<PackageVersion Include="xunit.v3" Version="3.2.2" />
<PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
<!-- build/ opts out of CPM: Fallout.Common + Fallout.Components 10.4.0 -->
```

**Four runtime packages total.** AGENTS.md hard rule 1: *"No new NuGet packages without a decision recorded in this file."*

### Entry point — `src/Bitbucket.Mcp/Program.cs`, entire file

```csharp
// The whole entry point: argv dispatch lives in CliDispatcher (D15) so that it stays testable.
return await Bitbucket.Mcp.Cli.CliDispatcher.RunAsync(args).ConfigureAwait(false);
```

### Hosting — `src/Bitbucket.Mcp/McpServerSetup.cs`

Deliberately **no generic host** (D3): a bare `ServiceCollection`, all registrations as explicit factories (constructors are `internal`, so container reflection can't see them, and it stays AOT-friendly). stdio transport only.

```csharp
var options = BitbucketMcpOptions.FromEnvironment();
var services = new ServiceCollection();

services.AddLogging(logging =>
{
    logging.SetMinimumLevel(options.LogLevel);
    logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);   // stderr only
});

services.AddSingleton(options);
services.AddSingleton(TimeProvider.System);
services.AddSingleton(sp => new TokenStore(...));
services.AddSingleton(sp => new OAuthTokenClient(...));
services.AddSingleton<IInteractiveAuthenticator>(sp => new InteractiveAuthenticator(...));
services.AddSingleton(CredentialProviderFactory.Create);
services.AddSingleton(sp => new BitbucketApiClient(
    sp.GetRequiredService<ICredentialProvider>(), sp.GetRequiredService<ILoggerFactory>()));

var jsonOptions = CreateToolSerializerOptions();

services
    .AddMcpServer(serverOptions =>
    {
        serverOptions.ServerInfo = new Implementation { Name = ServerVersion.Name, Version = ServerVersion.Value };
        serverOptions.ServerInstructions = ServerInstructions.Text;
    })
    .WithStdioServerTransport()
    // One WithTools<T>(jsonOptions) per tool class - never WithToolsFromAssembly (IL2026).
    .WithTools<PullRequestReadTools>(jsonOptions)
    .WithTools<PullRequestWriteTools>(jsonOptions);

await using var provider = services.BuildServiceProvider();
ToolErrors.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());
var server = provider.GetRequiredService<McpServer>();
await server.RunAsync(shutdown.Token).ConfigureAwait(false);
```

Shutdown is cooperative via `PosixSignalRegistration.Create(PosixSignal.SIGINT/SIGTERM, …)` with `context.Cancel = true`, wrapped in `try/catch(PlatformNotSupportedException)`.

**The serializer-options factory is the D6 rule made executable** (and is called by the tests too, so schemas can't drift):

```csharp
internal static JsonSerializerOptions CreateToolSerializerOptions()
{
    var jsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
    jsonOptions.TypeInfoResolverChain.Clear();
    jsonOptions.TypeInfoResolverChain.Add(BitbucketToolJsonContext.Default);          // OURS FIRST
    jsonOptions.TypeInfoResolverChain.Add(McpJsonUtilities.DefaultOptions.TypeInfoResolver!);
    jsonOptions.MakeReadOnly();
    return jsonOptions;
}
```

### Server instructions — `src/Bitbucket.Mcp/Tools/ServerInstructions.cs`

A ~10-line raw string literal sent at `initialize`. Budget rationale is explicit: *"It is read once per session and every line costs context, so it stays about ten lines — anything longer belongs in a parameter description, where it is read only when relevant."* Content: slugs not display names, diffstat-first, truncation flagged, opaque cursors, `codeSnippet` over line numbers, reviewer UUIDs, review states, "takes effect immediately on the real repository", first-run sign-in.

### csproj — `src/Bitbucket.Mcp/Bitbucket.Mcp.csproj`

```xml
<OutputType>Exe</OutputType>
<AssemblyName>bitbucket-mcp</AssemblyName>
<RootNamespace>Bitbucket.Mcp</RootNamespace>
<PublishAot>true</PublishAot>
<InvariantGlobalization>true</InvariantGlobalization>
<UseSystemResourceKeys>true</UseSystemResourceKeys>
<EventSourceSupport>false</EventSourceSupport>
<MetadataUpdaterSupport>false</MetadataUpdaterSupport>
<StripSymbols>true</StripSymbols>
<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
<GenerateDocumentationFile>true</GenerateDocumentationFile>   <!-- needed for IDE0005 -->
...
<IsPackable>true</IsPackable> <PackAsTool>true</PackAsTool>
<PackageType>McpServer</PackageType>            <!-- DotnetTool deliberately NOT listed alongside -->
<PackageId>bitbucket-mcp</PackageId> <ToolCommandName>bitbucket-mcp</ToolCommandName>
<RollForward>Major</RollForward>
<PackageReadmeFile>README.md</PackageReadmeFile>
...
<!-- PackTool.targets packs $(PublishDir) => `dotnet pack` runs a publish; AOT would need a RID -->
<PropertyGroup Condition="'$(RuntimeIdentifier)' == ''"><PublishAot>false</PublishAot></PropertyGroup>
<!-- Stated explicitly so they apply to every RID-less build/test/IDE session -->
<EnableAotAnalyzer>true</EnableAotAnalyzer>
<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
<EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
...
<None Include="../../.mcp/server.json" Pack="true" PackagePath="/.mcp/" />
<None Include="../../README.md" Pack="true" PackagePath="/" />
<InternalsVisibleTo Include="Bitbucket.Mcp.Tests" />
```
Deliberately absent: `RuntimeIdentifiers`, `PublishSingleFile`, `SelfContained` (D9/D10).

---

## 3. Tool definition and registration

Two classes, both `[McpServerToolType] internal sealed class` with a **private constructor** — sealed rather than `static` only because C# forbids a static class as the type argument of `WithTools<T>` (CS0718). All tool methods are **`public static`** (D8) and take `BitbucketApiClient client, BitbucketMcpOptions options` as the **first two parameters** (bound from DI, excluded from the schema via `IServiceProviderIsService`) and `CancellationToken cancellationToken = default` **last**.

### Representative tool 1 — a read tool (from `src/Bitbucket.Mcp/Tools/PullRequestReadTools.cs`)

```csharp
[McpServerTool(
    Name = "listPullRequests",
    Title = "List pull requests",
    ReadOnly = true,
    Idempotent = true,
    OpenWorld = true,
    UseStructuredContent = true)]
[Description(
    "Lists a repository's pull requests, most recently updated first. Returns a summary per pull request " +
    "(id, title, state, author, branches, url) — call getPullRequest for the description, reviewers and " +
    "approvals. Defaults to open pull requests only; pass state to widen that. Pass sourceBranch to ask " +
    "whether a branch already has a pull request, which is the check to make before createPullRequest. " +
    "Results are paginated: pass the returned nextCursor back as cursor for the next page.")]
public static async Task<PullRequestListResult> ListPullRequestsAsync(
    BitbucketApiClient client,
    BitbucketMcpOptions options,
    [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
    string repository,
    [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
    string? workspace = null,
    [Description("Which pull requests to include: OPEN, MERGED, DECLINED, SUPERSEDED or ALL (no state filter). Defaults to OPEN.")]
    string state = "OPEN",
    [Description("Restrict to one author: a Bitbucket account UUID in braced form ({...}), or a nickname. Omit for all authors.")]
    string? author = null,
    [Description("Restrict to pull requests opened from this branch, without any refs/heads/ prefix and spelled exactly. Combined with state, so pass state=\"ALL\" to find a merged or declined one too.")]
    string? sourceBranch = null,
    [Description("Pull requests per page, clamped to 1-50. Omit for Bitbucket's default.")]
    int? pageSize = null,
    [Description("Opaque cursor from a previous response's nextCursor — pass it back verbatim. Omit to start from the first page. When set, every other filter is ignored because the cursor already encodes them.")]
    string? cursor = null,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(client);

    var slug = ToolDefaults.RequireRepository(repository);
    var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
    var states = ToolDefaults.ResolvePullRequestStates(state);
    var branch = ToolDefaults.ResolveBranchFilter(sourceBranch);

    var context = new ToolCallContext("listPullRequests", resolvedWorkspace, slug);

    return await ToolErrors.ExecuteAsync(context, async () =>
    {
        var page = await client.ListPullRequestsAsync(
                resolvedWorkspace, slug, states, author, branch,
                query: null, sort: "-updated_on",
                ToolDefaults.ClampPageSize(pageSize), cursor, cancellationToken)
            .ConfigureAwait(false);

        return ResultMapper.List(page);
    }).ConfigureAwait(false);
}
```

### Representative tool 2 — a destructive write tool (from `PullRequestWriteTools.cs`)

```csharp
[McpServerTool(
    Name = "mergePullRequest",
    Title = "Merge pull request",
    ReadOnly = false,
    Destructive = true,
    Idempotent = false,
    OpenWorld = true,
    UseStructuredContent = true)]
[Description(
    "Merges a pull request into its destination branch. This rewrites the destination branch and cannot " +
    "be undone from here — read the pull request with getPullRequest and confirm it is the right one and " +
    "actually approved, and check listPullRequestStatuses for a build that has not passed, before " +
    "calling. A strategy the repository has disabled is rejected, not substituted, and a conflicting " +
    "pull request fails with a 409 rather than merging partially.")]
public static async Task<MergeResult> MergePullRequestAsync(
    BitbucketApiClient client,
    BitbucketMcpOptions options,
    [Description("Repository slug — ...")] string repository,
    [Description("The pull request number, as it appears in the pull request's URL.")] int pullRequestId,
    [Description("Workspace slug — ... Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")] string? workspace = null,
    [Description("How to merge: merge_commit, squash, fast_forward, squash_fast_forward, rebase_fast_forward or rebase_merge. Omit to use the repository's configured default.")] string? mergeStrategy = null,
    [Description("The merge commit message. Omit to let Bitbucket compose its default.")] string? message = null,
    [Description("Delete the source branch after merging. Omit to use the pull request's own close_source_branch setting.")] bool? closeSourceBranch = null,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(client);

    var slug = ToolDefaults.RequireRepository(repository);
    var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
    var id = ToolDefaults.RequirePullRequestId(pullRequestId);

    var body = new MergeRequest
    {
        Type = MergeRequestType,                        // "pullrequest_merge_parameters"
        MergeStrategy = ToolDefaults.ResolveMergeStrategy(mergeStrategy),
        Message = string.IsNullOrWhiteSpace(message) ? null : message,
        CloseSourceBranch = closeSourceBranch,
    };

    var context = new ToolCallContext("mergePullRequest", resolvedWorkspace, slug, id);

    return await ToolErrors.ExecuteAsync(context, async () =>
    {
        var dto = await client.MergeAsync(resolvedWorkspace, slug, id, body, cancellationToken).ConfigureAwait(false);
        return new MergeResult { State = dto.State, MergeCommitHash = dto.MergeCommit?.Hash };
    }).ConfigureAwait(false);
}
```

### Representative tool 3 — an idempotent write that swallows "already in that state"

```csharp
[McpServerTool(
    Name = "resolvePullRequestComment", Title = "Resolve pull request comment",
    ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
...
    if (resolved)
    {
        try { resolution = await client.ResolveCommentAsync(...).ConfigureAwait(false); }
        catch (BitbucketApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            // Bitbucket answers 409 for "already resolved". The caller asked for an end
            // state and it is already in place, which is what makes this tool idempotent.
        }
    }
    else
    {
        try { await client.UnresolveCommentAsync(...).ConfigureAwait(false); }
        catch (BitbucketApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { ... }
    }
```

### Return types / serialization

- Every tool returns `Task<T>` where `T` is a `internal sealed record` in namespace `Bitbucket.Mcp.Tools.Models` (asserted by test).
- `UseStructuredContent = true` on every tool ⇒ every tool has an `OutputSchema` (asserted).
- Result records use plain `{ get; init; }` properties, no JSON attributes — camelCase comes from the source-gen context options.

`src/Bitbucket.Mcp/Tools/Models/BitbucketToolJsonContext.cs`:
```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PullRequestListResult))]
... (~25 result types) ...
// Tool parameter types, for schema generation — required because reflection is off (D7)
[JsonSerializable(typeof(string))] [JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int))] [JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(long))] [JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(bool))] [JsonSerializable(typeof(bool?))]
internal sealed partial class BitbucketToolJsonContext : JsonSerializerContext;
```

### Shared argument handling — `Tools/ToolDefaults.cs`

Central static helpers so "sixteen tools cannot drift into sixteen different opinions": `ResolveWorkspace`, `RequireRepository`, `RequirePullRequestId`, `ClampPageSize` (1–50; *"the binding constraint is the model's context, not the API's"*), `ResolvePullRequestStates` (`ALL` → `null` = no filter), `ResolveDiffMode`, `ResolveBranchFilter` (strips `refs/heads/`, **refuses** `"` and `\` because BBQL documents no escape), `ResolveTaskState`, `ResolveMergeStrategy`, `CleanList`. Everything thrown is already an `McpException`.

---

## 4. The Bitbucket REST client

`src/Bitbucket.Mcp/Http/BitbucketApiClient.cs` — `internal sealed class … : IDisposable`, ~1,040 lines, the only thing that talks to Bitbucket.

### HTTP setup

```csharp
internal static readonly Uri DefaultBaseAddress = new("https://api.bitbucket.org/2.0/");
private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);
private const int MaxPageSize = 100;                    // Bitbucket's hard ceiling on pagelen
```

Handler chain built by hand (D5 — no `IHttpClientFactory`): **`AuthenticationHandler` → `RetryHandler` → `SocketsHttpHandler`**, ordering "load-bearing for the 401 refresh and for redirect following". Two constructors: the production one builds its own transport; a test one takes `HttpMessageHandler transport, Uri? baseAddress, TimeProvider? timeProvider`.

```csharp
private static SocketsHttpHandler CreateTransport() => new()
{
    AllowAutoRedirect = false,                       // D16 — see below
    AutomaticDecompression = DecompressionMethods.All,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(15),
};
...
_httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
_ = _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd($"{ServerVersion.Name}/{ServerVersion.Value} (+{ProjectUrl})");
// Deliberately absent: DefaultRequestHeaders.Authorization. See AuthenticationHandler.
```

Sends use `HttpCompletionOption.ResponseHeadersRead` "so that a multi-megabyte diff is streamed rather than buffered before the status code is even looked at".

### Auth (see also §5)

`ICredentialProvider.GetAuthenticationHeaderAsync` is called **per request** by `AuthenticationHandler`; the header is never on `DefaultRequestHeaders`. On a 401 with resendable content: dispose, `InvalidateAsync`, re-fetch header, retry **exactly once**.

**D16 — redirects followed by hand.** `SocketsHttpHandler` strips `Authorization` on *every* automatic redirect (same-origin included), and Bitbucket's `/diff` and `/diffstat` answer `302` to another `api.bitbucket.org` path that still needs the credential. So `AllowAutoRedirect = false`, and `AuthenticationHandler.FollowRedirectsAsync` handles 301/302/303/307/308, `GET`/`HEAD` only, ≤ 5 hops, relative `Location` resolved against the current URI, headers and `Options` copied by hand, and:

```csharp
private static bool CarriesCredential(Uri target) =>
    string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
    && string.Equals(target.Host, ApiHost, StringComparison.OrdinalIgnoreCase);   // "api.bitbucket.org"
```

### URL building — `Http/BitbucketRequestBuilder.cs`

Fluent, **relative** URLs only (`repositories/{ws}/{repo}/…`) resolved against `BaseAddress`, so "no code path here can be talked into naming a different host". `Segment(string)` runs `Uri.EscapeDataString` on everything including constants, plus `RequireSlug` which rejects blank / `.` / `..` (escaping alone doesn't stop RFC 3986 dot-segment collapse). `Query(name, string?|int?|bool?)` omits unset values ("unset" ≠ "empty string"). `QueryEach("path", paths)` → `?path=a&path=b`.

### Partial responses — `Http/FieldSets.cs`

Inclusive `fields=` lists, one const per endpoint. The stated rule: **"every paginated field set must contain `next`"** — otherwise Bitbucket returns page one with no continuation link and pagination silently truncates. Enforced by a reflection test (any const whose value contains `values.` must also contain `next`).

### Pagination — `Http/BitbucketCursor.cs` + `Http/Page.cs`

`next` URL → base64url cursor. Validation is an explicit **SSRF guard**, because *"a cursor arrives as a tool argument from the model, and the model's context is full of attacker-influenced text"*:

```csharp
internal static bool IsBitbucketApiUrl([NotNullWhen(true)] string? url)
{
    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
    return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase)   // api.bitbucket.org
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && uri.AbsolutePath.StartsWith(ApiPathPrefix, StringComparison.Ordinal);  // "/2.0/"
}
```
`MaxCursorLength = 4096`; `Base64Url.IsValid` before decode. `Encode` returns `null` for an unexpected URL — "pagination stops rather than handing out a cursor that would be rejected".

`internal sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, int? TotalSize)`.

### Retry — `Http/RetryHandler.cs`

Hand-rolled (D4 — Polly would cost 7 packages). `MaxAttempts = 4`; retries 408/429/502/503/504 + `HttpRequestException`/`IOException`; backoff `min(2^attempt × 500 ms, 20 s)` with ±25 % jitter; `Retry-After` (delta-seconds **and** HTTP-date) wins up to `MaxRetryAfter = 60 s`, beyond which the response is returned as-is. Only replayable bodies retried:

```csharp
internal static bool IsResendable(HttpContent? content) =>
    content is null or ByteArrayContent or ReadOnlyMemoryContent;
```
Rate-limit headers `X-RateLimit-Remaining` / `X-RateLimit-Reset` logged at Debug, or **Warning** below `LowRateLimitRemaining = 50`. Retry count is carried on `HttpRequestMessage.Options` via `HttpRequestOptionsKey<RetryAttemptCounter>` so the handler stays stateless.

### Serialization

Everything through `JsonTypeInfo<T>` from a source-gen context — no reflection anywhere. Request bodies:

```csharp
private static ByteArrayContent JsonBody<T>(T value, JsonTypeInfo<T> typeInfo)
{
    // Serialised to bytes rather than streamed: ByteArrayContent can be replayed, and that is
    // precisely what makes a write retryable after a 429 or a 401 credential refresh.
    var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
    content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    return content;
}
```
Responses deserialize straight off `response.Content.ReadAsStreamAsync` — *"No intermediate JSON strings exist anywhere on the success path"*.

`Http/Models/BitbucketWireJsonContext.cs` — snake_case wire contract, **never chained into the MCP options**, `[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]`, **no naming policy** — every DTO property carries an explicit `[JsonPropertyName("…")]` so "a rename or a refactor cannot silently change what goes on the wire". Closed generics registered explicitly with `TypeInfoPropertyName`:
```csharp
[JsonSerializable(typeof(PageEnvelope<PullRequestSummaryDto>), TypeInfoPropertyName = "PullRequestSummaryPage")]
```
DTOs are `internal record` / `internal sealed record` with **all-nullable** properties (a `fields=` partial response makes absence indistinguishable from unset). `PullRequestDto : PullRequestSummaryDto` so one mapper accepts both.

### Errors — `Http/BitbucketApiException.cs`

Carries `StatusCode` (including Bitbucket's non-standard **555**), the parsed `ErrorEnvelopeDto`, the raw body capped at `MaxRawBodyLength = 16 KiB`, `RetryAttempts`, `RetryAfterSeconds`. A `202` (merge queued) is deliberately turned into an exception because "this server does not poll merge tasks". `AuthenticationRequiredException` and `HttpRequestException` propagate **unwrapped** so the tool funnel can tell them apart.

---

## 5. Configuration

**Environment variables only.** No appsettings, no `Microsoft.Extensions.Configuration`, no command-line config args (D3). `src/Bitbucket.Mcp/Configuration/BitbucketMcpOptions.cs` is an `internal sealed record` read **once** at startup by `FromEnvironment()`; a second overload `FromEnvironment(Func<string,string?> read)` exists purely so tests never mutate the process environment.

**`FromEnvironment` never throws** — a malformed number or log level falls back to the documented default, "because failing startup over a typo would leave the MCP client with a dead server and no way to see why — stdout is the protocol channel".

| Variable | Default | Notes |
|---|---|---|
| `BITBUCKET_ACCESS_TOKEN` | — | Bearer. **Precedence 1** |
| `BITBUCKET_EMAIL` + `BITBUCKET_API_TOKEN` | — | `Basic base64(email:token)`. **Precedence 2** |
| `BITBUCKET_OAUTH_KEY` + `BITBUCKET_OAUTH_SECRET` | — | **Precedence 3**, browser flow |
| `BITBUCKET_OAUTH_CALLBACK_HOST` | `127.0.0.1` | |
| `BITBUCKET_OAUTH_CALLBACK_PORT` | `33418` | clamped 1–65535 |
| `BITBUCKET_DEFAULT_WORKSPACE` | — | makes `workspace` optional on every tool |
| `BITBUCKET_MCP_TOKEN_FILE` | per-OS | overrides token cache path |
| `BITBUCKET_MCP_NO_BROWSER` | `0` | accepts `1/true/yes/on`, `0/false/no/off` |
| `BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS` | `180` | 1–3600 |
| `BITBUCKET_MCP_LOG_LEVEL` | `Information` | `Enum.TryParse` + `Enum.IsDefined` (rejects `"42"`) |
| `BITBUCKET_MCP_MAX_LINES_PER_FILE` | `400` | 1–100000 |
| `BITBUCKET_MCP_MAX_DIFF_LINES` | `4000` | 1–1000000 |

Precedence is implemented in `Authentication/CredentialProviderFactory.cs`, which returns the OAuth provider **even when nothing is configured** — deliberately, because the factory runs while the container is built and "a server with no credentials at all still completes the MCP handshake, still lists its tools, and fails on the first tool call with an `AuthenticationRequiredException` that says exactly which variables to set".

**Command-line args** are verbs only, hand-dispatched in `Cli/CliDispatcher.cs` (D15 — no `System.CommandLine`): `serve` (default with no args), `login`, `logout`, `status`, `version|--version|-v`, `help|--help|-h`. Exit codes: `ExitSuccess = 0`, `ExitFailure = 1`, `ExitUsage = 2`.

---

## 6. Testing

- **xunit.v3 3.2.2 + xunit.runner.visualstudio + Microsoft.NET.Test.Sdk** (D11 — the VSTest bridge is there so stock Fallout `ITest` works unmodified).
- **No mocking or assertion libraries at all.** Hand-rolled `StubHttpMessageHandler`, `StubCredentialProvider`, `ManualTimeProvider`.
- Test csproj sets `<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>` (D7) so a missing `[JsonSerializable]` fails `dotnet test` rather than only after an AOT publish. Fixtures are `<EmbeddedResource Include="Fixtures/**" />` "so tests are path-independent".
- Cancellation everywhere via `TestContext.Current.CancellationToken`.
- ~340 tests over 28 files, ~9,600 lines. Largest: `ToolBehaviourTests` (63 tests, 1,795 lines), `BitbucketApiClientTests` (48, 1,410), `InlineAnchorResolverTests` (32, 555), `OAuthCredentialProviderTests` (19, 556).

### Mocking approach — `tests/…/StubHttpMessageHandler.cs`

FIFO queue of responders + `Fallback`, records every request with **body captured eagerly** ("because `HttpClient` disposes request content after the send completes"). Because production sets `AllowAutoRedirect=false`, "a stubbed 302 followed by a stubbed 200 exercises our own redirect code and both requests are recorded".

`ManualTimeProvider` (~40 hand-rolled lines) overrides `CreateTimer` to record `dueTime` into `Delays` and fire immediately on the thread pool — that's how the backoff schedule is asserted without spending real seconds.

### The distinctive part: **rule-enforcing tests**

There is no snapshot/Verify testing. Instead, invariants are machine-checked:

1. **`ToolInventoryTests`** — asserts the 16 names, the `Title`s, and the full annotation table against `tool.ProtocolTool.Annotations` (i.e. what an MCP client actually receives, not the attribute), plus: every tool has `OutputSchema`; tool classes are sealed with no public ctor; every method is `public static` returning `Task<T>` with `T` in `Bitbucket.Mcp.Tools.Models`; every method and every non-injected parameter has a `[Description]`; `CancellationToken` is last and optional; the two collaborators are parameters 0 and 1.
2. **`ToolSchemaTests`** — asserts `TypeInfoResolverChain[0] is BitbucketToolJsonContext` and `IsReadOnly`; then per-tool `[InlineData]` of the exact ordered property list and required list of the generated `InputSchema`; `NeverInSchema = ["client", "options", "cancellationToken"]`.
3. **`FieldSetTests`** — reflection over `FieldSets` consts: paginated ⇒ contains `next`; non-paginated ⇒ does not; plus a "the rule is discriminating" guard so it can't go vacuous.
4. **`NoStdoutWritesTest`** — walks up to `bitbucket-mcp.slnx`, scans `src/**/*.cs`, **strips comments and string/char/raw/verbatim literals**, then looks for `Console.Write|Out|Error|In|OpenStandard*|SetOut|SetError` with a word boundary (so `AddConsole` doesn't match). Two self-checks: must scan > 30 files, and must still find real usages inside `Cli/`.
5. **`AgentSkillTests`** — cross-checks every backticked tool-shaped identifier in `SKILL.md` against the reflected inventory **in both directions**, plus frontmatter `name`/`description` rules.
6. **`McpServerManifestTests`** — `.mcp/server.json` version == `CHANGELOG.md` top version; `identifier` == `<PackageId>`.
7. **`PluginManifestTests`** — marketplace `source: "./"`, skill path resolves, plugin `version` and the `dnx` pin both equal the changelog version, `userConfig` matches `.mcp/server.json` placeholder-for-placeholder including `isSecret` ⇒ `sensitive`.

### Representative test style

`ToolTestHost` builds the sixteen `McpServerTool` instances **through the production factory**:
```csharp
internal static JsonSerializerOptions SerializerOptions { get; } = McpServerSetup.CreateToolSerializerOptions();
...
tools.Add(McpServerTool.Create(method, target: null,
    new McpServerToolCreateOptions { Services = provider, SerializerOptions = serializerOptions }));
```

Behaviour tests call the static tool methods directly through the real client over the stub transport:
```csharp
[Theory]
[InlineData(0, "1")] [InlineData(-5, "1")] [InlineData(500, "50")]
public async Task PageSizeIsClampedIntoTheSupportedRange(int requested, string expectedPageLength)
{
    using var handler = new StubHttpMessageHandler();
    handler.EnqueueJson(ToolFixtures.PullRequestPage);
    using var client = ToolTestHost.CreateClient(handler);

    _ = await PullRequestReadTools.ListPullRequestsAsync(
        client, ToolTestHost.CreateOptions(), Repository, Workspace,
        pageSize: requested, cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal(expectedPageLength, Single(handler, "pagelen"));
}
```
Client tests assert URLs via `Uri.AbsolutePath` / parsed query, **never** `Uri.ToString()`, "which unescapes and would report a URL that was never sent — in particular hiding whether a hostile workspace slug was escaped".

---

## 7. Build / CI / packaging / release

### Orchestrator: **Fallout 10.4.0** (the maintained hard fork of NUKE), stable channel

- `.config/dotnet-tools.json` pins `fallout.globaltool` 10.4.0 (command `fallout`); `build/_build.csproj` references `Fallout.Common` + `Fallout.Components` 10.4.0 and opts out of CPM (`ManagePackageVersionsCentrally=false`, `UseArtifactsOutput=false`).
- `build.ps1` / `build.sh` / `build.cmd` bootstrap the SDK from `global.json` if needed, then `dotnet tool restore && dotnet fallout $args`.
- `.fallout/parameters.json` → `{ "Solution": "bitbucket-mcp.slnx" }`.

`build/Build.cs`:
```csharp
[ShutdownDotNetAfterServerBuild]
partial class Build : FalloutBuild,
    IHasSolution, IHasConfiguration, IHasArtifacts, IHasChangelog, IHasGitRepository,
    IRestore, ICompile, ITest, ICreateGitHubRelease
{
    public static int Main() => Execute<Build>(x => ((ITest)x).Test);
```
Targets: `Clean`, inherited `Restore`/`Compile`/`Test`, `PublishAot`, `SmokeTest`, `Pack`, `Publish`, `CreateGitHubRelease`.

### Versioning: **`CHANGELOG.md` is the version authority**

```csharp
protected override void OnBuildInitialized()
{
    var changelog = new ReleaseNotesParser().Parse(File.ReadAllText(ChangelogPath));
    LatestReleaseNotes = changelog.FirstOrDefault().NotNull($"{ChangelogPath} contains no parsable release section");
    Version = LatestReleaseNotes.SemVersion.ToString();
}
```
Not `RELEASE_NOTES.md` — but the same pattern (`build/ReleaseNotes.cs`, `ReleaseNotesParser.cs`, `SemVersion.cs` are hand-carried). The file is **never mutated by the build**; the first line must parse as `# 1.0.0` (a `# Changelog` title would abort). `Version` is pushed into compile/test/publish/pack via `.SetProperty("Version", Version)`. Both `CreateGitHubRelease` and `Publish` refuse unless `GITHUB_REF_NAME == $"v{Version}"`.

### Packaging — two channels

1. **Native AOT single binary per RID (primary).** `PublishAot` target: `DotNetPublish(-r <rid> --self-contained -c Release)`, asserts the executable exists, stages exactly `executable + LICENSE + README.md`, then `.zip` on Windows / **the `tar` CLI** on Unix. Documented reason (R2): `CompressionExtensions.TarGZipTo` goes through SharpZipLib's `TarEntry.CreateEntryFromFile` which hard-codes mode `0700`, shipping the binary unreadable and LICENSE executable.
2. **NuGet `dnx` tool package (convenience, D17).** `PackAsTool` + `PackageType=McpServer`, framework-dependent, `RollForward=Major`, `.mcp/server.json` packed to `/.mcp/`.

### `SmokeTest` — the end-to-end gate

Depends on `PublishAot`; spawns the published binary and drives a **real stdio JSON-RPC exchange**:
```csharp
"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"fallout-smoketest","version":"1.0.0"}}}""",
"""{"jsonrpc":"2.0","method":"notifications/initialized"}""",
"""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
```
asserting `serverInfo.name == "bitbucket-mcp"` and that `tools/list` returns exactly `ExpectedToolNames`. stdin is held open until both responses arrive ("EOF on stdin is the server's shutdown signal"); 30 s timeout; stderr captured for diagnostics.

### Workflows

| File | Origin | Trigger | Does |
|---|---|---|---|
| `.github/workflows/build.yml` | **generated** from `[GitHubActions("build", …)]` | push/PR to `main` | one Ubuntu job: `dotnet fallout Test SmokeTest` |
| `.github/workflows/publish.yml` | **generated** from `[GitHubActions("publish", …)]` | `v*.*.*` tags | `dotnet fallout Compile Test Pack Publish`, `environment: nuget`, `permissions: {id-token: write, contents: read}`, `env: NUGET_USER: lahma` |
| `.github/workflows/release.yml` | **hand-written by design** (generator has no matrix support) | `v*` tags | 5-RID matrix (`linux-x64`/`ubuntu-latest`, `linux-arm64`/`ubuntu-24.04-arm`, `win-x64`/`windows-latest`, `win-arm64`/`windows-11-arm`, `osx-arm64`/`macos-latest`), each running `fallout SmokeTest --runtime <rid>`, then a `release` job running `fallout CreateGitHubRelease` |

Hard rule 2: **never hand-edit `build.yml` / `publish.yml`** — a run whose generated output differs *aborts*. Actions pinned: `checkout@v7`, `setup-dotnet@v6` (`global-json-file: global.json`), `upload-artifact@v7`, `download-artifact@v8`, `cache@v6`. `fetch-depth: 0` everywhere. `fail-fast: false` on the matrix.

**Never ship a binary that has not answered a handshake on its own architecture** — that's why arm64 legs run on arm64 runners rather than cross-compiling.

### NuGet **trusted publishing** — `build/Build.Publish.cs`

No API key exists anywhere. The OIDC exchange is C# **inside the build**, not the `NuGet/login` marketplace action ("auditable alongside everything else and adds no third-party action to the release path"):

```csharp
const string NuGetAudience = "https://www.nuget.org";       // NOT "nuget"
const string NuGetTokenServiceUrl = "https://www.nuget.org/api/v2/token";
const string NuGetUserAgent = "bitbucket-mcp-build/1.0";
...
// REQUIRED. nuget.org's token endpoint sits behind Azure Front Door, which answers
// HTTP 400 "A User-Agent header is required." to a request that carries none...
client.DefaultRequestHeaders.UserAgent.ParseAdd(NuGetUserAgent);
```
Reads `ACTIONS_ID_TOKEN_REQUEST_URL` / `_TOKEN`, GETs the id token with `&audience=…`, POSTs `{ username = NuGetUser, tokenType = "ApiKey" }`, accepts `apiKey` or `api_key`, `add-mask`s it, then `DotNetNuGetPush(--skip-duplicate)` with `degreeOfParallelism: 1`. `Publish` is `.OnlyWhenDynamic(() => ShouldPublish())` — a non-tag build is a **logged skip**, not a failure ("there is no preview feed"). `NUGET_USER` is a plain workflow `env:`, not a secret, "a secret would be masked out of exactly the error message that names it".

---

## 8. Code style conventions

- `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel latest-recommended`. "The compiler and analyzers are the lint step."
- `GenerateDocumentationFile=true` in both projects *only* so IDE0005 (unnecessary usings) runs; `CS1591` suppressed centrally.
- **File-scoped namespaces enforced as an error**: `csharp_style_namespace_declarations = file_scoped:error`, `csharp_using_directive_placement = outside_namespace:error`.
- Usings: system-first, groups **not** auto-separated, but the code style is `System.*` block, blank line, project block, blank line, third-party block.
- **`internal sealed record`** is the default for everything: options, DTOs, results, `Page<T>`. `readonly record struct` for `ToolCallContext`. `internal static class` for helpers. Classes are sealed unless there's a reason.
- Primary constructors deliberately off: `csharp_style_prefer_primary_constructors = false:none`. Expression-bodied properties/accessors/lambdas on; expression-bodied constructors off.
- Suppressed: `CA1848` (LoggerMessage delegates) and `CA1852` — "noisy for this codebase". `CA1707` off in `build/` and `tests/`.
- **Escalated to error and never silenced: `IL2026` and `IL3050`** — "AOT / trimming safety is load-bearing here".
- `IDE0005` escalated to warning (⇒ error under TWAE).
- LF everywhere via `.gitattributes` (`*.cs text eol=lf`, etc.), 4-space C#, 2-space for csproj/props/json/yml.
- Every public/internal member has an XML doc comment, and the comments explain *why* not *what* — this is the single most distinctive stylistic trait of the repo. Design decisions are cited inline by number (`(D6)`, `(D16)`).

---

## 9. README and CLAUDE.md

`CLAUDE.md` is one line: `@AGENTS.md`. **`AGENTS.md` is the single source of truth** — 443 lines.

### AGENTS.md structure and key quotes

- Opening framing: *"The point of the project is a dependency tree small enough for one person to audit — treat that as a hard constraint, not a preference."* Bitbucket Data Center is explicitly out of scope.
- **Hard rules (6):**
  1. No new NuGet packages without a recorded decision (with a *Package budget changes* table).
  2. Never hand-edit the generated `build.yml` / `publish.yml`.
  3. *"No `Console.Write*` outside `src/Bitbucket.Mcp/Cli/`. In server mode stdout **is** the MCP protocol channel."*
  4. *"Diffstat first. Never fetch a whole PR diff speculatively… Truncation must always be visible, with continuation guidance — never silent."*
  5. `.gitignore` must never contain a bare `build` rule (it would untrack the orchestrator).
  6. LF endings; `TreatWarningsAsErrors` is the lint step.
- **Design decisions D1–D17** as a table — the numbering is referenced from source comments throughout.
- **Tool table** with the four annotation flags per tool, and two rows called out as judgement calls, with reasoning: `updatePullRequestTask` is non-destructive because *"a confirmation prompt in front of every tick teaches a user to click through the prompts that matter"*; `resolvePullRequestComment` is idempotent *"which it only is because the tool makes it so"*.
- **"Adding a tool means editing this table, that test, that array and the skill — all four, or the build fails."**
- Budget-of-attention principle: *"`ServerInstructions` is paid by every session that attaches the server, which is why it stays at about ten lines; the skill's body is paid only by a session that actually starts Bitbucket work… Guidance that only matters mid-workflow belongs in the skill; guidance a client needs before the first call belongs in `ServerInstructions`."*
- Layout map, Build section, Testing section (with the list of rule-enforcing tests and "Do not delete one to make a change pass"), Release engineering (including verified findings R1/R2 and the three nuget.org gotchas).

### README.md structure (713 lines, human-facing)

`# bitbucket-mcp` → Tools table (16 rows: tool / what it does / annotations) → Install (AOT binary recommended, NuGet `dnx` second) → Authentication (3 mechanisms + OAuth consumer setup, "OAuth consumers live in **workspace settings**") → Tokens → Client configuration (Claude Code / VS Code / Claude Desktop JSON snippets) → Environment variables table → Usage (a numbered end-to-end review walkthrough) → Agent skill → Troubleshooting → Security → Building from source → Contributing → License (MIT).

---

## 10. Everything else notable

### Logging / telemetry
Console logger to **stderr only** (`LogToStandardErrorThreshold = LogLevel.Trace`), level from `BITBUCKET_MCP_LOG_LEVEL`. **No telemetry, no metrics, no OpenTelemetry.** Every log call is guarded by `_logger.IsEnabled(LogLevel.Debug)` before interpolating. `ICredentialProvider.Describe()` is contractually forbidden from returning any part of a secret: *"Implementations must never return a token, secret or password, in whole or in a truncated form that still narrows a search."*

### Safety measures
- Annotation discipline as the confirmation mechanism: `Destructive` defaults to `true` in the SDK, so the six non-destructive writes say `false` explicitly; the seven read tools omit it (asserted absent). *"Getting that wrong makes a client prompt for confirmation before every comment, or worse, not prompt before a merge."*
- SSRF-validated cursors; credential only re-attached on `https://api.bitbucket.org`; header never on `DefaultRequestHeaders`; slug guard against `..`.
- BBQL injection: values quoted+escaped in the client, and `sourceBranch` containing `"` or `\` is **refused** at the tool layer since BBQL documents no escape sequence.
- Reviewer UUIDs must be braced (`{…}`) — names/nicknames/emails rejected with an actionable message.
- Merge queued (`202`) is reported as an error rather than polled.
- Token cache: DPAPI on Windows, `0600` in a `0700` dir elsewhere, atomic write (temp + flush + rename), undecodable cache treated as absent. OAuth `state` is 128 CSPRNG bits compared in constant time.
- Error funnel: **only `McpException` escapes a tool method** (`ToolErrors.ExecuteAsync` wraps every body; `OperationCanceledException` and `McpException` pass through). Messages are written for a model mid-task — each names the parameter or env var to change and the next call to make. Status codes translated, not quoted: 400 (with field errors), 401, 403 (lists both OAuth scopes *and* Atlassian API token scopes), 404 ("Bitbucket also answers 404 — not 403 — for a private repository"), 409, 429 (quotes Retry-After), **555** ("Diff too large; use mode=diffstat then paths=[...]"), 202, ≥500, other.

### Tool naming convention
**camelCase verbNoun**, set explicitly via `[McpServerTool(Name = "…")]` — matching Atlassian's own convention "so it sits alongside the official Atlassian MCP server without a naming clash". `Title` is sentence case ("List pull requests").

### Full tool list — 16 tools

| # | Tool | One-liner | Annotations |
|---|---|---|---|
| 1 | `listPullRequests` | Lists a repo's PRs, most recently updated first; `sourceBranch` answers "does this branch already have one?" | read-only, idempotent |
| 2 | `getPullRequest` | One PR in full: description, state, branches, reviewers + approvals, url — the source of reviewer UUIDs | read-only, idempotent |
| 3 | `getPullRequestDiff` | Diffstat by default; passing `paths` switches to unified diff for those files; truncation marked inline | read-only, idempotent |
| 4 | `getPullRequestComments` | General + inline comments, oldest first, deleted filtered out | read-only, idempotent |
| 5 | `listDefaultReviewers` | Effective default reviewers (repo + inherited from project) with account UUIDs | read-only, idempotent |
| 6 | `listPullRequestStatuses` | Build/deployment/external check statuses — the merge-readiness check | read-only, idempotent |
| 7 | `listPullRequestTasks` | The PR's tasks, RESOLVED/UNRESOLVED — the outstanding work | read-only, idempotent |
| 8 | `createPullRequest` | Opens a PR (title + sourceBranch required; reviewers are UUIDs) | write, **not** destructive |
| 9 | `updatePullRequest` | Changes title/description/destination/reviewers; `reviewers` REPLACES the list | write, **destructive** |
| 10 | `addPullRequestComment` | General, reply, or inline comment anchored by `codeSnippet` (preferred) or `line`+`lineType` | write, **not** destructive |
| 11 | `resolvePullRequestComment` | Marks a thread resolved or reopens it; already-in-that-state is not an error | write, **not** destructive, idempotent |
| 12 | `addPullRequestTask` | Adds a tracked task, optionally hung off a comment | write, **not** destructive |
| 13 | `updatePullRequestTask` | Ticks a task off, reopens it, or rewrites its text | write, **not** destructive, idempotent |
| 14 | `setPullRequestReviewStatus` | Sets own review state: APPROVED / CHANGES_REQUESTED / UNAPPROVED (clears both flags) | write, **not** destructive, idempotent |
| 15 | `mergePullRequest` | Merges into the destination branch with an optional strategy | write, **destructive** |
| 16 | `declinePullRequest` | Closes without merging | write, **destructive** |

All 16 are `OpenWorld = true` and `UseStructuredContent = true`.

### Agent Skill + Claude Code plugin
`.claude/skills/bitbucket-pull-requests/SKILL.md` (108 lines) is the **only** copy — YAML frontmatter (`name`, `description`, `license`, `compatibility`) plus sections: *Review a pull request* (8 numbered steps), *Merge or decline*, *Create a pull request*, *Discipline*, *When a call fails*. It holds "what neither `ServerInstructions` nor a tool `[Description]` can, because both are scoped to one call: the **order** the calls go in… and when *not* to call the server at all because local git already knows."

`.claude-plugin/marketplace.json` + `plugin.json` make the repo its own marketplace with `source: "./"` — deliberate, because "an installed plugin cannot reference files outside its own root", and a symlink "a Windows checkout without `core.symlinks` silently turns into a text file". The plugin bundles `dnx bitbucket-mcp@1.0.0 --yes` with a `userConfig` prompt per environment variable, wired through `${user_config.…}`; secrets are marked `sensitive`.

---

## Blueprint checklist for the SonarQube Cloud MCP server

1. `global.json` pin + `Directory.Build.props` (net10.0, nullable, implicit usings, TWAE, EnforceCodeStyleInBuild, deterministic, CI build flag) + `Directory.Packages.props` with CPM + transitive pinning and an **exact pin on `ModelContextProtocol`**; `.slnx` solution.
2. `Program.cs` = one line into a `CliDispatcher`; `McpServerSetup` with a bare `ServiceCollection`, explicit factory registrations, `AddMcpServer` + `WithStdioServerTransport` + one `WithTools<T>(jsonOptions)` per class, `provider.GetRequiredService<McpServer>().RunAsync(ct)`, POSIX signal shutdown.
3. Two source-gen JSON contexts: wire (snake_case-ish, explicit `[JsonPropertyName]`, never chained) and tool-facing (camelCase, chained **first**, includes primitive parameter types). `JsonSerializerIsReflectionEnabledByDefault=false` in **both** csprojs.
4. Static tool methods with `(ApiClient client, Options options, …args…, CancellationToken ct = default)`, full `[McpServerTool(Name, Title, ReadOnly, Destructive, Idempotent, OpenWorld, UseStructuredContent)]` and a `[Description]` on the method and every model-supplied parameter; a `ToolDefaults` for shared validation and a `ToolErrors.ExecuteAsync` funnel that guarantees only `McpException` escapes.
5. Hand-built handler chain (auth → retry → transport), `FieldSets`-style response trimming if the API supports it, opaque validated cursors, `ResultMapper` boundary between wire DTOs and result records.
6. Env-vars-only config in one record with a `Func<string,string?>` overload for tests, never throwing at startup.
7. xunit.v3 with no mocking libs; `StubHttpMessageHandler` + `ManualTimeProvider`; reflection/source-scan tests for the inventory, the schemas, the no-stdout rule, and any manifest that restates the version.
8. Fallout build with `CHANGELOG.md` as version authority, a `SmokeTest` that speaks real JSON-RPC to the published binary, generated `build.yml`/`publish.yml` + hand-written matrix `release.yml`, and OIDC trusted publishing implemented in-build (remember the `User-Agent`).
