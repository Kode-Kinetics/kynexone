namespace Zayra.Api.Infrastructure.Documents;

/// <summary>
/// The outcome of a residency check. <see cref="ActualLocation"/> is the configured endpoint host (or region);
/// <see cref="BucketRegion"/> is the region the bucket itself reports (null when it could not be read);
/// <see cref="Location"/> is the canonical "where documents land" string stamped on the selfie flag when it is enabled
/// and compared on every policy read (a different value means storage has moved).
/// </summary>
public sealed record StorageResidencyVerdict(bool Resident, string Jurisdiction, string ActualLocation, string Reason,
    string? BucketRegion, string Location);

/// <summary>
/// Checks where this deploy ACTUALLY stores documents against the operator's allow-list
/// (<see cref="StorageOptions.ResidencyAllowList"/>). A typed-in "KSA" proves nothing, and neither does a typed-in
/// region: residency holds only when BOTH
/// <list type="bullet">
///   <item>the configured location — the S3 endpoint host when there is one, else the configured region (or
///     <c>us-east-1</c> when it is empty or <c>auto</c>, which is what <see cref="S3DocumentStorage.CreatePrimitives"/>
///     really connects to) — and</item>
///   <item>the region the BUCKET reports about itself (S3 GetBucketLocation, read at startup and cached; B2 and MinIO
///     answer it too)</item>
/// </list>
/// are on the allow-list. Local disk is <c>local</c> for both (Development only; production refuses to boot without
/// object storage). The allow-list is empty by default, so nothing is resident until an operator lists a real
/// in-jurisdiction endpoint and its bucket's region.
/// </summary>
public sealed class StorageResidency
{
    public const string Ksa = "KSA";
    public const string Local = "local";

    /// <summary>A failed bucket-region read is retried after this long (a success is kept for the process lifetime).</summary>
    public static readonly TimeSpan ProbeFailureRetry = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest one bucket-region read may take (review 3, item 6). A storage endpoint that hangs must not hang every
    /// policy read behind the probe lock: past this the read counts as failed, so residency is NOT confirmed (fail closed).
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly StorageOptions _options;
    private readonly Func<CancellationToken, Task<string?>>? _bucketRegionProbe;
    private readonly object _probeGate = new();
    /// <summary>The one probe in flight, shared by every caller that arrives while it runs (null when none is).</summary>
    private Task<(string? Region, string? Error)>? _inFlightProbe;
    private volatile string? _cachedRegion;
    private string? _probeError;
    private DateTime _probeFailedAtUtc;

    /// <param name="bucketRegionProbe">Reads the bucket's own region from storage (S3 only). Without one, an S3 deploy's
    /// bucket region is unknown, so nothing is resident.</param>
    public StorageResidency(StorageOptions options, Func<CancellationToken, Task<string?>>? bucketRegionProbe = null)
    {
        _options = options;
        _bucketRegionProbe = bucketRegionProbe;
    }

    /// <summary>No allow-list at all: nothing is resident. What a caller gets when no configuration was supplied.</summary>
    public static StorageResidency Unconfigured { get; } = new(new StorageOptions());

    private bool IsS3 => string.Equals(_options.Provider, "s3", StringComparison.OrdinalIgnoreCase);

    public string ActualLocation
    {
        get
        {
            if (!IsS3) return Local;
            if (!string.IsNullOrWhiteSpace(_options.Endpoint)) return Normalize(_options.Endpoint);
            return string.IsNullOrWhiteSpace(_options.Region) || string.Equals(_options.Region, "auto", StringComparison.OrdinalIgnoreCase)
                ? "us-east-1"
                : Normalize(_options.Region);
        }
    }

