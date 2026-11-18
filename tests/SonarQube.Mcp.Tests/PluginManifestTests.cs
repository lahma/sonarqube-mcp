using System.Text.Json;

using NUnit.Framework;

using SonarQube.Mcp.Configuration;

namespace SonarQube.Mcp.Tests;

/// <summary>
/// The two Claude Code plugin manifests that make this repository installable with
/// <c>/plugin marketplace add lahma/sonarqube-mcp</c>: <c>.claude-plugin/marketplace.json</c> (the
/// catalog) and <c>.claude-plugin/plugin.json</c> (the plugin itself, whose source is the repository
/// root).
/// </summary>
/// <remarks>
/// <para>
/// One install delivers two things — the shipped skill and the MCP server wired through
/// <c>dnx</c> — and both are described by paths and version strings that nothing else in the build
/// reads back. A skill path that stops resolving ships a plugin with no skill; a <c>dnx</c> pin left
/// behind at release time ships this version's skill driving last version's server. Neither fails
/// anywhere else, which is what this file is for. It is the same argument, and the same shape, as
/// <see cref="McpServerManifestTests"/>.
/// </para>
/// <para>
/// The credential surface is checked rather than restated: the environment block the plugin hands
/// the server has to be exactly the one <c>.mcp/server.json</c> documents, each entry wired to a
/// <c>userConfig</c> option that exists, with secrecy agreeing between the two. A typo in a
/// <c>${user_config.…}</c> placeholder does not fail anywhere — it passes the placeholder itself
/// through as the credential.
/// </para>
/// <para>
/// Read with <see cref="JsonDocument"/> rather than deserialized, for the reason
/// <see cref="McpServerManifestTests"/> gives: the test project runs with
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> (D7) and these files have no business in
/// a <c>JsonSerializerContext</c>.
/// </para>
/// </remarks>
public class PluginManifestTests
{
    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string RootMarker = "sonarqube-mcp.slnx";

    private const string MarketplacePath = ".claude-plugin/marketplace.json";
    private const string PluginPath = ".claude-plugin/plugin.json";
    private const string ServerManifestPath = ".mcp/server.json";
    private const string ChangelogPath = "CHANGELOG.md";

    /// <summary>
    /// The plugin's own name, which is the repository's name — deliberately <em>not</em> the NuGet
    /// package id. The two were the same string until nuget.org rejected <c>sonarqube-mcp</c>, and
    /// then <c>sonarcloud-mcp</c>, under SonarSource's reserved <c>SonarQube*</c> and
    /// <c>SonarCloud*</c> id prefixes (2026-08-22) and the package became <c>mcp-sonarqube</c>;
    /// nothing else was renamed. Keeping one constant for both would have made this test enforce a
    /// coincidence.
    /// </summary>
    private const string PluginName = "sonarqube-mcp";

    /// <summary>
    /// The plugin's source is the repository root, which is what lets the manifest point at the one
    /// canonical <c>SKILL.md</c> under <c>.claude/skills/</c> instead of a second copy. A plugin
    /// cannot reference files outside its own root, so any other source would force a duplicate.
    /// </summary>
    [Test]
    public void TheMarketplaceListsThisRepositoryAsItsOnePlugin()
    {
        var root = FindRepositoryRoot();

        using var marketplace = Read(root, MarketplacePath);
        using var plugin = Read(root, PluginPath);

        Assert.That(marketplace.RootElement.GetProperty("name").GetString(), Is.EqualTo(PluginName));

        Assert.That(
            string.IsNullOrWhiteSpace(marketplace.RootElement.GetProperty("owner").GetProperty("name").GetString()),
            Is.False,
            "A marketplace needs an owner name; users see it before they trust the source.");

        var entries = marketplace.RootElement.GetProperty("plugins").EnumerateArray().ToList();
        Assert.That(entries, Has.Exactly(1).Items);
        var entry = entries.Single();

        Assert.That(entry.GetProperty("name").GetString(), Is.EqualTo(plugin.RootElement.GetProperty("name").GetString()));
        Assert.That(entry.GetProperty("source").GetString(), Is.EqualTo("./"));
    }

