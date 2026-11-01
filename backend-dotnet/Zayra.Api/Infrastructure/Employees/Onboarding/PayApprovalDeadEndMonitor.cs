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

    /// <summary>Config key: seconds during which a repeat change-hook check of the same company is skipped (0 or absent = off).</summary>
    public const string DebounceSecondsKey = "PayApprovalDeadEnd:DebounceSeconds";

    /// <summary>Triggers that are never skipped: the backstop sweep and the reviewer's own decision always evaluate.</summary>
    private static bool NeverDebounced(string trigger) => trigger is "sweep" or "decision";

    public async Task SyncCompanyAsync(Guid tenantId, Guid companyId, string trigger, CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(Math.Clamp(int.TryParse(_config?[DebounceSecondsKey], out var seconds) ? seconds : 0, 0, 600));
        if (!NeverDebounced(trigger) && PayApprovalDeadEndRotation.CheckedWithin(tenantId, companyId, window)) return;

        var user = _http.HttpContext?.User;
        var raw = user?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? user?.FindFirst("sub")?.Value;
        Guid? actor = Guid.TryParse(raw, out var id) ? id : null;
        var context = new Zayra.Api.Application.Auth.RequestContext(
            _http.HttpContext?.Connection.RemoteIpAddress?.ToString(), _http.HttpContext?.Request.Headers.UserAgent.ToString(), actor, tenantId);
        await PayApprovalDeadEnds.SyncTaskAsync(_db, tenantId, companyId, actor, trigger, context, _logger, ct, _config);
        PayApprovalDeadEndRotation.Touch(tenantId, companyId);
    }
}
