using System.Globalization;
using System.Text.Json;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Reports;

namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

public sealed record ReconExportFile(byte[] Bytes, string ContentType, string FileName, string Label);

/// <summary>
/// Renders the reconciliation as a workbook or a CSV. A signed-off run exports what was signed (the stored snapshot, with the
/// written explanations), never the live figures that may have moved since; anything else is watermarked UNSIGNED DRAFT,
/// and a department- or team-scoped view PARTIAL, in the summary and in the file name. Money is invariant-culture, two decimals.
/// </summary>
public static class ReconExporter
{
    public const string LabelSigned = "SIGNED OFF";
    public const string LabelUnsigned = "UNSIGNED DRAFT";
    public const string LabelPartial = "PARTIAL";

    public static string Label(bool signedOff, bool partial) => partial ? LabelPartial : signedOff ? LabelSigned : LabelUnsigned;

    public static ReconExportFile Create(ReconBuild build, string format)
    {
        var xlsx = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase);
        var signoff = build.ActiveSignoff;
        var useSnapshot = signoff is not null && !build.Partial;
        var dto = useSnapshot ? FromSnapshot(signoff!.SnapshotJson, build.Dto) : build.Dto;
        var label = Label(useSnapshot, build.Partial);

        var tables = Tables(dto, label, useSnapshot ? signoff : null, build.Dto.Scope.Level);
        var stem = $"payroll-recon-{dto.Period}" + (build.Partial ? "-PARTIAL" : useSnapshot ? "" : "-UNSIGNED-DRAFT");
        return xlsx
            ? new ReconExportFile(ReportWorkbookWriter.ToXlsx(tables), ReportWorkbookWriter.ContentType, stem + ".xlsx", label)
            : new ReconExportFile(ReportTabulator.ToCsv(tables), CsvDownload.ContentType, stem + ".csv", label);
    }

    /// <summary>The stored snapshot, with the run's own header facts kept; falls back to the live bridge if the snapshot cannot be read.</summary>
    private static ReconDto FromSnapshot(string json, ReconDto live)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<ReconDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return stored is null ? live : stored with { Signoff = live.Signoff, Scope = live.Scope };
        }
        catch (JsonException)
        {
            return live;
        }
    }

    public static IReadOnlyList<ReportTable> Tables(ReconDto dto, string label, Zayra.Api.Models.PayrollRunReconSignoff? signoff, string scopeLevel)
    {
        var tables = new List<ReportTable>
        {
            Summary(dto, label, signoff, scopeLevel),
            Volume(dto),
            Value(dto),
            FinalSettlement(dto),
        };
        if (signoff is { Explanations.Count: > 0 })
            tables.Add(new ReportTable("Explanations", new[] { "Scope", "Key", "Explanation" },
                signoff.Explanations.OrderBy(e => e.Scope, StringComparer.Ordinal).ThenBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => (IReadOnlyList<string>)new[] { e.Scope, e.Key, e.Text }).ToList()));
        return tables;
    }

    private static ReportTable Summary(ReconDto d, string label, Zayra.Api.Models.PayrollRunReconSignoff? signoff, string scopeLevel)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "Status", label },
            new[] { "Run", d.RunId.ToString() },
            new[] { "Period", d.Period },
            new[] { "Compared with", d.PriorPeriod ?? "No comparison run" },
            new[] { "Mode", d.Mode },
            new[] { "Currency", d.Currency },
            new[] { "Scope", scopeLevel },
            new[] { "Variance threshold %", Num(d.Thresholds.Pct) },
            new[] { "Variance minimum amount", Money(d.Thresholds.MinAmount) },
        };
        if (d.PriorNotFinal) rows.Add(new[] { "Note", "The comparison run is not final yet." });
        if (signoff is not null)
        {
            rows.Add(new[] { "Signed off by", signoff.SignedOffByName });
            rows.Add(new[] { "Signed off at (UTC)", signoff.SignedOffAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) });
            rows.Add(new[] { "Snapshot hash", signoff.SnapshotHash });
        }
        return new ReportTable("Summary", new[] { "Field", "Value" }, rows);
    }

    private static ReportTable Volume(ReconDto d)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "OPENING", "", Int(d.Volume.Opening), "", "" },
        };
        rows.AddRange(d.Volume.Lines.Select(l => (IReadOnlyList<string>)new[]
        {
            l.Category, l.Kind, Int(l.Count), l.FsApproved is { } a ? Int(a) : "", l.FsMissing is { } m ? Int(m) : "",
        }));
        rows.Add(new[] { "CLOSING", "", Int(d.Volume.Closing), "", "" });
        return new ReportTable("Volume", new[] { "Category", "Kind", "Count", "FS approved", "FS missing" }, rows);
    }

    private static readonly string[] ValueHeaders =
    {
        "Section", "Code", "Item", "Prior", "New joiner", "Rehire", "Late joiner", "Reinstated", "Transfer in",
        "Leaver", "Transfer out", "Not paid", "Unexplained", "Package", "Proration", "Variable", "Arrears", "Attendance", "Loans",
        "Statutory", "Other", "Current", "Delta", "Delta %", "Flagged", "Explanation",
    };

    private static ReportTable Value(ReconDto d)
    {
        var rows = new List<IReadOnlyList<string>>();
        void Add(string section, string code, string label, ReconBridgeRowDto i, bool? flagged, string? explanation) => rows.Add(new[]
        {
            section, code, label, Money(i.Prior),
            Money(i.Adds.NewJoiner), Money(i.Adds.Rehire), Money(i.Adds.LateJoiner), Money(i.Adds.Reinstated), Money(i.Adds.TransferIn),
            Money(i.Exits.Leaver), Money(i.Exits.TransferOut), Money(i.Exits.NotPaid), Money(i.Exits.Unexplained),
            Money(i.Continuing.Package), Money(i.Continuing.Proration), Money(i.Continuing.Variable), Money(i.Continuing.Arrears),
            Money(i.Continuing.Attendance), Money(i.Continuing.Loans), Money(i.Continuing.Statutory), Money(i.Continuing.Other),
            Money(i.Current), Money(i.Delta), i.DeltaPct is { } pct ? Num(pct) : "", flagged is null ? "" : flagged.Value ? "Yes" : "No", explanation ?? "",
        });
        var h = d.Value.Headline;
        Add("Headline", "GROSS", "Gross pay", h.Gross, null, null);
        Add("Headline", "DEDUCTIONS", "Employee deductions", h.Deductions, null, null);
        Add("Headline", "NET", "Net pay", h.Net, null, null);
        Add("Headline", "EMPLOYER", "Employer cost", h.Employer, null, null);
        foreach (var s in d.Value.Sections)
            foreach (var i in s.Items)
                Add(s.Kind, i.Code, i.Label, i, i.Flagged, i.Explanation);
        return new ReportTable("Value", ValueHeaders, rows);
    }

    private static ReportTable FinalSettlement(ReconDto d)
    {
        var rows = new List<IReadOnlyList<string>>();
        rows.AddRange(d.FinalSettlement.Cells.Select(c => (IReadOnlyList<string>)new[] { "Cell", c.Code, Int(c.Count), "", "" }));
        rows.AddRange(d.FinalSettlement.PerComponent.Select(c => (IReadOnlyList<string>)new[] { "Component", c.Code, Money(c.Prior), Money(c.Current), Money(c.Delta) }));
        rows.AddRange(d.FinalSettlement.LeaversWithoutFs.Select(l => (IReadOnlyList<string>)new[] { "Leaver without settlement", l.EmployeeCode, l.EmployeeName, l.LastWorkingDay ?? "", "" }));
        return new ReportTable("Final settlement", new[] { "Block", "Name", "Count / prior / name", "Current / last working day", "Delta" }, rows);
    }

    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Num(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
}
