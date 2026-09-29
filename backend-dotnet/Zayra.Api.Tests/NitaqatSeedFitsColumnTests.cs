using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Every string the Nitaqat reference seeder writes must fit the column it is written to.
///
/// <para>WHY THIS EXISTS. <c>NitaqatReferenceSeeder.SeedAsync</c> stages size tiers, weight rules,
/// activities and the illustrative grid and commits all four with ONE
/// <c>SaveChangesAsync()</c>. EF does not client-side validate <c>HasMaxLength</c>, so an
/// over-length string is not caught at staging: PostgreSQL rejects the batch with
/// <c>22001: value too long for type character varying(500)</c> and the whole transaction rolls
/// back. Not one section survives.</para>
///
/// <para>What that looked like in the product: <c>GCC_STANDARD</c>'s SourceNote was 509 characters
/// against a varchar(500). <c>Program.cs</c> wraps each seeder in <c>TrySeedAsync</c>, which logs
/// "Seeder 'NitaqatReferenceSeeder' failed — continuing startup" and carries on, so the API booted
/// healthy with <c>nitaqat_activities</c> empty. The Saudization panel then offered an EMPTY
/// economic-activity dropdown while its own refusal told the user to go and set the activity —
/// an unescapable loop, and the panel could never compute a band. A 9-character overrun disabled
/// the feature, and nothing went red.</para>
///
/// <para>This asserts the invariant directly against the EF model rather than a hard-coded 500, so
/// it keeps holding if a column is resized. It needs no database: the model is built from the
/// Npgsql provider without ever opening a connection, exactly as
/// <see cref="NitaqatPlatformScopeSqlTests"/> does.</para>
/// </summary>
public class NitaqatSeedFitsColumnTests
{
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=u;Password=p")
            .Options;
        using var db = new ZayraDbContext(options);
        return db.Model;
    }

    private static int MaxLengthOf<TEntity>(IModel model, string propertyName)
    {
        var property = model.FindEntityType(typeof(TEntity))?.FindProperty(propertyName);
        property.Should().NotBeNull($"{typeof(TEntity).Name}.{propertyName} must exist on the model");
        var max = property!.GetMaxLength();
        max.Should().HaveValue(
            $"{typeof(TEntity).Name}.{propertyName} must declare a MaxLength — an unbounded note " +
            "column would make this guard vacuous");
        return max!.Value;
    }

    /// <summary>Reads a private static array from the seeder and projects one member of each item.</summary>
    private static IReadOnlyList<(string Key, string Value)> SeededStrings(
        string arrayFieldName, string keyMember, string valueMember)
    {
        var field = typeof(NitaqatReferenceSeeder)
            .GetField(arrayFieldName, BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull(
            $"NitaqatReferenceSeeder.{arrayFieldName} is the seeded data this guard exists to check; " +
            "if it was renamed, rename it here rather than deleting the test");

        var items = ((System.Collections.IEnumerable)field!.GetValue(null)!).Cast<object>().ToList();
        items.Should().NotBeEmpty($"{arrayFieldName} must not be empty");

        return items.Select(item =>
        {
            var type = item.GetType();
            // Resolved explicitly so a renamed member fails with the member name rather than a
            // NullReferenceException that reads like a broken test instead of a stale one.
            var keyProperty = type.GetProperty(keyMember);
            var valueProperty = type.GetProperty(valueMember);
            keyProperty.Should().NotBeNull($"{type.Name}.{keyMember} must exist for this guard to name its findings");
            valueProperty.Should().NotBeNull($"{type.Name}.{valueMember} is the string this guard measures");

            var key = keyProperty!.GetValue(item)?.ToString() ?? "(no key)";
            var value = valueProperty!.GetValue(item)?.ToString() ?? string.Empty;
            return (key, value);
        }).ToList();
    }

    [Fact]
    public void EveryWeightRuleSourceNote_FitsTheSourceNoteColumn()
    {
        var max = MaxLengthOf<NitaqatWeightRule>(BuildModel(), nameof(NitaqatWeightRule.SourceNote));
        var notes = SeededStrings("Weights", "Code", "Source");

        // Named so a failure says WHICH rule and by how much, not just "expected <= 500".
        var overlong = notes
            .Where(n => n.Value.Length > max)
            .Select(n => $"{n.Key} is {n.Value.Length} chars ({n.Value.Length - max} over)")
            .ToList();

        overlong.Should().BeEmpty(
            $"every seeded weight-rule SourceNote must fit varchar({max}). One that does not throws " +
            "22001 inside the seeder's single SaveChangesAsync and rolls back EVERY Nitaqat " +
            "reference row — tiers, rules, activities and the grid — leaving the Saudization panel " +
            "with an empty activity dropdown and no way to ever compute a band. Shorten the note; " +
            "do not widen the column to make this pass.");
    }

    [Fact]
    public void EverySeededSizeTierAndActivityName_FitsItsColumn()
    {
        var model = BuildModel();

        var tierNameMax = MaxLengthOf<NitaqatSizeTier>(model, nameof(NitaqatSizeTier.NameEn));
        foreach (var (code, name) in SeededStrings("Tiers", "Code", "NameEn"))
            name.Length.Should().BeLessThanOrEqualTo(tierNameMax,
                $"seeded size tier '{code}' must fit NitaqatSizeTier.NameEn");

        var activityNameMax = MaxLengthOf<NitaqatActivity>(model, nameof(NitaqatActivity.NameEn));
        foreach (var (code, name) in SeededStrings("Activities", "Code", "NameEn"))
            name.Length.Should().BeLessThanOrEqualTo(activityNameMax,
                $"seeded activity '{code}' must fit NitaqatActivity.NameEn");
    }

    /// <summary>
    /// The shared note constants are written to SourceNote on several entities, so they are bounded
    /// by the smallest of those columns.
    /// </summary>
    [Fact]
    public void SharedSourceNoteConstants_FitTheNarrowestSourceNoteColumn()
    {
        var model = BuildModel();
        var narrowest = new[]
        {
            MaxLengthOf<NitaqatWeightRule>(model, nameof(NitaqatWeightRule.SourceNote)),
            MaxLengthOf<NitaqatActivity>(model, nameof(NitaqatActivity.SourceNote)),
            MaxLengthOf<NitaqatSizeTier>(model, nameof(NitaqatSizeTier.SourceNote)),
            MaxLengthOf<NitaqatBandThreshold>(model, nameof(NitaqatBandThreshold.SourceNote)),
        }.Min();

        var constants = typeof(NitaqatReferenceSeeder)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, Value: (string)f.GetRawConstantValue()!))
            .ToList();

        constants.Should().NotBeEmpty("the seeder's note constants are what this guard measures");

        var overlong = constants
            .Where(c => c.Value.Length > narrowest)
            .Select(c => $"{c.Name} is {c.Value.Length} chars ({c.Value.Length - narrowest} over)")
            .ToList();

        overlong.Should().BeEmpty(
            $"a note constant written to SourceNote must fit varchar({narrowest}) on every entity " +
            "that stores it");
    }
}
