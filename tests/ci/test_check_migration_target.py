"""The migration-target gate retries a transient Render API failure but stays fail-closed."""
import importlib.util
import io
from pathlib import Path
import unittest
import urllib.error

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("check_migration_target", ROOT / "scripts/check_migration_target.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def http(code):
    return urllib.error.HTTPError("https://x", code, "m", {}, None)


class RenderRetryTests(unittest.TestCase):
    def fetch(self, script):
        calls, naps = [], []

        def opener(req, timeout):
            calls.append(timeout)
            outcome = script[min(len(calls), len(script)) - 1]
            if isinstance(outcome, Exception):
                raise outcome
            return io.BytesIO(outcome)

        try:
            result = gate.fetch_render_env_vars("srv-x", "key", opener, naps.append)
        except SystemExit as exit_:
            result = ("exit", exit_.code)
        return result, calls, naps

    def test_transient_failures_are_retried_then_succeed(self):
        for script, attempts in [([TimeoutError(), b"[]"], 2), ([http(503), http(429), b"[]"], 3),
                                 ([urllib.error.URLError("reset"), b"[]"], 2), ([b"{truncated", b"[]"], 2)]:
            with self.subTest(script=script):
                result, calls, _ = self.fetch(script)
                self.assertEqual(result, [])
                self.assertEqual(len(calls), attempts)

    def test_persistent_failure_fails_closed_after_exactly_three_attempts(self):
        for failure in [TimeoutError(), http(502), urllib.error.URLError("down")]:
            with self.subTest(failure=failure):
                result, calls, _ = self.fetch([failure])
                self.assertEqual(result, ("exit", 1))
                self.assertEqual(len(calls), 3)

    def test_definite_http_answers_are_not_retried(self):
        for code in (400, 401, 403, 404):
            with self.subTest(code=code):
                result, calls, naps = self.fetch([http(code), b"[]"])
                self.assertEqual(result, ("exit", 1))
                self.assertEqual((len(calls), naps), (1, []))

    def test_total_wait_is_bounded_under_two_minutes(self):
        worst = gate.RENDER_ATTEMPTS * gate.RENDER_TIMEOUT_SECONDS + sum(gate.RENDER_BACKOFF_SECONDS)
        self.assertLess(worst, 120)
        self.assertEqual(len(gate.RENDER_BACKOFF_SECONDS), gate.RENDER_ATTEMPTS - 1)


if __name__ == "__main__":
    unittest.main()
