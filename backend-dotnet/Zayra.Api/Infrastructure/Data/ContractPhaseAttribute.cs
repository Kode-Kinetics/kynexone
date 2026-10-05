namespace Zayra.Api.Infrastructure.Data;

/// <summary>
/// Marks a migration as the deliberate CONTRACT phase of an expand → migrate → contract change
/// (docs/schema/HOW_TO_CHANGE_THE_SCHEMA.md §1): its <c>Up</c> drops, renames or tightens something
/// that code in an already-deployed release has stopped using.
///
/// <para>Without this attribute <c>DestructiveMigrationGuardTests</c> fails any new migration whose
/// <c>Up</c> contains DropColumn, DropTable, RenameColumn, RenameTable, an AlterColumn to NOT NULL
/// without a default, or the raw-SQL equivalents. During a rolling deploy the old instances are still
/// running against the new schema; one of those operations ships them a 500 on every request that
/// touches the column. The reason must name the release that stopped using the old shape.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ContractPhaseAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
