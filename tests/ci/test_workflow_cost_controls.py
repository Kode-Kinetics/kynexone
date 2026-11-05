"""Regression guards for the incident that exhausted the paid Actions budget."""
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
CI = (ROOT / ".github" / "workflows" / "ci.yml").read_text()
CODEQL = (ROOT / ".github" / "workflows" / "codeql.yml").read_text()


class WorkflowCostControlTests(unittest.TestCase):
    @staticmethod
    def job_block(workflow, job):
        match = re.search(
            rf"(?ms)^  {re.escape(job)}:\n.*?(?=^  [A-Za-z0-9_-]+:\n|\Z)", workflow
        )
        if match is None:
            raise AssertionError(f"workflow job is missing: {job}")
        return match.group(0)

    def test_paid_larger_runner_is_not_requested_by_any_workflow(self):
        workflow_text = "\n".join(
            path.read_text() for path in (ROOT / ".github" / "workflows").glob("*.yml")
        )
        self.assertNotIn("kynex-32gb", workflow_text)

    def test_heavy_ci_jobs_use_standard_runner_and_exact_build_artifact(self):
        for job in ("backend-build", "chrome-security-gate", "schema-gates", "browser-pilot", "migrate-backend"):
            with self.subTest(job=job):
                block = self.job_block(CI, job)
                self.assertIn("runs-on: ubuntu-24.04", block)

        for job in ("chrome-security-gate", "schema-gates", "migrate-backend"):
            with self.subTest(job=job):
                block = self.job_block(CI, job)
                self.assertIn("backend-build-artifact.py restore", block)

        build = self.job_block(CI, "backend-build")
        self.assertIn("retention-days: 7", build)

    def test_codeql_cancels_superseded_prs_on_standard_runner(self):
        self.assertIn("runs-on: ubuntu-24.04", CODEQL)
        self.assertIn("cancel-in-progress: ${{ github.event_name == 'pull_request' }}", CODEQL)
        self.assertIn("-maxcpucount:1", CODEQL)


if __name__ == "__main__":
    unittest.main()
