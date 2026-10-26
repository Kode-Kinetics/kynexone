using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// The creator may throw away their own unfinished draft. This is deliberately narrow: the hire must still be a Draft,
/// made by the caller, no more than seven days old, with no payroll, approval or history beyond its own creation.
/// Anything else keeps the existing soft-delete path (<c>employees.delete</c>), which retains the record.
/// </summary>
public static class DraftDiscardRules
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public enum Refusal { None, NotDraft, NotCreator, TooOld, HasActivity }

    public static Refusal Evaluate(string? status, Guid? createdBy, Guid caller, DateTime createdAtUtc, DateTime nowUtc, bool hasActivity)
    {
        if (!string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase)) return Refusal.NotDraft;
        if (createdBy is null || createdBy != caller) return Refusal.NotCreator;
        if (nowUtc - createdAtUtc > MaxAge) return Refusal.TooOld;
        return hasActivity ? Refusal.HasActivity : Refusal.None;
    }
}

public sealed class DraftDiscardService
{
    public const string AuditAction = "employee.draft_discarded";
    private readonly ZayraDbContext _db;
    private readonly IDocumentStorage _documents;

    public DraftDiscardService(ZayraDbContext db, IDocumentStorage documents)
    {
        _db = db;
        _documents = documents;
    }

    public async Task<bool> HasActivityAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var id = employeeId;
        if (await _db.EmployeeHistories.AnyAsync(h => h.TenantId == tenantId && h.EmployeeId == id && h.EventType != "Created", ct)) return true;
        if (await _db.EmployeeStatusHistories.AnyAsync(h => h.TenantId == tenantId && h.EmployeeId == id, ct)) return true;
        if (await _db.PayrollRunEmployees.AnyAsync(p => p.EmployeeId == id, ct)) return true;
        if (await _db.PayrollSlips.AnyAsync(p => p.EmployeeId == id, ct)) return true;
        if (await _db.ApprovalRequests.AnyAsync(a => a.TenantId == tenantId && a.RequestedForEmployeeId == id, ct)) return true;
        return await _db.EmployeeChangeRequests.AnyAsync(c => c.TenantId == tenantId && c.EmployeeId == id, ct);
    }

    /// <summary>Hard-removes the draft and everything that hangs off it, in one transaction. Eligibility is the
    /// caller's job (<see cref="DraftDiscardRules"/>). Blobs are removed after the commit, best effort.</summary>
    public async Task DiscardAsync(Guid tenantId, Employee employee, RequestContext context, CancellationToken ct)
    {
        var id = employee.Id;
        var code = employee.EmployeeCode;
        var blobs = await _db.EmployeeDocuments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.EmployeeId == id).Select(d => d.StorageUrl).ToListAsync(ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // Staged logins that never signed in belong to the draft; they go with it.
            var userIds = await _db.EmployeeUserAccounts.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.EmployeeId == id && a.UserId != null)
                .Select(a => a.UserId!.Value).ToListAsync(ct);

            foreach (var (table, column, tenantColumn) in DependentTables(_db.Model))
            {
                var sql = tenantColumn is null
                    ? $"DELETE FROM \"{table}\" WHERE \"{column}\" = {{0}}"
                    : $"DELETE FROM \"{table}\" WHERE \"{column}\" = {{0}} AND \"{tenantColumn}\" = {{1}}";
                if (tenantColumn is null) await _db.Database.ExecuteSqlRawAsync(sql, [id], ct);
                else await _db.Database.ExecuteSqlRawAsync(sql, [id, tenantId], ct);
            }
            _db.ChangeTracker.Clear();
            if (userIds.Count > 0)
            {
                var strays = await _db.Users.Where(u => u.TenantId == tenantId && userIds.Contains(u.Id) && u.LastLoginAtUtc == null).ToListAsync(ct);
                _db.Users.RemoveRange(strays);
            }
            var row = await _db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == id, ct);
            if (row is not null) _db.Employees.Remove(row);
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, AuditAction, "Employee",
                id.ToString(System.Globalization.CultureInfo.InvariantCulture), context with { TenantId = tenantId },
                JsonSerializer.Serialize(new { employeeId = id, employeeCode = code })));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        foreach (var url in blobs.Where(b => !string.IsNullOrWhiteSpace(b)))
            await _documents.TryDeleteAsync(tenantId, url, ct);
    }

    /// <summary>Every mapped table with an <c>EmployeeId</c> column (and its tenant column when it has one), found from
    /// the model so a table added later is covered without editing this list. Audit rows have no such column and stay.</summary>
    internal static IEnumerable<(string Table, string Column, string? TenantColumn)> DependentTables(IModel model)
    {
        foreach (var type in model.GetEntityTypes())
        {
            if (type.ClrType == typeof(Employee) || type.IsOwned() || type.GetTableName() is not { } table) continue;
            var store = StoreObjectIdentifier.Table(table, type.GetSchema());
            var property = type.FindProperty("EmployeeId");
            if (property is null || (property.ClrType != typeof(int) && property.ClrType != typeof(int?))) continue;
            if (property.GetColumnName(store) is not { } column) continue;
            var tenant = type.FindProperty("TenantId")?.GetColumnName(store);
            yield return (table, column, tenant);
        }
    }
}
