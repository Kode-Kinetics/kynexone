"""One-off release scope verifier behavior; future main changes are not frozen by this suite."""
import importlib.util
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[2]
SCRIPT=ROOT/'scripts/release/verify-backend-only-scope.py'

def verifier():
    spec=importlib.util.spec_from_file_location('release_scope',SCRIPT)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    return module

class BackendReleaseScopeTests(unittest.TestCase):
    def test_forbidden_client_and_schema_changes_are_rejected_even_if_allowlisted(self):
        scope=verifier()
        for path in ['frontend/src/api/payroll.ts','mobile/package.json','backend-dotnet/Zayra.Api/Migrations/Snapshot.cs','backend-dotnet/Zayra.Api/Models/CompanyPayrollRule.cs','backend-dotnet/Zayra.Api/Data/ZayraDbContext.cs','backend-dotnet/Zayra.Api/Controllers/PayrollController.cs','backend-dotnet/Zayra.Api/Application/Common/Csv.cs','backend-dotnet/Zayra.Api/Infrastructure/Http/SecurityHeaders.cs']:
            self.assertTrue(scope.path_errors([path],[path]),path)

    def test_allowlist_is_explicit_and_does_not_admit_unrelated_changes(self):
        scope=verifier();allowed=['backend-dotnet/Zayra.Api/Infrastructure/Qiwa/LiveQiwaApiAdapter.cs']
        self.assertEqual(scope.path_errors(allowed,allowed),[])
        self.assertTrue(scope.path_errors(['backend-dotnet/Zayra.Api/Controllers/PayApprovalsController.cs'],allowed))

    def test_critical_business_method_body_changes_are_detected_without_blocking_other_members(self):
        scope=verifier()
        before='class API {\n    public async Task<IActionResult> Process(Guid id)\n    {\n        return Ok(run);\n    }\n    public IActionResult Export() => Content(csv);\n}\n'
        export=before.replace('Content(csv)','CsvDownload.Result(csv)')
        self.assertEqual(scope.method_errors(before,export,['Process']),[])
        self.assertTrue(scope.method_errors(before,before.replace('Ok(run)','Accepted(job)'),['Process']))
