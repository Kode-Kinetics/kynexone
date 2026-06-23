namespace Zayra.Api.Tests;

/// <summary>
/// Single shared Postgres container for all integration test classes.
///
/// Root cause of the "flaky on first run" issue: when multiple test classes each
/// declare IClassFixture&lt;PostgresFixture&gt;, xUnit starts N Docker containers in
/// parallel (one per class). The host's Docker daemon, kernel TCP-stack, and port
/// allocator handle N simultaneous container starts. Under load this produces
/// transient failures: containers race on healthchecks, port 5432 bindings collide
/// on ephemeral port exhaustion, or the Reaper container times out.
///
/// Fix: [Collection("Integration")] groups all integration test classes into one
/// xUnit collection. xUnit creates exactly ONE PostgresFixture instance — one
/// container — and shares it across all classes in the collection. Tests within
/// a collection run sequentially (no parallel container startup). Tests across
/// collections still run concurrently (fast non-DB tests are unaffected).
///
/// Data isolation: each test calls SeedMinimalTenant() which inserts a Tenant row
/// with a fresh Guid.NewGuid() ID. All subsequent data is keyed to that ID, so
/// tests never interfere with each other's rows even when sharing a database.
/// </summary>
[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<PostgresFixture> { }
