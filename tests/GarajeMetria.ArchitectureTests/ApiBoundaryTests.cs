using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GarajeMetria.ArchitectureTests;

public sealed class ApiBoundaryTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);

    [Fact]
    public void ApiUsesInfrastructureOnlyThroughCompositionEntryPoints()
    {
        var directory = Path.Combine(DependencyTests.FindRepositoryRoot(), "src", "GarajeMetria.Api");
        var sourceFiles = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .ToList();
        var configuration = typeof(ApiBoundaryTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        sourceFiles.Add(Path.Combine(directory, "obj", configuration, "net10.0",
            "GarajeMetria.Api.GlobalUsings.g.cs"));
        var trees = sourceFiles.Select(path => CSharpSyntaxTree.ParseText(
            File.ReadAllText(path), ParseOptions, path));
        var compilation = CSharpCompilation.Create("ApiBoundaryAnalysis", trees, RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        AssertCompiles(compilation);
        Assert.Empty(FindViolations(compilation));
    }

    [Theory]
    [InlineData("Program.cs", "global::GarajeMetria.Infrastructure.DependencyInjection.AddInfrastructure();", true)]
    [InlineData("Program.cs", "System.Func<object> endpoint = () => global::GarajeMetria.Infrastructure.DependencyInjection.AddInfrastructure();", false)]
    [InlineData("Endpoint.cs", "using Context = GarajeMetria.Infrastructure.WorkshopContext; class Endpoint { public object Handle() => new Context(); }", false)]
    [InlineData("Endpoint.cs", "class Endpoint { public object Handle() => new Microsoft.EntityFrameworkCore.DbContext(); }", false)]
    [InlineData("Composition/Endpoint.cs", "namespace GarajeMetria.Api.Composition; public class Endpoint { public object Handle() => global::GarajeMetria.Infrastructure.DependencyInjection.AddInfrastructure(); }", false)]
    [InlineData("Composition/Services.cs", "namespace GarajeMetria.Api.Composition; public static class Services { public static object Register() => global::GarajeMetria.Infrastructure.DependencyInjection.AddInfrastructure(); }", true)]
    public void BoundaryDistinguishesRegistrationFromEndpointAndAliasedDataAccess(
        string path, string source, bool allowed)
    {
        var compilation = CreateFixtureCompilation(path, source, RuntimeReferences());

        AssertCompiles(compilation);
        Assert.Equal(allowed, FindViolations(compilation).Count == 0);
    }

    [Fact]
    public void FixturesRemainIsolatedWhenHostReferencesAlreadyContainReplacedAssemblies()
    {
        var references = FixtureReferences(RuntimeReferences());
        var hostPersistence = CompileReference("Microsoft.EntityFrameworkCore", """
            [assembly: System.Reflection.AssemblyVersion("10.0.0.0")]
            namespace Microsoft.EntityFrameworkCore;
            public class DbContext { }
            """, references);
        var hostInfrastructure = CompileReference("GarajeMetria.Infrastructure", """
            [assembly: System.Reflection.AssemblyVersion("10.0.0.0")]
            namespace GarajeMetria.Infrastructure;
            public class WorkshopContext : Microsoft.EntityFrameworkCore.DbContext { }
            """, references.Append(hostPersistence));
        var compilation = CreateFixtureCompilation("Endpoint.cs", """
            using Context = GarajeMetria.Infrastructure.WorkshopContext;
            class Endpoint { public object Handle() => new Context(); }
            """, references.Append(hostPersistence).Append(hostInfrastructure));

        AssertCompiles(compilation);
        Assert.NotEmpty(FindViolations(compilation));
    }

    private static CSharpCompilation CreateFixtureCompilation(
        string path, string source, IEnumerable<MetadataReference> hostReferences)
    {
        var references = FixtureReferences(hostReferences);
        var persistence = CompileReference("Microsoft.EntityFrameworkCore", """
            namespace Microsoft.EntityFrameworkCore;
            public class DbContext { }
            """, references);
        var infrastructure = CompileReference("GarajeMetria.Infrastructure", """
            namespace GarajeMetria.Infrastructure;
            public class WorkshopContext : Microsoft.EntityFrameworkCore.DbContext { }
            public static class DependencyInjection
            {
                public static object AddInfrastructure() => new object();
            }
            """, references.Append(persistence));
        var kind = path == "Program.cs" ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary;
        return CSharpCompilation.Create("ApiBoundaryFixture",
            [CSharpSyntaxTree.ParseText(source, ParseOptions, path)],
            references.Append(persistence).Append(infrastructure),
            new CSharpCompilationOptions(kind));
    }

    private static List<MetadataReference> FixtureReferences(IEnumerable<MetadataReference> references) =>
        references.Where(reference => !IsReplacedFixtureAssembly(reference)).ToList();

    private static bool IsReplacedFixtureAssembly(MetadataReference reference)
    {
        if (reference is not PortableExecutableReference portable || portable.GetMetadata() is not AssemblyMetadata assembly)
        {
            return false;
        }

        // Inspect identity, not Display/FilePath: image references need not have a file name.
        var reader = assembly.GetModules()[0].GetMetadataReader();
        var name = reader.GetString(reader.GetAssemblyDefinition().Name);
        return name is "Microsoft.EntityFrameworkCore" or "GarajeMetria.Infrastructure";
    }

    private static List<string> FindViolations(CSharpCompilation compilation)
    {
        var violations = new HashSet<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
            {
                var symbol = model.GetSymbolInfo(node).Symbol;
                if (symbol is IAliasSymbol alias)
                {
                    symbol = alias.Target;
                }

                var type = model.GetTypeInfo(node).Type;
                if (IsPersistenceSymbol(symbol) || IsPersistenceSymbol(type) ||
                    (IsInfrastructureSymbol(symbol) && !IsCompositionReference(node, symbol!, model)))
                {
                    var position = node.GetLocation().GetLineSpan();
                    violations.Add($"{position.Path}:{position.StartLinePosition.Line + 1}: {node}");
                }
            }
        }

        return violations.Order(StringComparer.Ordinal).ToList();
    }

    private static bool IsCompositionReference(SyntaxNode node, ISymbol symbol, SemanticModel model)
    {
        var registrationType = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (registrationType?.ToDisplayString() != "GarajeMetria.Infrastructure.DependencyInjection")
        {
            return false;
        }

        // Imports may name the facade; aliases cannot make data-access types legal.
        if (node.Ancestors().OfType<UsingDirectiveSyntax>().Any())
        {
            return true;
        }

        var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation is null || model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
            method.Name is not ("AddInfrastructure" or "UseInfrastructure") ||
            method.ContainingType.ToDisplayString() != "GarajeMetria.Infrastructure.DependencyInjection" ||
            node.Ancestors().Any(parent => parent is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
        {
            return false;
        }

        var enclosingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (enclosingType is null)
        {
            return Path.GetFileName(node.SyntaxTree.FilePath) == "Program.cs";
        }

        var type = model.GetDeclaredSymbol(enclosingType);
        var enclosingMethod = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return type is { IsStatic: true } &&
            type.ContainingNamespace.ToDisplayString() == "GarajeMetria.Api.Composition" &&
            enclosingMethod is not null && model.GetDeclaredSymbol(enclosingMethod) is { IsStatic: true };
    }

    private static bool IsInfrastructureSymbol(ISymbol? symbol) =>
        symbol is not INamespaceSymbol && symbol?.ContainingAssembly?.Name == "GarajeMetria.Infrastructure";

    private static bool IsPersistenceSymbol(ISymbol? symbol) =>
        symbol is not INamespaceSymbol && symbol?.ContainingAssembly is { } assembly &&
        DependencyTests.IsDataAccessDependency(assembly.Name);

    private static List<MetadataReference> RuntimeReferences()
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return platform.Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            .Where(path => Path.GetFileName(path) != "GarajeMetria.Api.dll")
            .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>().ToList();
    }

    private static PortableExecutableReference CompileReference(
        string name, string source, IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(source, ParseOptions)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static void AssertCompiles(CSharpCompilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(errors);
    }
}
