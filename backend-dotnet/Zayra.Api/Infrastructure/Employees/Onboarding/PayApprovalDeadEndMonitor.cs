using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>Scoped implementation of <see cref="IPayApprovalDeadEndMonitor"/>; the actor is the signed-in caller, when there is one.</summary>
public sealed class PayApprovalDeadEndMonitor : IPayApprovalDeadEndMonitor
{
    private readonly ZayraDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly IConfiguration? _config;
    private readonly ILogger<PayApprovalDeadEndMonitor> _logger;

    public PayApprovalDeadEndMonitor(ZayraDbContext db, IHttpContextAccessor http, IConfiguration? config, ILogger<PayApprovalDeadEndMonitor> logger)
    {
        _db = db; _http = http; _config = config; _logger = logger;
    }

    public Task SyncCompanyAsync(Guid tenantId, Guid companyId, string trigger, CancellationToken ct)
    {
        var user = _http.HttpContext?.User;
        var raw = user?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? user?.FindFirst("sub")?.Value;
        Guid? actor = Guid.TryParse(raw, out var id) ? id : null;
        var context = new Zayra.Api.Application.Auth.RequestContext(
            _http.HttpContext?.Connection.RemoteIpAddress?.ToString(), _http.HttpContext?.Request.Headers.UserAgent.ToString(), actor, tenantId);
        return PayApprovalDeadEnds.SyncTaskAsync(_db, tenantId, companyId, actor, trigger, context, _logger, ct, _config);
    }
}
