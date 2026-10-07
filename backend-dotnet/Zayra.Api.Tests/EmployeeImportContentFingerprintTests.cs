using Xunit;
using Zayra.Api.Controllers;

namespace Zayra.Api.Tests;

/// <summary>
/// The re-import guard compares content fingerprints. A spreadsheet re-save of the same rows (CRLF, a BOM, a trailing
/// newline, header case) must fingerprint the same, or every code-less row is imported a second time; a real change
/// to any cell must not.
/// </summary>
public class EmployeeImportContentFingerprintTests
{
    private const string Original = "EnglishName,Nationality,BasicSalary\nAhmed Ali,Saudi,8000\nSara Khan,Indian,6500\n";

    [Theory]
    [InlineData("EnglishName,Nationality,BasicSalary\r\nAhmed Ali,Saudi,8000\r\nSara Khan,Indian,6500\r\n")]
    [InlineData("﻿EnglishName,Nationality,BasicSalary\nAhmed Ali,Saudi,8000\nSara Khan,Indian,6500\n")]
    [InlineData("EnglishName,Nationality,BasicSalary\nAhmed Ali,Saudi,8000\nSara Khan,Indian,6500\n\n\n")]
    [InlineData("englishname,NATIONALITY, BasicSalary \nAhmed Ali,Saudi,8000\nSara Khan,Indian,6500")]
    [InlineData("EnglishName,Nationality,BasicSalary\n\"Ahmed Ali\",Saudi,8000\nSara Khan, Indian ,6500\n,,\n")]
    public void ARe_SaveOfTheSameRows_HasTheSameFingerprint(string resaved) =>
        Assert.Equal(EmployeesController.ImportContentSha256(Original), EmployeesController.ImportContentSha256(resaved));

    [Theory]
    [InlineData("EnglishName,Nationality,BasicSalary\nAhmed Ali,Saudi,8001\nSara Khan,Indian,6500\n")]
    [InlineData("EnglishName,Nationality,BasicSalary\nAhmed Ali,Saudi,8000\n")]
    [InlineData("EnglishName,Nationality,BasicSalary\nSara Khan,Indian,6500\nAhmed Ali,Saudi,8000\n")]
    public void AChangedFile_HasADifferentFingerprint(string changed) =>
        Assert.NotEqual(EmployeesController.ImportContentSha256(Original), EmployeesController.ImportContentSha256(changed));

    [Fact]
    public void AFileThatDoesNotParse_StillGetsAStableFingerprint() =>
        Assert.Equal(
            EmployeesController.ImportContentSha256("A,B\n1,2,3\n"),
            EmployeesController.ImportContentSha256("A,B\r\n1,2,3\r\n\r\n"));
}
