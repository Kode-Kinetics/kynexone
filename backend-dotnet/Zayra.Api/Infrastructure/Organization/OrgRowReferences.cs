using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Zayra.Api.Application.Organization;

namespace Zayra.Api.Infrastructure.Organization;

public enum OrgRefShape { Guid, Text, JsonList }

/// <summary>One column of the model that can point at an organisation row.</summary>
public sealed record OrgReference(Type Entity, string Property, OrgRefShape Shape);

/// <summary>
/// Discovers, from the EF model, every column that can hold a reference to a branch, department, designation, grade, cost
/// centre or location, so a new table that adds such a column is counted without anyone remembering this file.
/// <see cref="OrgRowReferencesRatchetTests"/> fails when a matching column cannot be counted and is not allowlisted below.
/// </summary>
public static class OrgRowReferences
{
    /// <summary>Column-name stem per kind. A Guid column ending in the stem matches (ParentDepartmentId, NewDepartmentId,
    /// SegmentCostCenterId…). Location is exact, because AttendanceLocationId is a different table.</summary>
    private static readonly Dictionary<OrgRowKind, string> Stems = new()
    {
        [OrgRowKind.Branch] = "BranchId", [OrgRowKind.Department] = "DepartmentId", [OrgRowKind.Designation] = "DesignationId",
        [OrgRowKind.Grade] = "GradeId", [OrgRowKind.CostCenter] = "CostCenterId", [OrgRowKind.Location] = "LocationId",
    };

    /// <summary>Columns that match by name but are NOT usage, each with the reason. Entity.Property.</summary>
    public static readonly IReadOnlyDictionary<string, string> NotUsage = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GradePayScaleComponent.GradeId"] = "The grade's own pay-scale lines: they are part of the grade, not a record that uses it.",
    };

    /// <summary>Entities that match by name but cannot be counted because they carry no TenantId to pin the count to.</summary>
    public static readonly IReadOnlyDictionary<string, string> CannotBePinned = new Dictionary<string, string>(StringComparer.Ordinal);

    public sealed record Discovery(IReadOnlyList<OrgReference> References, IReadOnlyList<string> Unpinnable);

    public static Discovery For(IModel model, OrgRowKind kind)
    {
        var stem = Stems[kind];
        var jsonStem = stem.Replace("Id", "Ids", StringComparison.Ordinal);
        var refs = new List<OrgReference>();
        var unpinnable = new List<string>();
        foreach (var entity in model.GetEntityTypes())
        {
            if (entity.IsOwned() || entity.ClrType.IsAbstract || entity.GetTableName() is null || entity.FindPrimaryKey() is null) continue;
            foreach (var property in entity.GetProperties())
            {
                var name = property.Name;
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                OrgRefShape? shape = null;
                if (type == typeof(Guid) && (kind == OrgRowKind.Location ? name == stem : name.EndsWith(stem, StringComparison.Ordinal))) shape = OrgRefShape.Guid;
                else if (type == typeof(string) && kind == OrgRowKind.Location && name == "WorkLocationId") shape = OrgRefShape.Text;
                else if (type == typeof(string) && kind != OrgRowKind.Location && name.EndsWith("Json", StringComparison.Ordinal) && name.Contains(jsonStem, StringComparison.Ordinal)) shape = OrgRefShape.JsonList;
                if (shape is null) continue;
                var key = $"{entity.ClrType.Name}.{name}";
                if (NotUsage.ContainsKey(key)) continue;
                if (entity.FindProperty("TenantId") is null) { unpinnable.Add(key); continue; }
                refs.Add(new OrgReference(entity.ClrType, name, shape.Value));
            }
        }
        return new Discovery(refs, unpinnable);
    }

    /// <summary>The stable key the client translates (lower camel of the entity name).</summary>
    public static string KindOf(string entity) => char.ToLowerInvariant(entity[0]) + entity[1..];

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["Employee"] = "employees", ["Position"] = "positions", ["Department"] = "departments", ["Designation"] = "designations",
        ["Location"] = "locations", ["Branch"] = "branches", ["GradeEntitlement"] = "benefit and loan limits",
        ["ApprovalWorkflow"] = "approval workflows", ["DepartmentStaffingBudget"] = "staffing budgets",
    };

    /// <summary>English plural for the server's own sentence; the client renders its own from <see cref="KindOf"/>.</summary>
    public static string LabelOf(string entity) =>
        Labels.TryGetValue(entity, out var label) ? label : string.Concat(entity.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())) + " records";
}
