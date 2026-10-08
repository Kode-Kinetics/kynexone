using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zayra.Api.Infrastructure.Documents;

/// <summary>
/// P0-5: config-selected document storage with a Production fail-fast.
///
/// Render's free/starter dynos have no persistent disk and spin down on idle, so
/// <see cref="LocalDocumentStorage"/> (writes under ContentRootPath) loses every uploaded
/// compliance document on the next restart. In any non-Development environment we therefore
/// REQUIRE a durable object-storage backend (S3/R2) and refuse to boot otherwise, rather than
/// silently accepting uploads that will vanish (and fail statutory retention).
///
/// The selection + validation is factored out of Program.cs into a static helper so it can be
/// unit-tested without booting the whole host (no WebApplicationFactory needed).
/// </summary>
public static class DocumentStorageRegistration
{
    /// <summary>
    /// Reads and validates <see cref="StorageOptions"/> for the given environment.
    /// Throws <see cref="InvalidOperationException"/> (fail-fast) when:
    ///   • the environment is not Development and the provider is not durable ("s3"); or
    ///   • the provider is "s3" but Bucket/AccessKey/SecretKey are not fully configured.
    /// Returns the resolved options on success.
    /// </summary>
    public static StorageOptions ResolveAndValidate(IConfiguration configuration, bool isDevelopment)
    {
        var opts = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        var useS3 = string.Equals(opts.Provider, "s3", StringComparison.OrdinalIgnoreCase);
        if (!isDevelopment && !useS3)
            throw new InvalidOperationException(
                "[Storage] Production requires durable object storage. Set Storage__Provider=s3 with " +
                "Storage__Bucket/Storage__AccessKey/Storage__SecretKey (and pin Storage__Region/Storage__Endpoint " +
                "to an approved GCC/KSA jurisdiction for PDPL residency). The Render dyno disk is ephemeral on " +
                "plan:free — uploaded compliance documents would be lost on every restart.");

        if (useS3 && (string.IsNullOrWhiteSpace(opts.Bucket)
                      || string.IsNullOrWhiteSpace(opts.AccessKey)
                      || string.IsNullOrWhiteSpace(opts.SecretKey)))
            throw new InvalidOperationException(
                "[Storage] Provider=s3 but Bucket/AccessKey/SecretKey are not fully configured.");

        return opts;
    }

    /// <summary>Validates config (fail-fast) and registers the selected <see cref="IDocumentStorage"/>.</summary>
    public static IServiceCollection AddDocumentStorage(this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        var opts = ResolveAndValidate(configuration, isDevelopment);
        var useS3 = string.Equals(opts.Provider, "s3", StringComparison.OrdinalIgnoreCase);
        // Where documents really land, checked against Storage:ResidencyAllowList (selfie attendance needs KSA): the
        // configured endpoint/region AND the region the bucket reports (read at startup, cached).
        services.AddHostedService<StorageResidencyStartupProbe>();

        if (useS3)
        {
            // CreatePrimitives + the S3DocumentStorage ctor are internal but live in this assembly.
            var primitives = S3DocumentStorage.CreatePrimitives(opts);
            services.AddSingleton(new StorageResidency(opts, ct => primitives.GetBucketRegionAsync(opts.Bucket, ct)));
            services.AddSingleton(opts);
            services.AddScoped<IDocumentStorage>(sp =>
                new S3DocumentStorage(primitives, opts, sp.GetRequiredService<ILogger<S3DocumentStorage>>()));
        }
        else
        {
            services.AddSingleton(new StorageResidency(opts));
            services.AddScoped<IDocumentStorage, LocalDocumentStorage>();
        }

        return services;
    }
}

/// <summary>
/// Reads the bucket's own region once at startup (and logs the residency verdict), so the first selfie request does
/// not pay for it. A failure is not fatal: the check is retried on the next read and treated as not resident meanwhile.
/// </summary>
internal sealed class StorageResidencyStartupProbe(StorageResidency residency, ILogger<StorageResidencyStartupProbe> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var verdict = await residency.CheckAsync(StorageResidency.Ksa, timeout.Token);
            log.LogInformation("Storage residency ({Jurisdiction}): {Resident} — {Reason}", verdict.Jurisdiction, verdict.Resident, verdict.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(ex, "Storage residency could not be checked at startup; it is checked again on first use.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
