namespace Zayra.Api.Tests;

/// <summary>
/// Checksum-valid synthetic identity numbers for fixtures. National ID/Iqama validation is enforced on
/// every employee write, so a fixture cannot use a made-up ten-digit number. Same Luhn variant as the
/// production rule (odd positions doubled, minus 9 above 9, total divisible by 10).
/// </summary>
public static class TestIds
{
    public static string ValidNationalId(int n) => WithCheckDigit('1', n);
    public static string ValidIqama(int n) => WithCheckDigit('2', n);
    public static string ValidBorderNumber(int n) => WithCheckDigit('3', n);

    private static string WithCheckDigit(char lead, int n)
    {
        var body = lead + Math.Abs(n % 100_000_000).ToString("D8");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var d = body[i] - '0';
            if (i % 2 == 0) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return body + ((10 - sum % 10) % 10);
    }
}
