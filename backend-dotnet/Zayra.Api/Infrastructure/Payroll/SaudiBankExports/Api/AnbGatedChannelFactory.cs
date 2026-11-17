using Microsoft.Extensions.Logging;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>
/// The ONLY place the ANB client and channel are built (their constructors are internal; ratchet I fails on any other construction,
/// registration or resolution). It hands out the channel wrapped in <see cref="GatedBankPayrollChannel"/>, so every call, the token
/// POST included, passes the live decision and, for a submit, the committed intent first. Not registered in DI: wiring is wave 2,
/// and must pass <see cref="AnbHttpHandlerFactory.CreateHandler"/> (or <c>GovernmentHttp.CreateHandler</c>) as the handler, with no
/// retry handler in front of it.
/// </summary>
public static class AnbGatedChannelFactory
{
    public static GatedBankPayrollChannel Create(HttpMessageHandler handler, IAnbEndpointConfig config, IAnbCredentialSource credentials,
        IBankChannelGate gate, IGovernmentExchangeRecorder recorder, ILoggerFactory logs, TimeProvider? clock = null, AnbClientLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(logs);
        var client = new AnbPayrollApiClient(handler, config, credentials, logs.CreateLogger<AnbPayrollApiClient>(), clock, limits);
        return new GatedBankPayrollChannel(new AnbPayrollChannel(client), gate, recorder, logs.CreateLogger<GatedBankPayrollChannel>());
    }
}
