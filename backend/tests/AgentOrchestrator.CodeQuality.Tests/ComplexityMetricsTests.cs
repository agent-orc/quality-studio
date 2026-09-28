namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Reference vectors from the SonarSource cognitive-complexity white paper, written once in C# and
/// once in TypeScript: both analyzers implement one rule set and must agree on the same shapes.
/// </summary>
public sealed class ComplexityMetricsTests
{
    private const string CSharpSample = """
        namespace Sample;

        public sealed class Numbers
        {
            public int SumOfPrimes(int max)
            {
                var total = 0;
                for (var i = 1; i <= max; ++i)
                {
                    for (var j = 2; j < i; ++j)
                    {
                        if (i % j == 0)
                        {
                            continue;
                        }
                    }
                    total += i;
                }
                return total;
            }

            public string GetWords(int number)
            {
                switch (number)
                {
                    case 1: return "one";
                    case 2: return "a couple";
                    case 3: return "a few";
                    default: return "lots";
                }
            }

            public bool Mixed(bool a, bool b, bool c, bool d) => a && b && c || d;

            public int Chain(int value)
            {
                if (value > 10) return 3;
                else if (value > 5) return 2;
                else return 1;
            }

            public int Callback(int[] values) => values.Count(value =>
            {
                if (value > 0) return true;
                return false;
            });

            public string Name { get => name ?? "none"; }
            private string? name;
        }
        """;

    private const string TypeScriptSample = """
        import { Injectable } from '@angular/core';

        type Predicate = (value: number) => boolean;

        export interface Options { strict?: boolean; run(): void; }

        @Injectable({ providedIn: 'root' })
        export class Numbers {
          private readonly pattern = /[{}]/g;
          handler: (value: number) => void;

          sumOfPrimes(max: number): number {
            let total = 0;
            for (let i = 1; i <= max; ++i) {
              for (let j = 2; j < i; ++j) {
                if (i % j === 0) {
                  continue;
                }
              }
              total += i;
            }
            return total;
          }

          getWords(value: number): string {
            switch (value) {
              case 1: return 'one';
              case 2: return `a ${value > 1 ? 'couple' : '}'}`;
              case 3: return 'a few';
              default: return 'lots';
            }
          }

          mixed(a: boolean, b: boolean, c: boolean, d: boolean): boolean { return a && b && c || d; }

          chain(value: number): number {
            if (value > 10) return 3;
            else if (value > 5) return 2;
            else return 1;
          }

          readonly callback = (values: number[]): number => values.filter(value => {
            if (value > 0) return true;
            return false;
          }).length;
        }

        export function standalone(value?: number): { ok: boolean } {
          try {
            return { ok: value !== undefined };
          } catch {
            return { ok: false };
          }
        }

        const enabled = globalThis.process?.env ? true : false;
        """;

    [Fact]
    public void CSharp_functions_follow_the_reference_vectors()
    {
        var file = ComplexityAnalyzer.Analyze("src/Numbers.cs", CSharpSample)!;
        var functions = file.Functions.ToDictionary(function => function.Name);

        Assert.Equal("csharp", file.Language);
        AssertMetric(functions["Numbers.SumOfPrimes"], cyclomatic: 4, cognitive: 6);
        AssertMetric(functions["Numbers.GetWords"], cyclomatic: 4, cognitive: 1);
        AssertMetric(functions["Numbers.Mixed"], cyclomatic: 4, cognitive: 2);
        AssertMetric(functions["Numbers.Chain"], cyclomatic: 3, cognitive: 3);
        // The lambda folds into its member and nests its `if` one level deeper.
        AssertMetric(functions["Numbers.Callback"], cyclomatic: 2, cognitive: 2);
        AssertMetric(functions["Numbers.Name.get"], cyclomatic: 2, cognitive: 0);
        Assert.Equal(6, file.MaxCognitive);
        Assert.Equal(functions.Values.Sum(function => function.Cyclomatic), file.Cyclomatic);
    }

