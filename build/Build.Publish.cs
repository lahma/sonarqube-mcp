using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Xml.Linq;

using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Utilities.Collections;
using Fallout.Common.Utilities.Net;

using Serilog;

using static Fallout.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// The NuGet distribution channel (D17): RID-specific Native AOT tool packages behind one pointer
/// package, pushed to nuget.org with a short-lived API key minted from the workflow's GitHub OIDC
/// token (trusted publishing - no stored NuGet API key anywhere), then the MCP Registry entry.
/// </summary>
/// <remarks>
/// <para>
/// <c>dnx mcp-sonarqube</c> resolves the pointer package, which names one package per runtime; the
/// CLI then fetches the one for the user's platform, or the framework-dependent <c>any</c> package on
/// a platform without one. Native AOT cannot cross-compile across operating systems, so each RID
/// package is built and handshake-tested by its own <c>release.yml</c> leg (<c>PackRuntime</c>), and
/// the tag's <c>publish.yml</c> run collects them (<c>CollectRuntimePackages</c>) before pushing:
/// <c>publish.yml</c> is the only workflow file the trusted publishing policy names.
/// </para>
/// <para>
/// The id is <c>mcp-sonarqube</c> (D17 - nuget.org reserves both the <c>SonarQube*</c> and the
/// <c>SonarCloud*</c> prefixes to SonarSource); everything else, the installed command included, is
/// still called <c>sonarqube-mcp</c>. There is no preview feed: the only push this repository ever
/// makes is a tagged release, which is why <c>Publish</c> is gated on <see cref="Build.IsTaggedBuild"/>.
/// </para>
/// </remarks>
partial class Build
{
    const string NuGetSource = "https://api.nuget.org/v3/index.json";

    /// <summary>The OIDC audience nuget.org expects. Not <c>nuget</c>, whatever the design doc says.</summary>
    const string NuGetAudience = "https://www.nuget.org";

    const string NuGetTokenServiceUrl = "https://www.nuget.org/api/v2/token";

    /// <summary>
    /// Any non-empty User-Agent will do, but there must be one - see
    /// <see cref="GetTrustedPublishingApiKey"/>.
    /// </summary>
    const string NuGetUserAgent = "sonarqube-mcp-build/1.0";

    /// <summary>The workflow file the nuget.org trusted publishing policy is scoped to.</summary>
    /// <remarks>
    /// Only used in the error message, but it is the field people get wrong, so it is worth naming
    /// the actual value rather than describing it.
    /// </remarks>
    const string PublishWorkflowFile = "publish.yml";

    /// <summary>The workflow whose legs build the RID packages.</summary>
    const string ReleaseWorkflowFile = "release.yml";

    /// <summary>What each <c>release.yml</c> leg names the artifact holding its RID package.</summary>
    const string RuntimePackageArtifactPrefix = "tool-package-";

    const string McpRegistryUrl = "https://registry.modelcontextprotocol.io";

    /// <summary>
    /// The OIDC audience the registry checks: its own scheme and lowercase host, which is how
    /// <c>mcp-publisher</c> derives it.
    /// </summary>
    const string McpRegistryAudience = "https://registry.modelcontextprotocol.io";

    /// <summary>The service-index resource the registry reads a package's README through.</summary>
    const string ReadmeUriTemplateResource = "ReadmeUriTemplate/6.13.0";

    /// <summary>How long the publish job waits for the release legs to upload their packages.</summary>
    static readonly TimeSpan ReleaseLegsTimeout = TimeSpan.FromMinutes(60);

    /// <summary>How long the publish job waits for nuget.org to serve a pushed version's README.</summary>
    static readonly TimeSpan NuGetReadmeTimeout = TimeSpan.FromMinutes(45);

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    [Parameter("nuget.org profile name of the account that owns the trusted publishing policy " +
               "(env: NUGET_USER). Public information, not a secret.")]
    readonly string NuGetUser;

