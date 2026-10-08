namespace Zayra.Api.Infrastructure.Documents;

/// <summary>The outcome of a residency check: whether the deploy's storage location is on the jurisdiction's allow-list.</summary>
public sealed record StorageResidencyVerdict(bool Resident, string Jurisdiction, string ActualLocation, string Reason);

/// <summary>
/// Checks where this deploy ACTUALLY stores documents against the operator's allow-list
/// (<see cref="StorageOptions.ResidencyAllowList"/>). A typed-in "KSA" proves nothing; the configured endpoint or region
/// is what decides where an uploaded face image lands.
/// <list type="bullet">
///   <item>S3 with an endpoint: the endpoint host is the location (the region only signs requests).</item>
///   <item>S3 without one: the region, or <c>us-east-1</c> when it is empty or <c>auto</c> — what
///     <see cref="S3DocumentStorage.CreatePrimitives"/> really connects to.</item>
///   <item>Local disk: <c>local</c> (Development only; production refuses to boot without object storage).</item>
/// </list>
/// The allow-list is empty by default, so nothing is resident until an operator lists a real in-jurisdiction bucket.
/// </summary>
public sealed class StorageResidency
{
    public const string Ksa = "KSA";

    private readonly StorageOptions _options;

    public StorageResidency(StorageOptions options) => _options = options;

    /// <summary>No allow-list at all: nothing is resident. What a caller gets when no configuration was supplied.</summary>
    public static StorageResidency Unconfigured { get; } = new(new StorageOptions());

    public string ActualLocation
    {
        get
        {
            if (!string.Equals(_options.Provider, "s3", StringComparison.OrdinalIgnoreCase)) return "local";
            if (!string.IsNullOrWhiteSpace(_options.Endpoint)) return Normalize(_options.Endpoint);
            return string.IsNullOrWhiteSpace(_options.Region) || string.Equals(_options.Region, "auto", StringComparison.OrdinalIgnoreCase)
                ? "us-east-1"
                : Normalize(_options.Region);
        }
    }

    public StorageResidencyVerdict Check(string jurisdiction)
    {
        var actual = ActualLocation;
        var allowed = _options.ResidencyAllowList.TryGetValue(jurisdiction, out var list) && list is not null
            ? list.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        if (allowed.Count == 0)
            return new(false, jurisdiction, actual,
                $"No storage location is approved for {jurisdiction} data on this deployment (Storage:ResidencyAllowList:{jurisdiction} is empty), "
                + $"and documents are stored at {actual}.");
        return allowed.Contains(actual)
            ? new(true, jurisdiction, actual, $"Documents are stored at {actual}, which is approved for {jurisdiction} data.")
            : new(false, jurisdiction, actual,
                $"Documents are stored at {actual}, which is not on the approved list for {jurisdiction} data "
                + $"(Storage:ResidencyAllowList:{jurisdiction}).");
    }

    /// <summary>Lower-case host or region: scheme, path and trailing slash removed.</summary>
    internal static string Normalize(string value)
    {
        var v = value.Trim();
        if (Uri.TryCreate(v, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)) v = uri.Host;
        return v.TrimEnd('/').ToLowerInvariant();
    }
}
