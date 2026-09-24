using Zayra.Api.Application.Auth;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Infrastructure.Auth;

internal static class AuthAuditEntry
{
    public static AuditLog Create(
        Guid id,
        DateTime createdAtUtc,
        string action,
        string entityName,
        string? entityId,
        RequestContext context,
        string? metadata = null) => new()
        {
            Id = id,
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            TenantId = context.TenantId,
            UserId = context.UserId,
            IpAddress = context.IpAddress,
            UserAgent = context.UserAgent,
            Metadata = metadata,
            CreatedAtUtc = createdAtUtc
        };
}
