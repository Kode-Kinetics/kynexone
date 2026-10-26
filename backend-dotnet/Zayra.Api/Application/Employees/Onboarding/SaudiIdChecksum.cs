namespace Zayra.Api.Application.Employees.Onboarding;

/// <summary>
/// Check digit of a Saudi National ID or Iqama: exactly ten digits; odd positions (1-based) are doubled
/// (minus 9 when above 9), even positions are taken as they are; the total must be divisible by 10.
/// Leading-digit rules (1 = National ID, 2 = Iqama) belong to <see cref="IdentityNumberRules"/>.
/// </summary>
public static class SaudiIdChecksum
{
    public static bool IsValid(string? digits)
    {
        if (digits is null || digits.Length != 10) return false;
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var c = digits[i];
            if (c < '0' || c > '9') return false;
            var d = c - '0';
            if (i % 2 == 0) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }
}
