using System.Text.Json;

namespace GarajeMetria.ArchitectureTests;

public sealed class DependencyTests
{
    [Theory]
    [InlineData("Domain", new string[] { })]
    [InlineData("Application", new[] { "Domain" })]
    [InlineData("Infrastructure", new[] { "Application", "Domain" })]
    [InlineData("Api", new[] { "Application", "Infrastructure" })]
    public void RestoredProjectReferencesFollowLayerBoundaries(string layer, string[] allowedLayers)
    {
        using var assets = LoadAssets(layer);
        var allowedProjects = allowedLayers.Select(name => $"GarajeMetria.{name}").ToHashSet();

        // Restore includes references inherited through shared MSBuild files.
        foreach (var framework in assets.RootElement.GetProperty("project").GetProperty("restore")
                     .GetProperty("frameworks").EnumerateObject())
        {
            foreach (var reference in framework.Value.GetProperty("projectReferences").EnumerateObject())
            {
                Assert.Contains(Path.GetFileNameWithoutExtension(reference.Name), allowedProjects);
            }
        }
    }

    [Fact]
    public void DomainHasNoRestoredExternalPackagesOrAdditionalFrameworks()
    {
        using var assets = LoadAssets("Domain");
        Assert.DoesNotContain(assets.RootElement.GetProperty("libraries").EnumerateObject(),
            library => library.Value.GetProperty("type").GetString() == "package");
        AssertFrameworkBoundaries(assets);
    }

    [Fact]
    public void ApplicationHasNoRestoredPersistenceOrHttpFrameworkDependencies()
    {
        using var assets = LoadAssets("Application");
        Assert.DoesNotContain(assets.RootElement.GetProperty("libraries").EnumerateObject(),
            library => IsDataAccessDependency(library.Name.Split('/')[0]) ||
                       library.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        AssertFrameworkBoundaries(assets);
        Assert.DoesNotContain(assets.RootElement.GetProperty("libraries").EnumerateObject(),
            library => library.Name.StartsWith("Serilog", StringComparison.Ordinal));
    }

    internal static bool IsDataAccessDependency(string name) =>
        name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.Data.Sql", StringComparison.Ordinal) ||
        name.StartsWith("System.Data", StringComparison.Ordinal) ||
        name.StartsWith("Npgsql", StringComparison.Ordinal) ||
        name.StartsWith("MySql", StringComparison.Ordinal) ||
        name.StartsWith("Pomelo.EntityFrameworkCore", StringComparison.Ordinal) ||
        name.StartsWith("Oracle", StringComparison.Ordinal) ||
        name.StartsWith("Devart.Data", StringComparison.Ordinal) ||
        name.StartsWith("Dapper", StringComparison.Ordinal);

    private static JsonDocument LoadAssets(string layer) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), "src", $"GarajeMetria.{layer}", "obj", "project.assets.json")));

    private static void AssertFrameworkBoundaries(JsonDocument assets)
    {
        foreach (var framework in assets.RootElement.GetProperty("project").GetProperty("frameworks")
                     .EnumerateObject())
        {
            foreach (var reference in framework.Value.GetProperty("frameworkReferences").EnumerateObject())
            {
                Assert.Equal("Microsoft.NETCore.App", reference.Name);
            }
        }
    }

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GarajeMetria.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate GarajeMetria.slnx.");
    }
}
