using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Arch.System.SourceGenerator.Tests;

/// <summary>
///     Tests that <see cref="QueryGenerator"/> is thread-safe when invoked concurrently,
///     as happens during parallel builds with multiple projects referencing the same generator.
/// </summary>
[TestFixture]
internal class ConcurrencyTest
{
    private const string SystemSourceA = """
        using Arch.Core;
        using Arch.System;

        namespace TestA;

        public partial class SystemA : BaseSystem<World, int>
        {
            public SystemA(World world) : base(world) { }

            [Query]
            public void MethodA(ref int component) { component++; }

            [Query]
            public void MethodB(ref int component) { component += 2; }
        }
        """;

    private const string SystemSourceB = """
        using Arch.Core;
        using Arch.System;

        namespace TestB;

        public partial class SystemB : BaseSystem<World, int>
        {
            public SystemB(World world) : base(world) { }

            [Query]
            public void MethodC(ref int component) { component += 10; }

            [Query]
            public void MethodD(ref int component) { component += 20; }
        }
        """;

    private static CSharpCompilation CreateCompilation(string source, string assemblyName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

        var baseAssemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var systemAssemblies = Directory.GetFiles(baseAssemblyPath)
            .Where(x => !x.EndsWith("Native.dll"))
            .Where(x =>
            {
                var filename = Path.GetFileName(x);
                return filename.StartsWith("System") || filename is "mscorlib.dll" or "netstandard.dll";
            });

        var references = new List<string>
        {
            typeof(Arch.Core.World).Assembly.Location,
            typeof(QueryAttribute).Assembly.Location,
            typeof(CommunityToolkit.HighPerformance.ArrayExtensions).Assembly.Location
        };
        references.AddRange(systemAssemblies);

        return CSharpCompilation.Create(assemblyName, new[] { syntaxTree },
            references.Select(r => MetadataReference.CreateFromFile(r)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> errors) RunGenerator(CSharpCompilation compilation)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new QueryGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        return (diagnostics, errors);
    }

    /// <summary>
    ///     Runs multiple QueryGenerator instances concurrently to reproduce the race condition
    ///     caused by the static _classToMethods field.
    /// </summary>
    [Test]
    [Repeat(10)]
    public void ConcurrentGeneratorRuns_ShouldNotThrow()
    {
        var compilationA = CreateCompilation(SystemSourceA, "TestAssemblyA");
        var compilationB = CreateCompilation(SystemSourceB, "TestAssemblyB");

        var exceptions = new global::System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, 8, i =>
        {
            try
            {
                var compilation = i % 2 == 0 ? compilationA : compilationB;
                var (diagnostics, errors) = RunGenerator(compilation);

                // Each run should produce zero generator diagnostics and zero compilation errors
                Assert.That(diagnostics, Is.Empty,
                    $"Iteration {i}: Generator diagnostics should be empty.\n{string.Join("\n", diagnostics)}");
                Assert.That(errors, Is.Empty,
                    $"Iteration {i}: Output compilation should have no errors.\n{string.Join("\n", errors)}");
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        if (!exceptions.IsEmpty)
        {
            Assert.Fail(
                $"Concurrent generator runs failed with {exceptions.Count} exception(s):\n" +
                string.Join("\n---\n", exceptions.Select(e => e.ToString())));
        }
    }
}
