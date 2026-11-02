using System.Text;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Zayra.Api.Infrastructure.Http;

/// <summary>
/// Keeps the exact request bytes (as UTF-8 text) in <c>HttpContext.Items[ItemKey]</c> BEFORE model binding, then rewinds the
/// stream so binding still works. A signed device's HMAC covers the bytes it sent, not whatever the serializer would
/// re-emit. Only put this on small, size-limited endpoints (it buffers the whole body).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CaptureRawBodyAttribute : Attribute, IAsyncResourceFilter
{
    public const string ItemKey = "kynexone.raw-body";

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        context.HttpContext.Items[ItemKey] = await reader.ReadToEndAsync(context.HttpContext.RequestAborted);
        request.Body.Position = 0;
        await next();
    }
}
