using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Infrastructure.OpenApi;

/// <summary>
/// The ERP document authenticates with an API key, not a person's JWT: replace the global Bearer
/// definition with the ApiKey header scheme on that document only. The "v1" document is untouched.
/// </summary>
public sealed class ErpApiKeyOpenApiFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        if (context.DocumentName != ErpApiKeyDefaults.OpenApiDocument) return;

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes.Clear();
        document.Components.SecuritySchemes[ErpApiKeyDefaults.Scheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "Authorization",
            Description = "'ApiKey kx_live_…' in the Authorization header (or the bare key in X-Api-Key). "
                + "Keys are scoped to finance.gl.exports.read and, optionally, finance.gl.exports.confirm.",
        };
        document.SecurityRequirements = new List<OpenApiSecurityRequirement>
        {
            new()
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ErpApiKeyDefaults.Scheme },
                }] = Array.Empty<string>(),
            },
        };
    }
}
