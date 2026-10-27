using System.Collections.Concurrent;
using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("Zayra.Api.Tests.TestHelpers.PerTestDatabaseFramework", "Zayra.Api.Tests")]

namespace Zayra.Api.Tests.TestHelpers;

/// <summary>
/// One test = one database (WP15.1). Every test case runs inside a <see cref="TestScope"/>, opened
/// before the test class is constructed and closed after it is disposed, and
/// <see cref="PostgresFixture"/> hands each scope its own database cloned from the schema template.
/// </summary>
/// <remarks>
/// <para>Until this, the 224 classes of the Integration collection shared one database and kept
/// apart only by each seeding its own tenant, so every test relied on the tenant query filter for
/// isolation. KynexOne now holds one organisation per installation and that filter is going away;
/// a test that seeds "its" organisation must not see another test's.</para>
/// <para>xUnit v2 has no per-test hook that runs before the class constructor, so this is a test
/// framework whose method runner wraps each test case. A theory's rows are one test case and share
/// one database, as they shared one before.</para>
/// </remarks>
public sealed class PerTestDatabaseFramework(IMessageSink diagnosticMessageSink) : XunitTestFramework(diagnosticMessageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new AssemblyRunner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class AssemblyRunner(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
    {
        protected override Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource) =>
            new CollectionRunner(testCollection, testCases, DiagnosticMessageSink, messageBus, TestCaseOrderer, new ExceptionAggregator(Aggregator), cancellationTokenSource).RunAsync();
    }

    private sealed class CollectionRunner(ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink, IMessageBus messageBus, ITestCaseOrderer testCaseOrderer, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
        : XunitTestCollectionRunner(testCollection, testCases, diagnosticMessageSink, messageBus, testCaseOrderer, aggregator, cancellationTokenSource)
    {
        protected override Task<RunSummary> RunTestClassAsync(ITestClass testClass, IReflectionTypeInfo @class, IEnumerable<IXunitTestCase> testCases) =>
            new ClassRunner(testClass, @class, testCases, DiagnosticMessageSink, MessageBus, TestCaseOrderer, new ExceptionAggregator(Aggregator), CancellationTokenSource, CollectionFixtureMappings).RunAsync();
    }

    private sealed class ClassRunner(ITestClass testClass, IReflectionTypeInfo @class, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink, IMessageBus messageBus, ITestCaseOrderer testCaseOrderer, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource, IDictionary<Type, object> collectionFixtureMappings)
        : XunitTestClassRunner(testClass, @class, testCases, diagnosticMessageSink, messageBus, testCaseOrderer, aggregator, cancellationTokenSource, collectionFixtureMappings)
    {
        protected override Task<RunSummary> RunTestMethodAsync(ITestMethod testMethod, IReflectionMethodInfo method, IEnumerable<IXunitTestCase> testCases, object[] constructorArguments) =>
            new MethodRunner(testMethod, Class, method, testCases, DiagnosticMessageSink, MessageBus, new ExceptionAggregator(Aggregator), CancellationTokenSource, constructorArguments).RunAsync();
    }

    private sealed class MethodRunner(ITestMethod testMethod, IReflectionTypeInfo @class, IReflectionMethodInfo method, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink, IMessageBus messageBus, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource, object[] constructorArguments)
        : XunitTestMethodRunner(testMethod, @class, method, testCases, diagnosticMessageSink, messageBus, aggregator, cancellationTokenSource, constructorArguments)
    {
        protected override Task<RunSummary> RunTestCaseAsync(IXunitTestCase testCase) =>
            TestScope.RunAsync(() => base.RunTestCaseAsync(testCase));
    }
}

/// <summary>The test case now running; resources tied to it are released when it ends.</summary>
public sealed class TestScope
{
    private static readonly AsyncLocal<TestScope?> CurrentScope = new();
    private readonly ConcurrentStack<Func<Task>> _onEnd = new();

    /// <summary>The running test's scope; null outside a test (fixture setup and teardown).</summary>
    public static TestScope? Current => CurrentScope.Value;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Runs after the test case, its class disposal included; last registered runs first.</summary>
    public void OnEnd(Func<Task> cleanup) => _onEnd.Push(cleanup);

    internal static async Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        var scope = new TestScope();
        CurrentScope.Value = scope;
        try
        {
            return await body();
        }
        finally
        {
            CurrentScope.Value = null;
            while (scope._onEnd.TryPop(out var cleanup))
            {
                // A failed cleanup must not fail the next test or hide this test's result; the container
                // is removed with the fixture anyway.
                try { await cleanup(); } catch { /* best effort */ }
            }
        }
    }
}
