using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Data.Esign;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Retention;

/// <summary>
/// The one way code is allowed to delete append-only evidence (<c>employee_policy_acknowledgements</c>, guarded by trigger <c>trg_ack_append_only</c>):
/// inside a transaction it sets <c>app.retention_purge = 'on'</c> LOCALLY, so the permission lasts exactly as long as that transaction and never leaks to
/// a pooled connection's next user. Only the retention engine, the tenant erasure and the platform's manual purge call it; an ordinary delete does not.
/// </summary>
public static class RetentionPurgeScope
{
    /// <summary>Call after the caller's transaction is open. Throws without one: outside a transaction the setting would apply to a single statement and mean nothing.</summary>
    public static async Task EnableInCurrentTransactionAsync(ZayraDbContext db, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The retention purge flag is transaction-local; open the transaction first.");
        if (!(db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return;
        var setting = AckAppendOnlySql.RetentionSetting;
        await db.Database.SqlQuery<string>($"SELECT set_config({setting}, 'on', true) AS \"Value\"").ToListAsync(ct);
    }

    private const string TenantErasureScope =
        "Owner-confirmed tenant purge removes the tenant's acknowledgement evidence before the general sweep, pinned to that tenant id.";

    /// <summary>The platform's manual tenant purge (it runs no transaction of its own): the tenant's acknowledgements go in one short purge transaction.</summary>
    public static Task PurgeTenantAcknowledgementsAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await EnableInCurrentTransactionAsync(db, ct);
            await ScopedBypass.TenantWide(db.EmployeePolicyAcknowledgements, tenantId, TenantErasureScope).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        });
}
