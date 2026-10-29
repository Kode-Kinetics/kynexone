using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Zayra.Api.Infrastructure.Http;

/// <summary>
/// Brotli/gzip for JSON responses, EXCEPT authentication and MFA endpoints. Those responses carry tokens, TOTP secrets
/// and backup codes next to caller-influenced text, the shape a BREACH-style compression oracle needs; they are tiny, so
/// skipping compression costs nothing. Everything else (including HTTPS) is compressed.
/// </summary>
public sealed class KynexResponseCompressionProvider : IResponseCompressionProvider
{
    private static readonly string[] ExcludedPrefixes = ["/api/auth", "/api/platform/auth"];

    private readonly ResponseCompressionProvider _inner;

    public KynexResponseCompressionProvider(IServiceProvider services, IOptions<ResponseCompressionOptions> options) =>
        _inner = new ResponseCompressionProvider(services, options);

    public static bool IsExcluded(PathString path) =>
        ExcludedPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    public ICompressionProvider? GetCompressionProvider(HttpContext context) =>
        IsExcluded(context.Request.Path) ? null : _inner.GetCompressionProvider(context);

    public bool CheckRequestAcceptsCompression(HttpContext context) =>
        !IsExcluded(context.Request.Path) && _inner.CheckRequestAcceptsCompression(context);

    public bool ShouldCompressResponse(HttpContext context) =>
        !IsExcluded(context.Request.Path) && _inner.ShouldCompressResponse(context);
}

public static class KynexResponseCompressionExtensions
{
    public static IServiceCollection AddKynexResponseCompression(this IServiceCollection services)
    {
        // Registered first so AddResponseCompression's TryAdd keeps ours.
        services.AddSingleton<IResponseCompressionProvider, KynexResponseCompressionProvider>();
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
        return services;
    }
}
