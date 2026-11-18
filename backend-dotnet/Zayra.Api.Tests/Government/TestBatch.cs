using System.Security.Cryptography;
using System.Text;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// A payroll submission names its payment batch by id (a Guid), and that id is the batch lock. Tests that used a label such as
/// "B-001" for a batch use the label's id: the same label is always the same batch, a different label a different one.
/// </summary>
internal static class TestBatch
{
    public static string Id(string label) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes("test-payment-batch|" + label))).ToString("D");
}
