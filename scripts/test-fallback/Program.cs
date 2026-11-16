using Xunit;
using Xunit.Abstractions;
using Xunit.Runners;

// Fallback xUnit v2 runner used when `dotnet test` cannot start in the current environment. It is copied into
// the test project's output directory by scripts/run-tests.ps1 so the test assembly's dependencies resolve
// exactly like they do for the normal testhost.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: DshTestFallback <test-assembly.dll> [name-filter]");
            return 2;
        }

        var assemblyPath = Path.GetFullPath(args[0]);
        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine("test assembly not found: " + assemblyPath);
            return 2;
        }

        var filter = args.Length > 1 ? args[1] : null;
        var sink = new CollectingSink(filter);
        var controller = new XunitFrontController(
            AppDomainSupport.Denied,
            assemblyPath,
            configFileName: null,
            shadowCopy: false);

        var configuration = new TestAssemblyConfiguration();
        controller.RunAll(sink, TestFrameworkOptions.ForDiscovery(configuration), TestFrameworkOptions.ForExecution(configuration));
        sink.Done.Wait(TimeSpan.FromMinutes(30));

        Console.WriteLine();
        Console.WriteLine($"passed={sink.Passed} failed={sink.Failed} skipped={sink.Skipped}");
        foreach (var failure in sink.Failures)
        {
            Console.WriteLine();
            Console.WriteLine(failure);
        }

        return sink.Failed > 0 || !sink.AssemblyFinished ? 1 : 0;
    }

    private sealed class CollectingSink : IMessageSink
    {
        private readonly string? _filter;
        private readonly List<string> _failures = new();

        public CollectingSink(string? filter) => _filter = filter;

        public int Passed { get; private set; }

        public int Failed { get; private set; }

        public int Skipped { get; private set; }

        public bool AssemblyFinished { get; private set; }

        public ManualResetEventSlim Done { get; } = new(false);

        public IReadOnlyList<string> Failures => _failures;

        public bool OnMessage(IMessageSinkMessage message)
        {
            switch (message)
            {
                case ITestPassed passed:
                    if (Matches(passed.Test))
                    {
                        Passed++;
                    }

                    break;
                case ITestFailed failed:
                    if (Matches(failed.Test))
                    {
                        Failed++;
                        _failures.Add(Format(failed));
                    }

                    break;
                case ITestSkipped skipped:
                    if (Matches(skipped.Test))
                    {
                        Skipped++;
                    }

                    break;
                case ITestAssemblyFinished:
                    AssemblyFinished = true;
                    Done.Set();
                    break;
                case IErrorMessage error:
                    Console.Error.WriteLine("[error] " + error.Messages);
                    break;
            }

            return true;
        }

        private bool Matches(ITest testCase) =>
            string.IsNullOrEmpty(_filter) ||
            testCase.DisplayName.Contains(_filter!, StringComparison.OrdinalIgnoreCase);

        private static string Format(ITestFailed failed)
        {
            var details = string.Join("\n", failed.Messages ?? Array.Empty<string>());
            var stack = string.Join("\n", failed.StackTraces ?? Array.Empty<string>());
            return $"[FAIL] {failed.Test.DisplayName}\n{details}\n{stack}";
        }
    }
}
