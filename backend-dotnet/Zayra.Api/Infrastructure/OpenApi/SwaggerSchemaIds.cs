namespace Zayra.Api.Infrastructure.OpenApi;

/// <summary>
/// Stable OpenAPI component names. Controller-heavy modular APIs commonly reuse
/// short DTO names; namespace-qualified IDs prevent one module from making the
/// entire Swagger document unavailable.
/// </summary>
public static class SwaggerSchemaIds
{
    public static string For(Type type)
        => (type.FullName ?? type.Name).Replace('+', '.');
}
