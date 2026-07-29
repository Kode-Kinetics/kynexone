using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        // Transition escape hatch: durable object storage is the REQUIRED default in Production, but
        // provisioning S3 is an ops action with credentials this build cannot perform. Until it's set
        // up, an operator may set Storage__AllowEphemeral=true to explicitly acknowledge running on
        // ephemeral disk (uploaded documents are lost on restart) — the boot then emits a loud warning
        // instead of hard-failing. Remove the flag (or provision S3) to restore the hard guarantee.
        var allowEphemeral = configuration.GetValue<bool>($"{StorageOptions.SectionName}:AllowEphemeral");

        if (!isDevelopment && !useS3 && !allowEphemeral)
            throw new InvalidOperationException(
                "[Storage] Production requires durable object storage. Set Storage__Provider=s3 with " +
                "Storage__Bucket/Storage__AccessKey/Storage__SecretKey (and pin Storage__Region/Storage__Endpoint " +
                "to an approved GCC/KSA jurisdiction for PDPL residency). The Render dyno disk is ephemeral on " +
                "plan:free — uploaded compliance documents would be lost on every restart. To run temporarily on " +
                "ephemeral storage while S3 is being provisioned, set Storage__AllowEphemeral=true (documents WILL " +
                "be lost on restart — acknowledged).");

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

        if (useS3)
        {
            // CreatePrimitives + the S3DocumentStorage ctor are internal but live in this assembly.
            var primitives = S3DocumentStorage.CreatePrimitives(opts);
            services.AddSingleton(opts);
            services.AddScoped<IDocumentStorage>(sp =>
                new S3DocumentStorage(primitives, opts, sp.GetRequiredService<ILogger<S3DocumentStorage>>()));
        }
        else
        {
            if (!isDevelopment)
                Console.WriteLine(
                    "[Storage] WARNING: running on EPHEMERAL LocalDocumentStorage in a non-Development " +
                    "environment because Storage__AllowEphemeral=true. Uploaded documents (compliance " +
                    "records) are LOST on every restart. Provision S3 (Storage__Provider=s3 + creds) and " +
                    "remove Storage__AllowEphemeral to restore durability.");
            services.AddScoped<IDocumentStorage, LocalDocumentStorage>();
        }

        return services;
    }
}
