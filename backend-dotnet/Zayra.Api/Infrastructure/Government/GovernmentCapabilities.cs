using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Government;

/// <summary>How a tenant can exchange data with a body right now. <c>live</c> only when the gate is open AND a live adapter runs.</summary>
public static class GovernmentCapabilityModes
{
    public const string Disabled = "disabled";
    public const string FileOnly = "file_only";
    public const string Simulated = "simulated";
    public const string Live = "live";
}

/// <summary>GET /api/government/capabilities, one entry per body. The UI hides every control whose mode does not allow it.</summary>
public sealed record GovernmentCapabilityDto(
    string Body, string Mode, string ReasonCode, string ReasonText, string ReasonTextAr, IReadOnlyList<string> ChannelsAvailable,
    bool WritesAllowed = false, string? WritesReasonCode = null);

/// <summary>
/// Works out <see cref="GovernmentCapabilityDto"/> per body for one tenant. Honest by construction: <c>live</c> needs all four
/// gates of <see cref="GovernmentLivePolicy"/> AND an adapter that declared itself live; the Qiwa simulator is <c>simulated</c>;
/// the ANB Connect bank file is <c>file_only</c> where that export is open; everything else is <c>disabled</c> with the first
/// reason that keeps it so.
/// </summary>
public sealed class GovernmentCapabilitiesService(
    ZayraDbContext db,
    IConfiguration configuration,
    IQiwaApiAdapter qiwa,
    IEnumerable<IGovernmentProvider> providers,
    GovernmentCredentialStore credentials)
{
    public const string ReasonSimulator = "simulator";
    public const string ReasonFileOnly = "file_only";
    public const string ReasonNoLiveAdapter = "no_live_adapter";
    public const string ReasonConnected = "connected";

    /// <summary>The user-facing sentence for every reason code, in English and Arabic.</summary>
    public static readonly IReadOnlyDictionary<string, (string En, string Ar)> ReasonTexts = new Dictionary<string, (string, string)>
    {
        [ReasonConnected] = ("Connected. Exchanges with this service are sent from KynexOne.", "متصل. تُرسل التبادلات مع هذه الخدمة من KynexOne."),
        [ReasonSimulator] = ("Check only. A simulator runs on this server; nothing is sent to this service.", "للتحقق فقط. يعمل محاكٍ على هذا الخادم، ولا يُرسل شيء إلى هذه الخدمة."),
        [ReasonFileOnly] = ("File only. KynexOne prepares the file; you upload it yourself.", "ملف فقط. يجهّز KynexOne الملف، وتتولى رفعه بنفسك."),
        [ReasonNoLiveAdapter] = ("Not connected. KynexOne has no connection to this service yet.", "غير متصل. لا يوجد لدى KynexOne اتصال بهذه الخدمة بعد."),
        [GovernmentLivePolicy.OperatorDisabled] = ("Not connected. The connection is switched off on this server.", "غير متصل. الاتصال متوقف على هذا الخادم."),
        [GovernmentLivePolicy.NoPartnerAgreement] = ("Not connected. No agreement with this service is recorded for this server.", "غير متصل. لا توجد اتفاقية مسجلة مع هذه الخدمة لهذا الخادم."),
        [GovernmentLivePolicy.TenantNotOptedIn] = ("Not connected. Your organisation has not authorised sending data to this service.", "غير متصل. لم تأذن منشأتك بإرسال البيانات إلى هذه الخدمة."),
        [GovernmentLivePolicy.NoCredential] = ("Not connected. No valid credential for this service is on file.", "غير متصل. لا توجد بيانات اعتماد صالحة لهذه الخدمة."),
        [GovernmentLivePolicy.LiveWritesNeedLegalClearance] = ("Production submissions through this service are switched off until a legal review is recorded for this server. The sandbox is not affected.", "الإرسال الفعلي عبر هذه الخدمة متوقف إلى أن تُسجَّل مراجعة قانونية لهذا الخادم. بيئة الاختبار غير متأثرة."),
    };

    public async Task<IReadOnlyList<GovernmentCapabilityDto>> GetAsync(Guid tenantId, CancellationToken ct)
    {
        var result = new List<GovernmentCapabilityDto>(GovernmentBodies.All.Count);
        foreach (var body in GovernmentBodies.All)
            result.Add(await ForBodyAsync(tenantId, body, ct));
        return result;
    }

    private async Task<GovernmentCapabilityDto> ForBodyAsync(Guid tenantId, string body, CancellationToken ct)
    {
        var adapter = AdapterFor(body);
        // The screen's answer: open in ANY environment (credentials are checked per environment, never across them).
        GovernmentLiveDecision? read = null, write = null;
        foreach (var environment in GovernmentEnvironments.All)
        {
            var (r, w) = body == GovernmentBodies.Qiwa
                ? await QiwaDecisionsAsync(tenantId, environment, ct)
                : (await DecideAsync(tenantId, null, body, environment, ct), await DecideLiveWriteAsync(tenantId, null, body, environment, ct));
            if (read is null || (!read.IsOpen && r.IsOpen)) read = r;
            if (write is null || (!write.IsOpen && w.IsOpen)) write = w;
        }

        var writes = (Allowed: write!.AllowsWrites && adapter?.IsLiveIntegration == true, Reason: write.ReasonCode);
        if (read!.IsOpen && adapter?.IsLiveIntegration == true)
            return Dto(body, GovernmentCapabilityModes.Live, ReasonConnected, [Models.GovernmentExchangeChannels.Api], writes);
        if (body == GovernmentBodies.Qiwa && adapter is SandboxQiwaApiAdapter)
            return Dto(body, GovernmentCapabilityModes.Simulated, ReasonSimulator, [], writes);
        if (body == GovernmentBodies.BankAnb
            && (await SaudiBankExportActivation.EnabledCompanyIdsAsync(db, configuration, tenantId, ct)).Count > 0)
            return Dto(body, GovernmentCapabilityModes.FileOnly, ReasonFileOnly,
                [Models.GovernmentExchangeChannels.FileOut, Models.GovernmentExchangeChannels.FileIn], writes);
        return Dto(body, GovernmentCapabilityModes.Disabled, read.ReasonCode ?? ReasonNoLiveAdapter, [], writes);
    }

    /// <summary>
    /// The READ decision for one body (not Qiwa), one company (null = tenant-wide) and one environment. G3 is checked for exactly
    /// that environment: a sandbox credential never opens a production call.
    /// </summary>
    public async Task<GovernmentLiveDecision> DecideAsync(Guid tenantId, Guid? companyId, string body, string environment, CancellationToken ct) =>
        GovernmentLivePolicy.Decide(configuration, body, await GovernmentOptIn.HasConsentAsync(db, tenantId, body, ct),
            await credentials.IsUsableAsync(tenantId, companyId, body, environment, ct), environment);

    /// <summary>The WRITE decision for one body, company and environment: the four gates and the body's legal clearance.</summary>
    public async Task<GovernmentLiveDecision> DecideLiveWriteAsync(Guid tenantId, Guid? companyId, string body, string environment, CancellationToken ct) =>
        GovernmentLivePolicy.DecideLiveWrite(configuration, body, await GovernmentOptIn.HasConsentAsync(db, tenantId, body, ct),
            await credentials.IsUsableAsync(tenantId, companyId, body, environment, ct), environment);

    /// <summary>
    /// Qiwa keeps its own switches (QIWA_USE_LIVE_ADAPTER, Qiwa:PartnerAgreementReference, its own credential table) and now needs
    /// the same consent record as every other body for its LIVE path (QiwaLiveAdapterPolicy). The data check is unaffected.
    /// </summary>
    private async Task<(GovernmentLiveDecision Read, GovernmentLiveDecision Write)> QiwaDecisionsAsync(Guid tenantId, string environment, CancellationToken ct)
    {
        var consent = await QiwaLiveAdapterPolicy.HasTenantConsentAsync(db, tenantId, ct);
        var credential = await db.QiwaApiCredentials.AsNoTracking()
            .AnyAsync(c => c.TenantId == tenantId && c.Environment == environment && c.ClientId != "" && c.EncryptedClientSecret != "", ct);
        var write = QiwaLiveAdapterPolicy.DecideLiveWrite(configuration, consent, credential, environment);
        var read = GovernmentLivePolicy.Decide(GovernmentBodies.Qiwa, new GovernmentLiveGates(
            QiwaLiveAdapterPolicy.HasPartnerAgreement(configuration), consent, credential,
            QiwaLiveAdapterPolicy.LiveRequested(configuration) && GovernmentLivePolicy.OperatorEnabled(configuration, GovernmentBodies.Qiwa)), environment);
        return (read, write);
    }

    private IGovernmentAdapter? AdapterFor(string body) =>
        body == GovernmentBodies.Qiwa ? qiwa : providers.FirstOrDefault(p => p.Body == body);

    private static GovernmentCapabilityDto Dto(string body, string mode, string reason, IReadOnlyList<string> channels, (bool Allowed, string? Reason) writes)
    {
        var text = ReasonTexts[reason];
        return new GovernmentCapabilityDto(body, mode, reason, text.En, text.Ar, channels, writes.Allowed, writes.Allowed ? null : writes.Reason ?? ReasonNoLiveAdapter);
    }
}
