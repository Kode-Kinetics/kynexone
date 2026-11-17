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
    string Body, string Mode, string ReasonCode, string ReasonText, string ReasonTextAr, IReadOnlyList<string> ChannelsAvailable);

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
        [GovernmentLivePolicy.LiveWritesNeedLegalClearance] = ("Submitting through this service is switched off until a legal review is recorded for this server.", "الإرسال عبر هذه الخدمة متوقف إلى أن تُسجَّل مراجعة قانونية لهذا الخادم."),
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
        var decision = body == GovernmentBodies.Qiwa ? await QiwaDecisionAsync(tenantId, ct) : await DecideAsync(tenantId, body, ct);

        if (decision.IsOpen && adapter?.IsLiveIntegration == true)
            return Dto(body, GovernmentCapabilityModes.Live, ReasonConnected, [Models.GovernmentExchangeChannels.Api]);
        if (body == GovernmentBodies.Qiwa && adapter is SandboxQiwaApiAdapter)
            return Dto(body, GovernmentCapabilityModes.Simulated, ReasonSimulator, []);
        if (body == GovernmentBodies.BankAnb
            && (await SaudiBankExportActivation.EnabledCompanyIdsAsync(db, configuration, tenantId, ct)).Count > 0)
            return Dto(body, GovernmentCapabilityModes.FileOnly, ReasonFileOnly,
                [Models.GovernmentExchangeChannels.FileOut, Models.GovernmentExchangeChannels.FileIn]);
        return Dto(body, GovernmentCapabilityModes.Disabled, decision.ReasonCode ?? ReasonNoLiveAdapter, []);
    }

    /// <summary>The full four-gate decision for a body other than Qiwa (credential checked for the tenant-wide sandbox or production one).</summary>
    public async Task<GovernmentLiveDecision> DecideAsync(Guid tenantId, string body, CancellationToken ct)
    {
        var consent = await GovernmentOptIn.HasConsentAsync(db, tenantId, body, ct);
        var credential = false;
        foreach (var environment in GovernmentEnvironments.All)
            credential |= await credentials.IsUsableAsync(tenantId, null, body, environment, ct);
        return GovernmentLivePolicy.Decide(configuration, body, consent, credential);
    }

    /// <summary>
    /// Qiwa keeps its own switches (QIWA_USE_LIVE_ADAPTER, Qiwa:PartnerAgreementReference, the qiwa_integration module and its
    /// own credential table), mapped onto the four gates so the answer reads the same as every other body.
    /// </summary>
    private async Task<GovernmentLiveDecision> QiwaDecisionAsync(Guid tenantId, CancellationToken ct)
    {
        var moduleOn = !await db.TenantFeatureFlags.AsNoTracking()
            .AnyAsync(f => f.TenantId == tenantId && f.FeatureKey == FeatureKeys.QiwaIntegration && !f.IsEnabled, ct);
        var credential = await db.QiwaApiCredentials.AsNoTracking()
            .AnyAsync(c => c.TenantId == tenantId && c.ClientId != "" && c.EncryptedClientSecret != "", ct);
        return GovernmentLivePolicy.Decide(GovernmentBodies.Qiwa, new GovernmentLiveGates(
            QiwaLiveAdapterPolicy.HasPartnerAgreement(configuration), moduleOn, credential, QiwaLiveAdapterPolicy.LiveRequested(configuration)));
    }

    private IGovernmentAdapter? AdapterFor(string body) =>
        body == GovernmentBodies.Qiwa ? qiwa : providers.FirstOrDefault(p => p.Body == body);

    private static GovernmentCapabilityDto Dto(string body, string mode, string reason, IReadOnlyList<string> channels)
    {
        var text = ReasonTexts[reason];
        return new GovernmentCapabilityDto(body, mode, reason, text.En, text.Ar, channels);
    }
}
