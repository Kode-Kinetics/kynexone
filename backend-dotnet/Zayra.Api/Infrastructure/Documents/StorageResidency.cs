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

    private readonly StorageOptions _options;
    private readonly Func<CancellationToken, Task<string?>>? _bucketRegionProbe;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private string? _cachedRegion;
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

    /// <summary>The region the bucket reports, read once and cached (a failure is retried after <see cref="ProbeFailureRetry"/>).</summary>
    public async Task<(string? Region, string? Error)> BucketRegionAsync(CancellationToken ct)
    {
        if (!IsS3) return (Local, null);
        if (_bucketRegionProbe is null) return (null, "this deployment cannot read its bucket's region");
        if (_cachedRegion is not null) return (_cachedRegion, null);
        if (_probeError is not null && DateTime.UtcNow - _probeFailedAtUtc < ProbeFailureRetry) return (null, _probeError);
        await _probeLock.WaitAsync(ct);
        try
        {
            if (_cachedRegion is not null) return (_cachedRegion, null);
            try
            {
                var region = await _bucketRegionProbe(ct);
                if (string.IsNullOrWhiteSpace(region)) throw new InvalidOperationException("storage returned no bucket region");
                _cachedRegion = Normalize(region);
                _probeError = null;
                return (_cachedRegion, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _probeError = $"the bucket's region could not be read from storage ({ex.GetType().Name}: {ex.Message})";
                _probeFailedAtUtc = DateTime.UtcNow;
                return (null, _probeError);
            }
        }
        finally { _probeLock.Release(); }
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