    /// <summary>The region the bucket reports, read once and cached (a failure — including a read that takes longer than
    /// <see cref="ProbeTimeout"/> — is retried after <see cref="ProbeFailureRetry"/>, and until then nothing is resident).
    /// <para>Callers that arrive while a probe runs share that ONE probe (and its answer) instead of queueing to run
    /// their own one after another: with storage hanging, 20 concurrent policy reads cost one probe and about one
    /// <see cref="ProbeTimeout"/>, not 20. A caller's own token cancels only its wait, never the shared probe.</para></summary>
    public async Task<(string? Region, string? Error)> BucketRegionAsync(CancellationToken ct)
    {
        if (!IsS3) return (Local, null);
        if (_bucketRegionProbe is null) return (null, "this deployment cannot read its bucket's region");
        if (_cachedRegion is not null) return (_cachedRegion, null);
        Task<(string? Region, string? Error)> probe;
        lock (_probeGate)
        {
            // Re-checked under the gate: the previous probe's answer (a success, or a failure within ProbeFailureRetry)
            // is returned as it is rather than probing again.
            if (_cachedRegion is not null) return (_cachedRegion, null);
            if (_probeError is not null && DateTime.UtcNow - _probeFailedAtUtc < ProbeFailureRetry) return (null, _probeError);
            // Task.Run, so the probe's own completion (which clears _inFlightProbe under this gate) can never run before
            // the assignment below — a synchronously finishing probe would otherwise leave a stale task behind.
            probe = _inFlightProbe ??= Task.Run(RunProbeAsync);
        }
        return await probe.WaitAsync(ct);
    }

    private async Task<(string? Region, string? Error)> RunProbeAsync()
    {
        try
        {
            // A 3-second timeout; WaitAsync as well, so a probe that ignores its token cannot hang us either.
            using var timeout = new CancellationTokenSource(ProbeTimeout);
            string? region;
            try { region = await _bucketRegionProbe!(timeout.Token).WaitAsync(ProbeTimeout); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                throw new TimeoutException($"storage did not answer within {ProbeTimeout.TotalSeconds:0} seconds");
            }
            if (string.IsNullOrWhiteSpace(region)) throw new InvalidOperationException("storage returned no bucket region");
            var normalized = Normalize(region);
            lock (_probeGate)
            {
                _cachedRegion = normalized;
                _probeError = null;
            }
            return (normalized, null);
        }
        catch (Exception ex)
        {
            var error = $"the bucket's region could not be read from storage ({ex.GetType().Name}: {ex.Message})";
            lock (_probeGate)
            {
                _probeError = error;
                _probeFailedAtUtc = DateTime.UtcNow;
            }
            return (null, error);
        }
        finally
        {
            lock (_probeGate) _inFlightProbe = null;
        }
    }

    public async Task<StorageResidencyVerdict> CheckAsync(string jurisdiction, CancellationToken ct = default)
    {
        var actual = ActualLocation;
        var (bucketRegion, probeError) = await BucketRegionAsync(ct);
        var location = Canonical(actual, IsS3 ? _options.Bucket : string.Empty, bucketRegion);
        StorageResidencyVerdict Verdict(bool resident, string reason) => new(resident, jurisdiction, actual, reason, bucketRegion, location);

        var allowed = _options.ResidencyAllowList.TryGetValue(jurisdiction, out var list) && list is not null
            ? list.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        if (allowed.Count == 0)
            return Verdict(false, $"No storage location is approved for {jurisdiction} data on this deployment (Storage:ResidencyAllowList:{jurisdiction} is empty), "
                                  + $"and documents are stored at {actual}.");
        if (!allowed.Contains(actual))
            return Verdict(false, $"Documents are stored at {actual}, which is not on the approved list for {jurisdiction} data "
                                  + $"(Storage:ResidencyAllowList:{jurisdiction}).");
        if (bucketRegion is null)
            return Verdict(false, $"Documents are stored at {actual}, but {probeError ?? "the bucket's region is unknown"}, so residency in {jurisdiction} cannot be confirmed.");
        if (!allowed.Contains(bucketRegion))
            return Verdict(false, $"The storage bucket reports region {bucketRegion}, which is not on the approved list for {jurisdiction} data "
                                  + $"(Storage:ResidencyAllowList:{jurisdiction}).");
        return Verdict(true, $"Documents are stored at {actual} in a bucket that reports region {bucketRegion}, both approved for {jurisdiction} data.");
    }

    /// <summary>The canonical storage location: configured location, bucket and the bucket's reported region.</summary>
    public static string Canonical(string actualLocation, string? bucket, string? bucketRegion) =>
        $"{actualLocation}/{(bucket ?? string.Empty).Trim()}@{bucketRegion ?? "unknown"}".ToLowerInvariant();

    /// <summary>Lower-case host or region: scheme, path and trailing slash removed.</summary>
    internal static string Normalize(string value)
    {
        var v = value.Trim();
        if (Uri.TryCreate(v, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)) v = uri.Host;
        return v.TrimEnd('/').ToLowerInvariant();
    }
}
