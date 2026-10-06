using System.Text;
using System.Xml;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

// Generates the KSA INTERNAL PAYROLL REGISTER.
//
// WHAT THIS IS NOT. This used to be called the "Mudad WPS XML" file and was written with a
// <MudadWPS> root. No such format exists: Mudad accepts only the WPS file that the employer's BANK
// produces and digitally signs (SHA1withRSA) from the payroll instruction the employer sends it.
// See WpsConformance for the full finding. The XML is kept because customers use it to review a run
// and reconcile it, but it is labelled — in its root element, its file name, the API metadata and the
// download header — as an internal register that is not a bank file and not a WPS file.
//
// The Saudi bank file is the ANB Connect payroll-payment instruction
// (Infrastructure/Payroll/SaudiBankExports), validated by KsaWageFileRules before it is built.
public sealed class KsaWageProtectionExporter : IWageProtectionExporter
{
    public Task<WageProtectionExportResult> ExportAsync(
        WageProtectionExportInput input, CancellationToken ct = default)
    {
        var xml = BuildRegisterXml(input);
        var bytes = Encoding.UTF8.GetBytes(xml);
        var fileName = $"payroll-register_INTERNAL-not-a-bank-or-WPS-file_{input.PeriodYear}{input.PeriodMonth:D2}.xml";
        return Task.FromResult(new WageProtectionExportResult(
            bytes, fileName, WpsConformance.KsaPayrollRegisterFormat, input.Employees.Count));
    }

    private static string BuildRegisterXml(WageProtectionExportInput input)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false };

        using var writer = XmlWriter.Create(sb, settings);
        writer.WriteStartDocument();
        writer.WriteComment(" " + WpsConformance.KsaPayrollRegisterLabel + ". Do not upload to a bank, Mudad or MHRSD. ");
        writer.WriteStartElement("PayrollRegister");
        writer.WriteAttributeString("Version", "1.0");
        writer.WriteAttributeString("Kind", "internal-payroll-register");
        writer.WriteAttributeString("NotABankOrWpsFile", "true");

        writer.WriteStartElement("Header");
        writer.WriteElementString("MolEstablishmentId", input.EstablishmentId);
        writer.WriteElementString("EmployerName",   input.CompanyNameEn);
        writer.WriteElementString("Period",         $"{input.PeriodYear}-{input.PeriodMonth:D2}");
        writer.WriteElementString("RecordCount",    input.Employees.Count.ToString());
        writer.WriteElementString("TotalNetPay",    input.Employees.Sum(e => e.NetPay).ToString("F2"));
        writer.WriteEndElement(); // Header

        writer.WriteStartElement("Employees");
        foreach (var emp in input.Employees)
        {
            writer.WriteStartElement("Employee");
            writer.WriteElementString("EmpCode",      emp.EmployeeCode);
            writer.WriteElementString("FullNameEn",   emp.FullNameEn);
            writer.WriteElementString("GovernmentId", emp.NationalId);
            writer.WriteElementString("Nationality",  emp.Nationality);
            writer.WriteElementString("IBAN",         emp.IbanOrAccount);
            writer.WriteElementString("BankCode",     emp.BankCode);
            writer.WriteElementString("BasicSalary",  emp.Salary.Basic.ToString("F2"));
            writer.WriteElementString("Housing",      emp.Salary.HousingAllowance.ToString("F2"));
            writer.WriteElementString("Transport",    emp.Salary.TransportAllowance.ToString("F2"));
            writer.WriteElementString("OtherAllow",   emp.Salary.OtherAllowances.ToString("F2"));
            writer.WriteElementString("GrossSalary",  emp.Salary.Gross.ToString("F2"));
            writer.WriteElementString("NetPay",       emp.NetPay.ToString("F2"));
            writer.WriteEndElement(); // Employee
        }
        writer.WriteEndElement(); // Employees

        writer.WriteEndElement(); // PayrollRegister
        writer.WriteEndDocument();
        writer.Flush();
        return sb.ToString();
    }
}
