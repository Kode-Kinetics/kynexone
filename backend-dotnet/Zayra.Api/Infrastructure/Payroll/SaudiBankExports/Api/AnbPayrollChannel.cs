using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>
/// ANB behind the bank-neutral <see cref="IBankPayrollChannel"/>. Thin: it picks the two frozen files by the
/// generator's own file names, passes the neutral options through as ANB's request headers, and maps the
/// status read onto the batch with <see cref="AnbPayrollStatusMapper"/>. NOT LIVE and not registered. Built only by
/// <see cref="AnbGatedChannelFactory"/>, which hands it out behind <see cref="GatedBankPayrollChannel"/>.
/// <para>Transport: the client sends only through GovernmentHttp (the guarded <c>gov-bank_anb</c> client, allow-listed URLs, the live
/// gate, the credential store); there is no handler to configure here.</para>
/// <para>ANB's fee headers (feescategorycode, feesandvataccount, feesfrequency) have no published meaning
/// and are not sent through this channel.</para>
/// </summary>
public sealed class AnbPayrollChannel : IBankPayrollChannel
{
    public const string Id = "anb";

    private readonly AnbPayrollApiClient _client;

    // Internal: built only by AnbGatedChannelFactory, behind GatedBankPayrollChannel (ratchet I).
    internal AnbPayrollChannel(AnbPayrollApiClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    public string BankId => Id;

    public bool IsLiveIntegration => false;

    public Task<BankSubmitOutcome> SubmitAsync(BankSubmitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Files.Where(f => string.Equals(f.FileName, AnbConnectCsvGenerator.HeaderFileName, StringComparison.Ordinal)).ToList();
        var body = request.Files.Where(f => string.Equals(f.FileName, AnbConnectCsvGenerator.BodyFileName, StringComparison.Ordinal)).ToList();
        if (request.Files.Count != 2 || header.Count != 1 || body.Count != 1)
            return Task.FromResult(new BankSubmitOutcome(BankSubmitOutcomeKind.Refused, BankChannelCodes.ArtifactInconsistent, RequestSent: false));

        var headers = new AnbSubmitHeaders(
            ProfileId: request.Options.ProfileId,
            AutoWpsFileUpload: request.Options.AutoWpsUpload,
            NationalUnifiedNo: request.Options.NationalUnifiedNo);
        return _client.SubmitAsync(new AnbSubmitRequest(request.Scope, header[0], body[0], headers, request.PriorOutcomeUnknown,
            request.Intent?.Receipt), ct);
    }

    public async Task<BankStatusSnapshot> GetStatusAsync(BankChannelScope scope, string bankPayrollId, BankBatchBinding binding,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var snapshot = await _client.GetStatusAsync(scope, bankPayrollId, binding.ProfileId, ct);
        return AnbPayrollStatusMapper.Map(snapshot, binding);
    }

    public Task<BankWpsFile> DownloadWpsAsync(BankChannelScope scope, string bankPayrollId, string? profileId, CancellationToken ct) =>
        _client.DownloadWpsAsync(scope, bankPayrollId, profileId, ct);
}