    [Fact]
    public void TypeScript_functions_agree_with_the_CSharp_vectors()
    {
        var file = ComplexityAnalyzer.Analyze("src/numbers.ts", TypeScriptSample)!;
        var functions = file.Functions.ToDictionary(function => function.Name);

        Assert.Equal("typescript", file.Language);
        AssertMetric(functions["Numbers.sumOfPrimes"], cyclomatic: 4, cognitive: 6);
        // The template substitution's conditional counts, its `}` inside a string does not.
        AssertMetric(functions["Numbers.getWords"], cyclomatic: 5, cognitive: 3);
        AssertMetric(functions["Numbers.mixed"], cyclomatic: 4, cognitive: 2);
        AssertMetric(functions["Numbers.chain"], cyclomatic: 3, cognitive: 3);
        AssertMetric(functions["Numbers.callback"], cyclomatic: 2, cognitive: 2);
        AssertMetric(functions["standalone"], cyclomatic: 2, cognitive: 1);
        AssertMetric(functions["<top-level>"], cyclomatic: 2, cognitive: 1);
        // Type aliases, interfaces, field types and decorators are not functions.
        Assert.DoesNotContain(functions.Keys, name => name.Contains("Predicate") || name.Contains("handler") ||
                                                     name.Contains("Options") || name.Contains("pattern"));
        Assert.Equal(7, functions.Count);
        Assert.Equal(12, functions["Numbers.sumOfPrimes"].Line);
    }

    [Fact]
    public void CSharp_top_level_local_functions_are_units_of_their_own()
    {
        var file = ComplexityAnalyzer.Analyze("Program.cs", """
            var app = args.Length > 0 ? args[0] : "none";
            Run(app);

            static void Run(string value)
            {
                if (value == "none") return;
                foreach (var character in value) { if (character == 'x') break; }
            }
            """)!;

        var functions = file.Functions.ToDictionary(function => function.Name);
        AssertMetric(functions["<top-level>"], cyclomatic: 2, cognitive: 1);
        AssertMetric(functions["Run"], cyclomatic: 4, cognitive: 4);
    }

    [Theory]
    [InlineData("types.d.ts")]
    [InlineData("view.tsx")]
    [InlineData("README.md")]
    public void Unsupported_files_are_not_measured(string path)
    {
        Assert.False(ComplexityAnalyzer.Supports(path));
        Assert.Null(ComplexityAnalyzer.Analyze(path, "export const x = 1;"));
    }

    [Fact]
    public void Pressure_scales_the_worst_function_against_twice_the_threshold()
    {
        var file = new FileComplexity("a.ts", "typescript", 10, 20, 5, 15, []);
        Assert.Equal(50, file.Pressure);
        Assert.Equal(100, (file with { MaxCognitive = 45 }).Pressure);
        Assert.Equal(0, (file with { MaxCognitive = 0 }).Pressure);
    }

    [Fact]
    public void Cache_recomputes_only_after_the_file_changes()
    {
        var root = Directory.CreateTempSubdirectory("qs-complexity-").FullName;
        try
        {
            var path = Path.Combine(root, "a.ts");
            File.WriteAllText(path, "export function a(x: boolean) { return x ? 1 : 2; }\n");
            var cache = new ComplexityCache();
            var first = cache.Get(root, "a.ts");
            Assert.Same(first, cache.Get(root, "a.ts"));
            File.WriteAllText(path, "export function a(x: boolean, y: boolean) { return x && y ? 1 : 2; }\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            var second = cache.Get(root, "a.ts");
            Assert.Equal(2, first!.Cyclomatic);
            Assert.Equal(3, second!.Cyclomatic);
            Assert.Single(cache.GetMany(root, ["a.ts", "missing.ts", "notes.md"]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertMetric(FunctionComplexity function, int cyclomatic, int cognitive)
    {
        Assert.True(cyclomatic == function.Cyclomatic && cognitive == function.Cognitive,
            $"{function.Name}: expected cyclomatic {cyclomatic}, cognitive {cognitive}; " +
            $"was {function.Cyclomatic}, {function.Cognitive}.");
    }
}
