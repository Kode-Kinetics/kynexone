using System.Text.Json;
using Zayra.Api.Application.Auth;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// The one recognisable audit row for "where cost lands changed" (AM-14 / AM-22, strict separation of duties): a department's or position's
/// cost centre, a department's parent, a cost centre's active state, company, segment or parent. Every master-data writer that moves attribution
/// stages it, with the company, in the same SaveChanges as the change; <see cref="CostAccountingSod.RelevantActions"/> counts it, so whoever made
/// the change cannot lock or export that company's period while costing is on. Employee-level moves already carry their own actions.
/// </summary>
public static class CostAttributionAudit
{
    public const string Action = "organization.cost_attribution.changed";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Stages the row; does not save. A tenant-level change (no company) is staged with a null company, which counts for every company.</summary>
    public static void Stage(IAuditService audit, RequestContext context, string entityName, Guid entityId, Guid? companyId, object? before, object? after, string? reason = null) =>
        audit.Stage(Action, entityName, entityId.ToString(), context,
            JsonSerializer.Serialize(new { before, after, reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim() }, Json), companyId);
}
