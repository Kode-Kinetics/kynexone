"""Configuration/privacy guards; the actual .NET build is verified by repository CI."""
import os
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "build-resource-snapshot.sh"

class CompileDiagnosticsTests(unittest.TestCase):
    def test_resource_snapshot_is_bounded_and_does_not_leak_environment(self):
        self.assertTrue(SCRIPT.is_file(), "CI resource diagnostics are missing")
        subprocess.run(["bash", "-n", str(SCRIPT)], check=True)
        sentinel = "NEVER_PRINT_PRIVATE_TOKEN_78541"
        result = subprocess.run(["bash", str(SCRIPT), "Test.Project", "before"],
                                text=True, capture_output=True, timeout=10,
                                env={**os.environ, "PRIVATE_TOKEN": sentinel})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("[ci-resource] phase=before project=Test.Project", result.stdout)
        self.assertNotIn(sentinel, result.stdout + result.stderr)
        self.assertLess(len(result.stdout), 12000)
        self.assertNotIn("cmdline", SCRIPT.read_text())
        self.assertNotIn("environ", SCRIPT.read_text())
        self.assertNotIn("printenv", SCRIPT.read_text())

if __name__ == "__main__":
    unittest.main()
