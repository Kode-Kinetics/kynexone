using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// Inserts an entity into a database that is migrated to an EARLIER migration than the current model, writing only
/// the columns that table has at that point. Upgrade tests seed rows before applying the migration under test; with
/// the current model, any later migration that adds a column to the seeded table makes the INSERT fail (42703).
/// </summary>
internal static class LegacyRows
{
    public static async Task InsertAsync<T>(ZayraDbContext db, string table, T entity) where T : class
    {
        var existing = await db.Database.SqlQueryRaw<string>(
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = {0}", table)
            .ToListAsync();
        var store = StoreObjectIdentifier.Table(table, null);
        var columns = db.Model.FindEntityType(typeof(T))!.GetProperties()
            .Select(p => (Property: p, Column: p.GetColumnName(store)))
            .Where(x => x.Column is not null && existing.Contains(x.Column))
            .ToList();
        var parameters = columns.Select((x, i) =>
        {
            var value = x.Property.PropertyInfo?.GetValue(entity);
            var converter = x.Property.GetTypeMapping().Converter;
            if (value is not null && converter is not null) value = converter.ConvertToProvider(value);
            return new NpgsqlParameter($"p{i}", value ?? DBNull.Value);
        }).ToArray();
        var sql = $"INSERT INTO {table} ({string.Join(",", columns.Select(x => $"\"{x.Column}\""))}) VALUES ({string.Join(",", parameters.Select(p => "@" + p.ParameterName))})";
        await db.Database.ExecuteSqlRawAsync(sql, parameters);
    }
}
