using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

public sealed class DataProtectionPersistenceTests
{
    [Fact]
    public async Task DatabaseKeyRing_DecryptsAcrossIndependentServiceProviders()
    {
        var databaseName = $"dp-{Guid.NewGuid():N}";
        var connectionString = $"Data Source=file:{databaseName}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();

        var schemaOptions = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseSqlite(anchor)
            .Options;
        await using (var schema = new ZayraDbContext(schemaOptions))
            await schema.Database.EnsureCreatedAsync();

        string cipherText;
        await using (var first = BuildProvider(connectionString))
        {
            var protector = first.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("restart-safety-proof");
            cipherText = protector.Protect("mfa-and-provider-secret");
        }

        await using (var second = BuildProvider(connectionString))
        {
            var protector = second.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("restart-safety-proof");
            Assert.Equal("mfa-and-provider-secret", protector.Unprotect(cipherText));
        }

        await using var verify = new ZayraDbContext(schemaOptions);
        Assert.NotEmpty(await verify.DataProtectionKeys.AsNoTracking().ToListAsync());
    }

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ZayraDbContext>(options => options.UseSqlite(connectionString));
        services.AddDataProtection()
            .SetApplicationName("Zayra.Api")
            .PersistKeysToDbContext<ZayraDbContext>();
        return services.BuildServiceProvider(validateScopes: true);
    }
}
