using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// Gives an onboarding draft the statutory identity its jurisdiction requires, so a test about the
/// RECRUITMENT lifecycle is not refused for a readiness reason it is not asserting.
///
/// <para><b>Why these tests needed it.</b> An employee now inherits the employing company's country
/// instead of being stored with a blank one, and a blank country no longer resolves an EMPTY
/// requirement list that the gate read as "nothing is required". So a draft that resolves to a Saudi
/// or UAE legal entity is held at activation until it holds that country's identity documents — which
/// is the point of the floor, and what these fixtures were silently escaping.</para>
///
/// <para><b>Why the hires are non-GCC expats.</b> <c>EmployeeDraft</c> carries an Iqama, an Emirates ID
/// and a work permit, but it has NO <c>IdNumber</c> column — the National ID (Hawiyya) that the KSA
/// floor requires of a SAUDI NATIONAL to activate. A Saudi-national hire therefore cannot satisfy the
/// floor through the draft path at all. That is a product gap, not something a test should paper over,
/// so these fixtures hire the expat the draft CAN describe and the gap is reported separately.</para>
/// </summary>
public static class DraftStatutoryIdentity
{
    /// <summary>Non-GCC expat identity for <paramref name="countryCode"/>, applied to the draft in place.</summary>
    public static async Task ApplyAsync(ZayraDbContext db, Guid draftId, string countryCode)
    {
        var draft = await db.EmployeeDrafts.SingleAsync(d => d.Id == draftId);
        draft.CountryCode = countryCode;
        draft.Nationality = "Indian";   // a non-GCC expat: the class the draft's fields can fully describe
        switch (countryCode.ToUpperInvariant())
        {
            case "SA":
                draft.IqamaNumber = "2000000001";       // residence permit — the KSA activate-gate item
                break;
            case "AE":
                draft.EmiratesId = "784-1990-3333333-3"; // identity card — all residents
                draft.WorkPermitNumber = "MOHRE-000001"; // MOHRE labour card — non-GCC expats
                break;
        }
        await db.SaveChangesAsync();
    }
}
