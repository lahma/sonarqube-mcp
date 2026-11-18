namespace SonarQube.Mcp.Tests;

/// <summary>
/// The checkout the tests run from, for the tests that read checked-in files (manifests, the skill,
/// the sources, the workflows) rather than anything generated.
/// </summary>
internal static class RepositoryRoot
{
    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string Marker = "sonarqube-mcp.slnx";

    /// <summary>The repository root's full path.</summary>
    internal static string FullPath { get; } = Find();

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, Marker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not find {Marker} above {AppContext.BaseDirectory}.");
    }
}
