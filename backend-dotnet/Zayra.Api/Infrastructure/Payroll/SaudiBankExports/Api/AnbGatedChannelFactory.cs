using Microsoft.Extensions.Logging;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>
/// The ONLY place the ANB client and channel are built (their constructors are internal; ratchet I fails on any other construction,
/// registration or resolution). It hands out the channel wrapped in <see cref="GatedBankPayrollChannel"/>, so every submit passes the
/// live decision and the committed intent first, and the client itself asks the same gate again before every token request and send
/// (E4). The transport is <see cref="GovernmentHttp"/> and nothing else: no handler, HttpClient or client factory can be passed in, so
/// no handler with redirects on or certificate checks off can reach the bank. Not registered in DI: wiring is wave 2.
/// </summary>
public static class AnbGatedChannelFactory
{
    public static GatedBankPayrollChannel Create(GovernmentHttp transport, IBankCredentialSource credentials, IBankChannelGate gate,
        IGovernmentExchangeRecorder recorder, ILoggerFactory logs, TimeProvider? clock = null, AnbClientLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(logs);
        var client = new AnbPayrollApiClient(transport, credentials, gate, logs.CreateLogger<AnbPayrollApiClient>(), clock, limits);
        return new GatedBankPayrollChannel(new AnbPayrollChannel(client), gate, recorder, logs.CreateLogger<GatedBankPayrollChannel>());
    }
}
