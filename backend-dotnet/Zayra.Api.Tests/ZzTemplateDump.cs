using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Tests;

public sealed class ZzTemplateDump
{
    [Fact]
    public void Dump()
    {
        var asm = typeof(Csv).Assembly;
        var sb = new StringBuilder();
        var eps = asm.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetParameters().Length == 0)
                .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>().Select(a => a.Template)
                    .Where(r => !string.IsNullOrWhiteSpace(r) && (r!.TrimEnd('/').Split('/')[^1].Equals("template", StringComparison.OrdinalIgnoreCase) || r.TrimEnd('/').Split('/')[^1].EndsWith("-template", StringComparison.OrdinalIgnoreCase)))
                    .Select(r => (Name: $"{t.Name}.{m.Name}", Route: r!, Type: t, Method: m))))
            .DistinctBy(e => e.Name).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

        foreach (var e in eps)
        {
            var c = (ControllerBase)RuntimeHelpers.GetUninitializedObject(e.Type);
            c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            var res = e.Method.Invoke(c, null);
            var docs = res switch
            {
                FileContentResult f => new[] { (e.Name, Encoding.UTF8.GetString(f.FileContents)) },
                ContentResult ct => new[] { (e.Name, ct.Content ?? "") },
                ObjectResult o when o.Value is IDictionary<string, string> d => d.Select(kv => ($"{e.Name}[{kv.Key}]", kv.Value)).ToArray(),
                _ => new[] { (e.Name, "??") }
            };
            foreach (var (name, csv) in docs)
            {
                sb.AppendLine($"=== {name}  (GET {e.Route})");
                string? hdr = null;
                foreach (var line in csv.Replace("\r\n", "\n").Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) { hdr = null; continue; }
                    if (hdr is null) { hdr = line; sb.AppendLine($"    headers={Csv.SplitRow(line).Count}"); }
                    else sb.AppendLine($"    row={Csv.SplitRow(line).Count}");
                }
                sb.AppendLine(csv);
                sb.AppendLine();
            }
        }
        File.WriteAllText(
            "/private/tmp/claude-501/-Users-zackkhan-Downloads-KynexOne/6c30fca1-b845-4381-a30f-4866005f27be/scratchpad/templates.txt",
            sb.ToString());
    }
}
