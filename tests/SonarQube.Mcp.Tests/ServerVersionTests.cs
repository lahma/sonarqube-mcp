using NUnit.Framework;

namespace SonarQube.Mcp.Tests;

/// <summary>
/// The one test the Phase A skeleton can honestly make: the server project is referenced, its
/// internals are visible here, and the name the MCP handshake reports is the product name the
/// build archives and packages under.
/// </summary>
/// <remarks>
/// It exists so that <c>build.ps1 Test</c> is a real gate from the first commit rather than a
/// no-op that starts working later — a test run that discovers zero tests passes for the wrong
/// reason. The rest of the suite lands in Phases B–D.
/// </remarks>
public class ServerVersionTests
{
    [Test]
    public void NameIsTheProductName()
    {
        Assert.That(ServerVersion.Name, Is.EqualTo("sonarqube-mcp"));
    }

    [Test]
    public void VersionIsResolvedFromTheAssembly()
    {
        // The build passes -p:Version from CHANGELOG.md; a plain `dotnet test` falls back to
        // Directory.Build.props's VersionPrefix. Either way it must be a real version, never the
        // "0.0.0" that means the informational-version attribute was missing.
        Assert.That(ServerVersion.Value, Is.Not.EqualTo("0.0.0"));
        Assert.That(ServerVersion.Value, Does.Not.Contain('+'));
    }
}
