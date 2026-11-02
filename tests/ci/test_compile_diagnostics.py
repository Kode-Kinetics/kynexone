"""Configuration/privacy guards; the actual .NET build is verified by repository CI."""
import os
from pathlib import Path
import subprocess
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
TARGETS = ROOT / "backend-dotnet" / "Directory.Build.targets"
SCRIPT = ROOT / "scripts" / "ci" / "build-resource-snapshot.sh"

class CompileDiagnosticsTests(unittest.TestCase):
    def test_ci_settings_exist_and_change_only_compiler_lifetime(self):
        self.assertTrue(TARGETS.is_file(), "CI compiler-lifetime policy is missing")
        tree = ET.parse(TARGETS).getroot()
        groups = tree.findall("PropertyGroup")
        self.assertEqual(len(groups), 1)
        self.assertEqual(groups[0].attrib.get("Condition"), "'$(GITHUB_ACTIONS)' == 'true'")
        self.assertEqual([(e.tag, e.text) for e in groups[0]], [("UseSharedCompilation", "false")])
        self.assertFalse(tree.findall(".//ItemGroup"), "Do not change compiled sources or analyzer items")
        self.assertFalse(tree.findall(".//OnError"), "Never suppress a failed build")
        hooks = tree.findall("Target")
        self.assertEqual(len(hooks), 2)
        self.assertEqual(hooks[0].attrib.get("BeforeTargets"), "CoreCompile")
        self.assertEqual(hooks[1].attrib.get("AfterTargets"), "CoreCompile")
        for hook in hooks:
            self.assertIn("'$(GITHUB_ACTIONS)' == 'true'", hook.attrib["Condition"])
            self.assertIn("'$(OS)' == 'Unix'", hook.attrib["Condition"])
            self.assertEqual([e.tag for e in hook], ["Exec"])
            task = hook.find("Exec")
            self.assertIn("build-resource-snapshot.sh", task.attrib["Command"])
            self.assertEqual(task.attrib.get("IgnoreExitCode"), "true")

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
