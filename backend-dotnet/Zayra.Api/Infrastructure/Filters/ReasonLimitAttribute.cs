using Microsoft.AspNetCore.Mvc.Filters;

namespace Zayra.Api.Infrastructure.Filters;

public sealed class ReasonLimitAttribute : ActionFilterAttribute
{
    public ReasonLimitAttribute(params string[] properties) { }
}