    /// <summary>
    /// <c>CHANGELOG.md</c> is the version authority the whole build reads from, and an explicit
    /// plugin <c>version</c> is what Claude Code keys updates off: leave it behind and users are
    /// told they are already up to date.
    /// </summary>
    [Test]
    public void ThePluginVersionIsTheVersionAuthority()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        Assert.That(plugin.RootElement.GetProperty("version").GetString(), Is.EqualTo(ReadChangelogVersion(root)));
    }

    /// <summary>
    /// The bundled server is pinned, not floating. A floating <c>dnx mcp-sonarqube</c> would change
    /// what the plugin runs without the plugin version changing — invisible to <c>/plugin update</c>,
    /// and able to pair this release's skill with a server that no longer matches it.
    /// </summary>
    /// <remarks>
    /// The id is read out of <c>.mcp/server.json</c> rather than written here, which closes the
    /// chain: <see cref="McpServerManifestTests"/> pins that identifier to the csproj's
    /// <c>&lt;PackageId&gt;</c>, so a rename of the package has exactly one place to be made and
    /// this assertion follows it instead of having to be remembered.
    /// </remarks>
    [Test]
    public void TheBundledServerIsPinnedToThatSameVersion()
    {
        var root = FindRepositoryRoot();
        var version = ReadChangelogVersion(root);

        using var plugin = Read(root, PluginPath);
        using var server = Read(root, ServerManifestPath);

        var packageId = NuGetPackage(server).GetProperty("identifier").GetString();

        var arguments = SingleServer(plugin)
            .GetProperty("args")
            .EnumerateArray()
            .Select(argument => argument.GetString())
            .ToList();

        Assert.That(arguments, Does.Contain($"{packageId}@{version}"));
    }

    /// <summary>
    /// The manifest names the skill directory explicitly, because the plugin's source is the
    /// repository root and the default <c>skills/</c> scan would otherwise find nothing.
    /// </summary>
    [Test]
    public void EveryDeclaredSkillPathResolvesToASkill()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        var paths = plugin.RootElement.GetProperty("skills")
            .EnumerateArray()
            .Select(path => path.GetString()!)
            .ToList();

        Assert.That(paths, Is.Not.Empty);

        foreach (var path in paths)
        {
            Assert.That(path, Does.StartWith("./").Using(StringComparison.Ordinal));

            var skill = Path.Combine(root, path[2..].Replace('/', Path.DirectorySeparatorChar), "SKILL.md");

            Assert.That(
                File.Exists(skill),
                Is.True,
                $"{PluginPath} declares the skill path '{path}', which has no SKILL.md. The plugin would "
                + "install with no skill at all.");
        }
    }

    /// <summary>
    /// The environment block and <c>.mcp/server.json</c> describe the same server's configuration
    /// surface, and each value has to reach a <c>userConfig</c> option that exists — an unresolved
    /// <c>${user_config.…}</c> is handed to the server as a literal value, which for the token means
    /// a literal credential.
    /// </summary>
    [Test]
    public void EveryDocumentedVariableIsPromptedForAndPassedThrough()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);
        using var server = Read(root, ServerManifestPath);

        var documented = NuGetPackage(server)
            .GetProperty("environmentVariables")
            .EnumerateArray()
            .ToDictionary(
                variable => variable.GetProperty("name").GetString()!,
                variable => variable.TryGetProperty("isSecret", out var secret) && secret.GetBoolean(),
                StringComparer.Ordinal);

        var options = plugin.RootElement.GetProperty("userConfig");

        var passed = SingleServer(plugin).GetProperty("env")
            .EnumerateObject()
            .ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!, StringComparer.Ordinal);

        // The manifest writes to the plugin-option names, never to the plain ones. That is the fix
        // for issue #1 and it is the whole reason this indirection exists: an option the user never
        // filled in substitutes as the empty string rather than being omitted, so mapping it onto
        // SONARQUBE_TOKEN would set that variable to "" in the child process and shadow a value the
        // user already had in their environment. The server reads the prefixed name first and falls
        // back to the plain one, so blank means "not configured" instead of "configured as empty".
        Assert.That(
            passed.Keys.OrderBy(name => name, StringComparer.Ordinal),
            Is.EqualTo(documented.Keys.Select(name => SonarQubeMcpOptions.PluginOptionPrefix + name).OrderBy(name => name, StringComparer.Ordinal)));

        foreach (var name in documented.Keys)
        {
            Assert.That(
                passed.ContainsKey(name),
                Is.False,
                $"The manifest maps {name} directly. An unset option substitutes as the empty string, "
                + $"which would shadow a {name} the user already has in their environment (issue #1). "
                + $"Map it to {SonarQubeMcpOptions.PluginOptionPrefix}{name} instead.");
        }

        foreach (var (name, isSecret) in documented)
        {
            var placeholder = passed[SonarQubeMcpOptions.PluginOptionPrefix + name];

            Assert.That(
                placeholder.StartsWith("${user_config.", StringComparison.Ordinal)
                && placeholder.EndsWith('}'),
                Is.True,
                $"{name} is passed as '{placeholder}', which is a literal, not a configured value.");

            var key = placeholder["${user_config.".Length..^1];

            Assert.That(
                options.TryGetProperty(key, out var option),
                Is.True,
                $"{name} substitutes '{key}', which no userConfig option declares. The server would receive "
                + "the placeholder text as its value.");

            var sensitive = option.TryGetProperty("sensitive", out var flag) && flag.GetBoolean();

            Assert.That(
                sensitive == isSecret,
                Is.True,
                $"{name} is {(isSecret ? "secret" : "not secret")} in {ServerManifestPath} but "
                + $"{(sensitive ? "sensitive" : "not sensitive")} in {PluginPath}. A secret that is not marked "
                + "sensitive is written to settings.json in the clear.");
        }
    }

    private static JsonDocument Read(string root, string relativePath)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relativePath)));

    /// <summary>
    /// The one <c>packages</c> entry of <c>.mcp/server.json</c> that describes the NuGet channel —
    /// the id <c>dnx</c> is given and the environment variables it documents both come from there.
    /// </summary>
    private static JsonElement NuGetPackage(JsonDocument server)
        => server.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Single(package => package.GetProperty("registryType").GetString() == "nuget");

    /// <summary>The plugin bundles exactly one MCP server; more would need naming here to be meaningful.</summary>
    private static JsonElement SingleServer(JsonDocument plugin)
    {
        var servers = plugin.RootElement.GetProperty("mcpServers").EnumerateObject().ToList();

        Assert.That(servers, Has.Exactly(1).Items);
        return servers[0].Value;
    }

    /// <summary>The version authority: <c>CHANGELOG.md</c>'s first line is a <c># version</c> header.</summary>
    private static string ReadChangelogVersion(string root)
    {
        var first = File.ReadLines(Path.Combine(root, ChangelogPath)).First().Trim();

        Assert.That(first, Does.StartWith("# ").Using(StringComparison.Ordinal));
        return first[2..].Trim();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not find {RootMarker} above {AppContext.BaseDirectory}.");
    }
}
