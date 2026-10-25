using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zayra.Api.Infrastructure.Documents;

/// <summary>
/// P0-5: config-selected document storage with a Production fail-fast.
///
/// A container's own filesystem does not survive the container being replaced, so
/// <see cref="LocalDocumentStorage"/> writing under ContentRootPath would lose every uploaded compliance document on
/// the next upgrade. In any non-Development environment we therefore REQUIRE durable storage — an S3-compatible
/// store the organisation runs, or (the client kit's default) a local directory on a durable volume (<c>Storage__LocalRoot</c>) — and
/// refuse to boot otherwise, rather than silently accepting uploads that will vanish (and fail statutory retention).
///
/// The selection + validation is factored out of Program.cs into a static helper so it can be
/// unit-tested without booting the whole host (no WebApplicationFactory needed).
/// </summary>
public static class DocumentStorageRegistration
{
    /// <summary>
    /// Reads and validates <see cref="StorageOptions"/> for the given environment.
    /// Throws <see cref="InvalidOperationException"/> (fail-fast) when:
    ///   • the environment is not Development and the provider is neither "s3" nor "local" with a usable LocalRoot; or
    ///   • the provider is "s3" but Bucket/AccessKey/SecretKey are not fully configured.
    /// Returns the resolved options on success.
    /// </summary>
    public static StorageOptions ResolveAndValidate(IConfiguration configuration, bool isDevelopment)
    {
        var opts = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        var useS3 = string.Equals(opts.Provider, "s3", StringComparison.OrdinalIgnoreCase);
        if (!isDevelopment && !useS3)
        {
            if (!string.Equals(opts.Provider, "local", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"[Storage] Unknown Storage__Provider '{opts.Provider}'. Use s3 or local.");
            if (LocalRootProblem(opts.LocalRoot) is { } problem)
                throw new InvalidOperationException(
                    "[Storage] Production requires durable document storage. Either set Storage__Provider=s3 with " +
                    "Storage__Endpoint/Storage__Bucket/Storage__AccessKey/Storage__SecretKey (an S3-compatible store), or set " +
                    $"Storage__Provider=local with Storage__LocalRoot pointing at a durable volume: {problem}");
        }

        if (useS3 && (string.IsNullOrWhiteSpace(opts.Bucket)
                      || string.IsNullOrWhiteSpace(opts.AccessKey)
                      || string.IsNullOrWhiteSpace(opts.SecretKey)))
            throw new InvalidOperationException(
                "[Storage] Provider=s3 but Bucket/AccessKey/SecretKey are not fully configured.");

        return opts;
    }

    /// <summary>Why <paramref name="localRoot"/> cannot hold documents, or null when it can (absolute, exists, writable).</summary>
    public static string? LocalRootProblem(string? localRoot)
    {
        if (string.IsNullOrWhiteSpace(localRoot)) return "Storage__LocalRoot is not set.";
        if (!Path.IsPathRooted(localRoot)) return $"Storage__LocalRoot '{localRoot}' is not an absolute path.";
        if (!Directory.Exists(localRoot)) return $"Storage__LocalRoot '{localRoot}' does not exist.";
        try
        {
            var probe = Path.Combine(localRoot, $".kynexone-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Storage__LocalRoot '{localRoot}' is not writable ({ex.GetType().Name}).";
        }
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
            services.AddSingleton(opts);
            services.AddScoped<IDocumentStorage>(sp => new LocalDocumentStorage(
                sp.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(), opts));
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
