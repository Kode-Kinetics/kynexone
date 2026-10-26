using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Zayra.Api.Infrastructure.Finance;

namespace Zayra.Api.Infrastructure.Filters;

/// <summary>
/// Caps a free-text reason at 500 characters before the action runs: 422 with an English and an Arabic message (not the framework's bare 400, and
/// before any work or audit write). Reasons go into the append-only, hash-chained audit trail for good, so they are bounded at the door. Looks at
/// action arguments named like the property (a query-string <c>reason</c>) and at those properties on a bound body. With no names it measures
/// <c>Reason</c> and <c>CostCenterReason</c>; a controller whose other DTOs allow longer text names only the property it means.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ReasonLimitAttribute : ActionFilterAttribute
{
    public const int MaxLength = 500;
    public const string ErrorCode = "reason_too_long";

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Props = new();
    private readonly string[] _names;

    public ReasonLimitAttribute(params string[] properties) =>
        _names = properties is { Length: > 0 } ? properties : ["Reason", "CostCenterReason"];

    public static object Body { get; } = CostAccountingGuards.Error(ErrorCode,
        $"A reason can be at most {MaxLength} characters.",
        $"يجب ألا يزيد السبب على {MaxLength} حرف.");

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        foreach (var (argName, value) in context.ActionArguments)
        {
            if (value is null) continue;
            if (value is string text)
            {
                if (_names.Any(n => string.Equals(n, argName, StringComparison.OrdinalIgnoreCase)) && TooLong(text)) { Refuse(context); return; }
                continue;
            }
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) continue;
            foreach (var name in _names)
            {
                var property = Props.GetOrAdd((type, name), k => k.Item1.GetProperty(k.Item2, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
                if (property?.PropertyType == typeof(string) && property.GetValue(value) is string reason && TooLong(reason)) { Refuse(context); return; }
            }
        }
    }

    private static bool TooLong(string reason) => reason.Trim().Length > MaxLength;
    private static void Refuse(ActionExecutingContext context) => context.Result = new ObjectResult(Body) { StatusCode = StatusCodes.Status422UnprocessableEntity };
}
