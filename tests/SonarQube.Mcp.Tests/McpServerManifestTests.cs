using System.Text.Json;
using System.Xml.Linq;

using NUnit.Framework;

namespace SonarQube.Mcp.Tests;

/// <summary>
/// <c>.mcp/server.json</c> is the MCP server manifest packed into the NuGet package at
/// <c>/.mcp/server.json</c>, where nuget.org reads it to render the package's MCP tab and to
/// generate client configuration.
/// </summary>
/// <remarks>
/// <para>
/// It restates two things the build already knows — the version and the package id — and nothing in
/// the build reads it back, so a released package could advertise last release's version
/// indefinitely without anything failing. That is what this test is for. It follows the same
/// source-scanning shape as <c>NoStdoutWritesTest</c>: walk up from the test assembly to the
/// repository root, then read the checked-in files rather than anything generated.
/// </para>
/// <para>
/// The manifest is read with <see cref="JsonDocument"/> rather than deserialized into a record,
/// because the test project runs with <c>JsonSerializerIsReflectionEnabledByDefault=false</c> (D7)
/// and this file has no business in a <c>JsonSerializerContext</c>.
/// </para>
/// </remarks>
public class McpServerManifestTests
{
    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string RootMarker = "sonarqube-mcp.slnx";

    private const string ManifestPath = ".mcp/server.json";
    private const string ChangelogPath = "CHANGELOG.md";
    private const string ServerProjectPath = "src/SonarQube.Mcp/SonarQube.Mcp.csproj";

    [Test]
    public void ManifestVersionMatchesTheChangelog()
    {
        var root = FindRepositoryRoot();
        var changelogVersion = ReadChangelogVersion(root);

        using var manifest = ReadManifest(root);
        var package = SingleNuGetPackage(manifest);

        Assert.That(manifest.RootElement.GetProperty("version").GetString(), Is.EqualTo(changelogVersion));
        Assert.That(package.GetProperty("version").GetString(), Is.EqualTo(changelogVersion));
    }

    [Test]
    public void ManifestIdentifierMatchesThePackagedId()
    {
        var root = FindRepositoryRoot();

        var packageId = XDocument
            .Load(Path.Combine(root, ServerProjectPath))
            .Descendants("PackageId")
            .Select(x => x.Value)
            .SingleOrDefault();

        Assert.That(string.IsNullOrWhiteSpace(packageId), Is.False, $"{ServerProjectPath} declares no <PackageId>.");

        using var manifest = ReadManifest(root);
        Assert.That(SingleNuGetPackage(manifest).GetProperty("identifier").GetString(), Is.EqualTo(packageId));
    }

    /// <summary>
    /// nuget.org only looks at the first <c>packages</c> entry whose <c>registryType</c> is
    /// <c>nuget</c>, and the server speaks stdio only — a second entry or a different transport
    /// would silently change what clients are told to run.
    /// </summary>
    [Test]
    public void ManifestDeclaresExactlyOneStdioNuGetPackage()
    {
        var root = FindRepositoryRoot();
        using var manifest = ReadManifest(root);

        var package = SingleNuGetPackage(manifest);

        Assert.That(package.GetProperty("transport").GetProperty("type").GetString(), Is.EqualTo("stdio"));
        Assert.That(manifest.RootElement.GetProperty("name").GetString(), Is.EqualTo("io.github.lahma/sonarqube-mcp"));
    }

    /// <summary>
    /// The manifest is the credential contract a client reads before it ever runs the server, so the
    /// token has to be there, has to be marked secret, and has to be the only thing marked secret —
    /// a variable that is not a credential but claims to be one gets prompted for as though it were.
    /// The tuning knobs (<c>SONARQUBE_MCP_LOG_LEVEL</c> and the page-size, source-line and timeout
    /// limits) are deliberately absent: they are documented in the README for whoever runs the
    /// binary, not offered to whoever configures a client.
    /// </summary>
    [Test]
    public void ManifestDeclaresTheClientFacingEnvironmentVariables()
    {
        var root = FindRepositoryRoot();
        using var manifest = ReadManifest(root);

        var variables = SingleNuGetPackage(manifest)
            .GetProperty("environmentVariables")
            .EnumerateArray()
            .ToList();

        var names = variables
            .Select(variable => variable.GetProperty("name").GetString())
            .ToList();

        Assert.That(
            names,
            Is.EqualTo<string?[]>([
                "SONARQUBE_TOKEN",
                "SONARQUBE_ORG",
                "SONARQUBE_URL",
                "SONARQUBE_MCP_DEFAULT_PROJECT",
                "SONARQUBE_MCP_READ_ONLY",
            ]));

        foreach (var variable in variables)
        {
            var name = variable.GetProperty("name").GetString();

            Assert.That(
                string.IsNullOrWhiteSpace(variable.GetProperty("description").GetString()),
                Is.False,
                $"{name} has no description; the description is what a client shows next to the prompt.");

            var isSecret = variable.TryGetProperty("isSecret", out var secret) && secret.GetBoolean();
            var isRequired = variable.TryGetProperty("isRequired", out var required) && required.GetBoolean();

            Assert.That(isSecret, Is.EqualTo(name == "SONARQUBE_TOKEN"));
            Assert.That(isRequired, Is.EqualTo(name == "SONARQUBE_TOKEN"));
        }
    }

    /// <summary>
    /// What the MCP Registry refuses or warns about when a NuGet-backed server is published to it
    /// (D17). Its NuGet validator accepts exactly one base URL, the schema caps <c>description</c> at
    /// 100 characters, and ownership is proven by an <c>mcp-name:</c> line in the README packed
    /// <em>into the published version</em> - which is why the marker has to be checked here, before
    /// a version ships without it and can never be listed.
    /// </summary>
    [Test]
    public void ManifestIsListableInTheMcpRegistry()
    {
        var root = FindRepositoryRoot();
        using var manifest = ReadManifest(root);

        Assert.That(
            manifest.RootElement.GetProperty("$schema").GetString(),
            Is.EqualTo("https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json"));
        Assert.That(
            SingleNuGetPackage(manifest).GetProperty("registryBaseUrl").GetString(),
            Is.EqualTo("https://api.nuget.org/v3/index.json"));
        Assert.That(manifest.RootElement.GetProperty("description").GetString()!.Length, Is.LessThanOrEqualTo(100));

        var name = manifest.RootElement.GetProperty("name").GetString();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.That(readme, Contains.Substring($"mcp-name: {name}").Using(StringComparison.Ordinal));
    }

    private static JsonElement SingleNuGetPackage(JsonDocument manifest)
    {
        var packages = manifest.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Where(x => x.GetProperty("registryType").GetString() == "nuget")
            .ToList();

        Assert.That(packages, Has.Exactly(1).Items);
        return packages[0];
    }

    private static JsonDocument ReadManifest(string root)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ManifestPath)));

    /// <summary>
    /// The version authority: CHANGELOG.md's first line is a <c># version</c> header, which is what
    /// the Fallout build parses in <c>OnBuildInitialized</c>.
    /// </summary>
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