    [Parameter("A release.yml run id to collect the RID packages from, instead of the run the tag " +
               "started - how CollectRuntimePackages is checked against a manual dry run")]
    readonly long? ReleaseRunId;

    AbsolutePath PackagesDirectory => ArtifactsDirectory / "packages";
    AbsolutePath PackageCheckDirectory => ArtifactsDirectory / "package-check";
    AbsolutePath ServerManifestFile => RootDirectory / ".mcp" / "server.json";

    /// <summary>The NuGet package id, read from the project: the one name that is not <c>sonarqube-mcp</c> (D17).</summary>
    string PackageId => ServerProjectProperty("PackageId");

    /// <summary>
    /// The runtimes that get a Native AOT package: the project's <c>ToolPackageRuntimeIdentifiers</c>
    /// without <c>any</c>, which is the framework-dependent fallback. <c>ToolPackageTests</c> holds the
    /// release matrix to the same list.
    /// </summary>
    string[] NativeRuntimes => ServerProjectProperty("ToolPackageRuntimeIdentifiers")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => x != "any")
        .ToArray();

    string ServerProjectProperty(string name) =>
        XDocument.Load(ServerProject).Descendants(name).Select(x => x.Value).SingleOrDefault()
            .NotNull($"{ServerProject} declares no <{name}>");

    /// <summary>The pointer package for a null runtime, else the package for that runtime.</summary>
    AbsolutePath PackageFile(string runtime) =>
        PackagesDirectory / (runtime == null ? $"{PackageId}.{Version}.nupkg" : $"{PackageId}.{runtime}.{Version}.nupkg");

    Target Pack => _ => _
        .Description("Packs the pointer tool package and the framework-dependent 'any' fallback into " +
                     "artifacts/packages, and handshakes the fallback")
        .Produces(PackagesDirectory / "*.nupkg")
        .Executes(() =>
        {
            // Independent of Compile for the same reason PublishAot is: these are Release packs of one
            // project. Only this target's own files are replaced; CollectRuntimePackages adds the others.
            PackagesDirectory.CreateDirectory();
            PackageFile(null).DeleteFile();
            PackageFile("any").DeleteFile();

            // With PublishAot and ToolPackageRuntimeIdentifiers set, a RID-less pack builds the pointer
            // package alone - it names the RID packages and publishes nothing.
            DotNetPack(_ => _
                .SetProject(ServerProject)
                .SetConfiguration(Configuration.Release)
                .SetOutputDirectory(PackagesDirectory)
                .SetProperty("Version", Version));

            DotNetPack(_ => _
                .SetProject(ServerProject)
                .SetConfiguration(Configuration.Release)
                .SetRuntime("any")
                .SetProperty("PublishAot", false)
                .SetOutputDirectory(PackagesDirectory)
                .SetProperty("Version", Version));

            Assert.True(PackageFile(null).FileExists(), $"dotnet pack did not produce '{PackageFile(null)}'");
            Assert.True(PackageFile("any").FileExists(), $"dotnet pack -r any did not produce '{PackageFile("any")}'");

            // The fallback ships a program too, so it answers a handshake before it ships: run the way
            // the CLI runs a framework-dependent tool, `dotnet sonarqube-mcp.dll`.
            var dll = ExtractPackage(PackageFile("any")).GlobFiles($"tools/**/{ProductName}.dll").Single();
            var (tools, readOnlyTools) = AssertServerAnswers(new ServerUnderTest("dotnet", $"\"{dll}\"", dll.Parent));

            ReportSummary(_ => _
                .AddPair("Version", Version)
                .AddPair("Packages", "pointer, any")
                .AddPair("Fallback tools", $"{tools}/{readOnlyTools}"));
        });

    Target PackRuntime => _ => _
        .Description("Packs the Native AOT tool package for --runtime into artifacts/packages and " +
                     "handshakes the binary inside it")
        .Produces(PackagesDirectory / "*.nupkg")
        .Executes(() =>
        {
            Assert.True(NativeRuntimes.Contains(Runtime, StringComparer.Ordinal),
                $"'{Runtime}' is not one of the tool package's runtimes ({string.Join(", ", NativeRuntimes)}). " +
                "Add it to ToolPackageRuntimeIdentifiers in the project and to release.yml's matrix together.");

            PackagesDirectory.CreateDirectory();
            PackageFile(Runtime).DeleteFile();

            DotNetPack(_ => _
                .SetProject(ServerProject)
                .SetConfiguration(Configuration.Release)
                .SetRuntime(Runtime)
                .SetOutputDirectory(PackagesDirectory)
                .SetProperty("Version", Version));

            Assert.True(PackageFile(Runtime).FileExists(), $"dotnet pack -r {Runtime} did not produce '{PackageFile(Runtime)}'");

            // The package's binary is a publish of its own, not the archive's, so it is held to the same
            // rule: it does not ship before it has answered a handshake on its own architecture, and on
            // Linux it is held to the glibc floor (R3) as well.
            var binary = ExtractPackage(PackageFile(Runtime)).GlobFiles($"tools/**/{ExecutableName}").Single();

            if (!OperatingSystem.IsWindows())
            {
                // A zip entry carries no Unix mode; the CLI marks the binary executable when it installs it.
                File.SetUnixFileMode(binary, File.GetUnixFileMode(binary) |
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            var glibc = IsLinuxRuntime ? AssertGlibcFloor(binary) : null;
            var (tools, readOnlyTools) = AssertServerAnswers(new ServerUnderTest(binary, string.Empty, binary.Parent));

            ReportSummary(_ => _
                .AddPair("Package", PackageFile(Runtime).Name)
                .AddPair("Tools", $"{tools}/{readOnlyTools}")
                .AddPairWhenValueNotNull("Needs glibc", glibc));
        });

    Target CollectRuntimePackages => _ => _
        .Description("Downloads the Native AOT tool packages the release.yml legs built for this commit " +
                     "into artifacts/packages")
        .After(Pack)
        .OnlyWhenDynamic(() => ReleaseRunId != null || IsTaggedBuild)
        .Executes(() =>
        {
            var token = Assert.NotNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
                "Collecting the RID packages reads another workflow's artifacts, which needs GITHUB_TOKEN with " +
                "actions: read (the publish job's EnableGitHubToken; locally, `gh auth token`).");
            var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "lahma/sonarqube-mcp";

            using var github = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
            github.DefaultRequestHeaders.UserAgent.ParseAdd(NuGetUserAgent);
            github.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            github.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            github.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            var runId = ReleaseRunId ?? FindReleaseRun(github, repository);
            var wanted = NativeRuntimes.Select(x => RuntimePackageArtifactPrefix + x).ToArray();
            var artifacts = WaitForArtifacts(github, repository, runId, wanted);

            PackagesDirectory.CreateDirectory();

            foreach (var (name, id) in artifacts)
            {
                // The API answers with a redirect to a signed blob URL; HttpClient follows it and drops
                // the Authorization header on the way, which is what the blob store wants.
                var zip = PackageCheckDirectory / $"{name}.zip";
                zip.Parent.CreateDirectory();
                using (var response = github.GetAsync($"repos/{repository}/actions/artifacts/{id}/zip").GetAwaiter().GetResult())
                {
                    Assert.True(response.IsSuccessStatusCode,
                        $"Downloading artifact '{name}' of {ReleaseWorkflowFile} run {runId} failed ({(int) response.StatusCode}).");
                    using var file = File.Create(zip);
                    response.Content.CopyToAsync(file).GetAwaiter().GetResult();
                }

                ZipFile.ExtractToDirectory(zip, PackagesDirectory, overwriteFiles: true);
                Log.Information("Collected {Artifact} from {Workflow} run {Run}", name, ReleaseWorkflowFile, runId);
            }

            foreach (var runtime in NativeRuntimes)
            {
                Assert.True(PackageFile(runtime).FileExists(),
                    $"{ReleaseWorkflowFile} run {runId} uploaded no '{PackageFile(runtime).Name}': its artifact holds a " +
                    "package for another version than CHANGELOG.md's, or none at all.");
            }

            ReportSummary(_ => _
                .AddPair("Release run", runId.ToString())
                .AddPair("Collected", string.Join(", ", NativeRuntimes)));
        });

    Target Publish => _ => _
        .Description("Pushes the tool packages to nuget.org - every RID package first, the pointer last - " +
                     "using a trusted-publishing (OIDC) key")
        .DependsOn(Pack, CollectRuntimePackages)
        .OnlyWhenDynamic(() => ShouldPublish())
        .Executes(() =>
        {
            AssertPublishContext();

            var runtimePackages = NativeRuntimes.Append("any").Select(PackageFile).ToList();
            var pointer = PackageFile(null);

            foreach (var package in runtimePackages.Append(pointer))
            {
                Assert.True(package.FileExists(),
                    $"Refusing to publish an incomplete set: '{package.Name}' is missing from '{PackagesDirectory}'.");
            }

            // Minted here rather than in an earlier target or job: the key lives 15-60 minutes and
            // one OIDC token mints exactly one key.
            var apiKey = GetTrustedPublishingApiKey();

            // The CLI resolves the pointer first and the package for the user's runtime second, so a
            // pointer that is live before its packages is an install that fails. Every runtime package
            // therefore goes first, the pointer only once all of them went through, and the first
            // failure stops the push. --skip-duplicate is what makes re-running a half-finished push safe.
            PushPackages(runtimePackages, apiKey);
            PushPackages([pointer], apiKey);

            ReportSummary(_ => _
                .AddPair("Pushed", (runtimePackages.Count + 1).ToString())
                .AddPair("Version", Version));
        });

    Target PublishMcpRegistry => _ => _
        .Description("Publishes .mcp/server.json to the MCP Registry once nuget.org serves the version's README")
        .DependsOn(Publish)
        .OnlyWhenDynamic(() => ShouldPublish())
        .Executes(() =>
        {
            AssertPublishContext();

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NuGetUserAgent);

            // The registry proves ownership by finding `mcp-name: <server name>` in the README nuget.org
            // serves for this exact version, and refuses the publish until it can. So wait for that
            // README, through the same service-index resource the registry reads it from.
            var serverName = JsonNode.Parse(File.ReadAllText(ServerManifestFile))!["name"]!.GetValue<string>();
            WaitForPackageReadme(client, serverName);

            var idToken = GetGitHubOidcToken(McpRegistryAudience);

            using var exchange = client.PostAsync(
                    $"{McpRegistryUrl}/v0/auth/github-oidc",
                    JsonContent(JsonSerializer.Serialize(new { oidc_token = idToken })))
                .GetAwaiter().GetResult();
            var exchangeBody = exchange.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.True(exchange.IsSuccessStatusCode,
                $"MCP Registry token exchange failed ({(int) exchange.StatusCode}): {exchangeBody}. The job needs " +
                "'permissions: id-token: write', and the token's repository owner must own the server name's " +
                $"io.github.<owner> namespace ('{serverName}').");

            var registryToken = JsonNode.Parse(exchangeBody)!["registry_token"]!.GetValue<string>();
            GitHubActions.Instance?.WriteCommand("add-mask", registryToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{McpRegistryUrl}/v0/publish")
            {
                Content = JsonContent(File.ReadAllText(ServerManifestFile)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registryToken);

            using var publish = client.SendAsync(request).GetAwaiter().GetResult();
            var publishBody = publish.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!publish.IsSuccessStatusCode)
            {
                // A 422 is a validation failure; the public validate endpoint says which field.
                var detail = publish.StatusCode == HttpStatusCode.UnprocessableEntity
                    ? " Validation: " + client.PostAsync($"{McpRegistryUrl}/v0/validate",
                            JsonContent(File.ReadAllText(ServerManifestFile)))
                        .GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    : string.Empty;

                Assert.Fail($"MCP Registry publish of {serverName} {Version} failed ({(int) publish.StatusCode}): " +
                            $"{publishBody}.{detail} A version is immutable once published there, so a re-run after " +
                            "a success fails here too.");
            }

            Log.Information("Published {Server} {Version} to the MCP Registry", serverName, Version);
            ReportSummary(_ => _
                .AddPair("Registry", serverName)
                .AddPair("Version", Version));
        });

    void PushPackages(IReadOnlyCollection<AbsolutePath> packages, string apiKey) =>
        DotNetNuGetPush(_ => _
                .SetSource(NuGetSource)
                .SetApiKey(apiKey)
                .EnableSkipDuplicate()
                .CombineWith(packages, (_, v) => _.SetTargetPath(v)),
            degreeOfParallelism: 1,
            completeOnFailure: false);

    /// <summary>Unpacks a package into its own folder under artifacts/package-check.</summary>
    AbsolutePath ExtractPackage(AbsolutePath package)
    {
        var target = PackageCheckDirectory / Path.GetFileNameWithoutExtension(package);
        target.CreateOrCleanDirectory();
        ZipFile.ExtractToDirectory(package, target);
        return target;
    }

    /// <summary>The newest <c>release.yml</c> run the tag push started for this commit.</summary>
    long FindReleaseRun(HttpClient github, string repository)
    {
        var sha = Assert.NotNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_SHA"),
            "GITHUB_SHA names the commit whose release.yml run holds the RID packages; pass --release-run-id " +
            "to name a run directly.");

        // Both workflows start on the same tag push, so the run may not exist for the first moments.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);

        while (true)
        {
            var runs = GetJson(github,
                    $"repos/{repository}/actions/workflows/{ReleaseWorkflowFile}/runs?head_sha={sha}&event=push&per_page=10")
                ["workflow_runs"]!.AsArray();

            var newest = runs
                .OrderByDescending(x => x!["created_at"]!.GetValue<DateTimeOffset>())
                .FirstOrDefault();

            if (newest != null)
            {
                var id = newest["id"]!.GetValue<long>();
                Log.Information("Collecting RID packages from {Workflow} run {Run} ({Status})",
                    ReleaseWorkflowFile, id, newest["status"]!.GetValue<string>());
                return id;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline,
                $"No {ReleaseWorkflowFile} run was started by a push of commit {sha}; the RID packages come from it.");
            Thread.Sleep(TimeSpan.FromSeconds(15));
        }
    }

    /// <summary>
    /// Waits until the run has uploaded every wanted artifact, failing as soon as it finishes without
    /// one, and returns their ids by name.
    /// </summary>
    IReadOnlyDictionary<string, long> WaitForArtifacts(HttpClient github, string repository, long runId, string[] wanted)
    {
        var deadline = DateTimeOffset.UtcNow + ReleaseLegsTimeout;

        while (true)
        {
            var found = GetJson(github, $"repos/{repository}/actions/runs/{runId}/artifacts?per_page=100")
                ["artifacts"]!.AsArray()
                .Where(x => !x!["expired"]!.GetValue<bool>())
                .Select(x => (Name: x!["name"]!.GetValue<string>(), Id: x["id"]!.GetValue<long>()))
                .Where(x => wanted.Contains(x.Name, StringComparer.Ordinal))
                .GroupBy(x => x.Name)
                .ToDictionary(x => x.Key, x => x.First().Id);

            var missing = wanted.Except(found.Keys, StringComparer.Ordinal).ToArray();
            if (missing.Length == 0)
            {
                return found;
            }

            var run = GetJson(github, $"repos/{repository}/actions/runs/{runId}");
            Assert.True(run["status"]!.GetValue<string>() != "completed",
                $"{ReleaseWorkflowFile} run {runId} finished ({run["conclusion"]?.GetValue<string>()}) without uploading " +
                $"{string.Join(", ", missing)}: a leg failed, so the set is incomplete and nothing is published.");
            Assert.True(DateTimeOffset.UtcNow < deadline,
                $"Gave up after {ReleaseLegsTimeout.TotalMinutes:0} minutes waiting for {string.Join(", ", missing)} " +
                $"from {ReleaseWorkflowFile} run {runId}.");

            Log.Information("Waiting for {Missing} from {Workflow} run {Run}", string.Join(", ", missing), ReleaseWorkflowFile, runId);
            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>Polls the README nuget.org serves for this version until it carries the registry's ownership marker.</summary>
    void WaitForPackageReadme(HttpClient client, string serverName)
    {
        var index = JsonNode.Parse(client.GetStringAsync(NuGetSource).GetAwaiter().GetResult())!;
        var template = index["resources"]!.AsArray()
            .FirstOrDefault(x => x!["@type"]!.GetValue<string>() == ReadmeUriTemplateResource)
            .NotNull($"nuget.org's service index has no {ReadmeUriTemplateResource}")!["@id"]!.GetValue<string>();

        var url = template
            .Replace("{lower_id}", PackageId.ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("{lower_version}", Version.ToLowerInvariant(), StringComparison.Ordinal);
        var marker = $"mcp-name: {serverName}";
        var deadline = DateTimeOffset.UtcNow + NuGetReadmeTimeout;

        while (true)
        {
            using var response = client.GetAsync(url).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                Assert.True(response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains(marker, StringComparison.Ordinal),
                    $"nuget.org serves the README of {PackageId} {Version} without '{marker}', so the MCP Registry would " +
                    "refuse the publish. README.md must carry the marker in the version it is packed into.");
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline,
                $"nuget.org still did not serve {url} after {NuGetReadmeTimeout.TotalMinutes:0} minutes; the package is " +
                "pushed, and this target can be re-run alone once it does.");

            Log.Information("Waiting for nuget.org to serve the {Version} README ({Status})", Version, (int) response.StatusCode);
            Thread.Sleep(PollInterval);
        }
    }

    static JsonNode GetJson(HttpClient client, string url)
    {
        using var response = client.GetAsync(url).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.True(response.IsSuccessStatusCode, $"GET {url} failed ({(int) response.StatusCode}): {body}");
        return JsonNode.Parse(body)!;
    }

    static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// The tag gate. There is exactly one thing a <c>Publish</c> run can legitimately be - a v* tag
    /// build pushing to nuget.org - so anything else is skipped rather than failed, which keeps
    /// <c>Compile Test Pack Publish</c> a safe line to run anywhere. Skipping is never silent.
    /// </summary>
    bool ShouldPublish()
    {
        if (IsTaggedBuild)
        {
            return true;
        }

        Log.Warning(
            "Skipping Publish: this build is not a v* tag build, and nothing is pushed to nuget.org from " +
            "anything else (there is no preview feed). Run `Pack` to build and inspect the package locally; " +
            "push tag v{Version} - the version CHANGELOG.md declares - to publish it.", Version);

        return false;
    }

    /// <summary>
    /// Refuses to run anywhere the OIDC exchange cannot possibly succeed, with the reason spelled
    /// out. Same tag-versus-CHANGELOG rule as <c>CreateGitHubRelease</c>: CHANGELOG.md is the
    /// version authority, so the tag has to agree with it or the package version and the release
    /// name would tell different stories.
    /// </summary>
    void AssertPublishContext()
    {
        Assert.True(GitHubActions.Instance != null,
            "Publish only runs on GitHub Actions: the nuget.org API key is minted from the workflow's " +
            "OIDC token, which exists nowhere else. Run `.\\build.ps1 Pack` locally to inspect the package, " +
            "and push a v* tag to publish.");

        Assert.NotNullOrWhiteSpace(NuGetUser,
            "The nuget.org profile name of the trusted publishing policy's creator is required. Set it as " +
            "NUGET_USER (env:) on the publish job, or pass --nuget-user. It is public information, " +
            "not a secret.");

        var expected = $"v{Version}";
        var actual = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

        Assert.True(actual == expected,
            $"Refusing to publish to nuget.org: the workflow ran for ref '{actual}' but CHANGELOG.md says the " +
            $"version is {Version} (tag '{expected}'). Tag the commit that carries the matching CHANGELOG entry.");
    }

    /// <summary>The job's GitHub OIDC token for one audience. Each audience gets a token of its own.</summary>
    string GetGitHubOidcToken(string audience)
    {
        const string MissingOidc =
            "GitHub OIDC is unavailable - the publish job needs 'permissions: id-token: write'";

        var requestUrl = Assert.NotNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL"), MissingOidc);
        var requestToken = Assert.NotNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN"), MissingOidc);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(NuGetUserAgent);

        return client
            .CreateRequest(HttpMethod.Get, $"{requestUrl}&audience={Uri.EscapeDataString(audience)}")
            .WithBearerAuthentication(requestToken)
            .GetResponse()
            .AssertSuccessfulStatusCode()
            .GetBodyAsJsonObject().GetAwaiter().GetResult()["value"].GetValue<string>();
    }

    /// <summary>
    /// Exchanges the job's GitHub OIDC token for a short-lived nuget.org API key - what
    /// <c>NuGet/login@v1</c> does, without taking a dependency on a marketplace action.
    /// </summary>
    string GetTrustedPublishingApiKey()
    {
        var idToken = GetGitHubOidcToken(NuGetAudience);

        using var client = new HttpClient();

        // REQUIRED. nuget.org's token endpoint sits behind Azure Front Door, which answers
        // HTTP 400 "A User-Agent header is required." to a request that carries none - and a bare
        // HttpClient sends none. The check runs before the token or the policy is looked at, so the
        // symptom of forgetting this is a 400 rather than the 401 a misconfiguration would give.
        // The JS http client behind NuGet/login always sends one, which is why only in-build .NET
        // callers ever hit it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(NuGetUserAgent);

        var body = client
            .CreateRequest(HttpMethod.Post, NuGetTokenServiceUrl)
            .WithBearerAuthentication(idToken)
            .WithJsonContent(new { username = NuGetUser, tokenType = "ApiKey" })
            .GetResponse()
            // Surface the response body: nuget.org answers { "error": "..." } and it usually names
            // the cause outright.
            .AssertResponse(x => x.IsSuccessStatusCode
                ? null
                : $"nuget.org token exchange failed ({(int) x.StatusCode}): " +
                  $"{x.Content.ReadAsStringAsync().GetAwaiter().GetResult()}. A 400 means the request shape is " +
                  "wrong (checked before any policy lookup); a 401 means no trusted publishing policy matched " +
                  $"repository lahma/sonarqube-mcp, workflow file {PublishWorkflowFile}, environment nuget and " +
                  $"creator '{NuGetUser}'.")
            .GetBodyAsJsonObject().GetAwaiter().GetResult();

        // The action reads 'apiKey'; the original design document says 'api_key'. Accept either.
        var apiKey = (body["apiKey"] ?? body["api_key"])
            .NotNull($"nuget.org returned no API key: {body.ToJsonString()}")
            .GetValue<string>();

        GitHubActions.Instance?.WriteCommand("add-mask", apiKey);
        Log.Information("Minted a short-lived nuget.org API key, expires {Expires}",
            body["expires"]?.GetValue<string>() ?? "(not reported)");

        return apiKey;
    }
}
