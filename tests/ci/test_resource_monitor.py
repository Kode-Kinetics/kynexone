import os
from pathlib import Path
import subprocess
import unittest

ROOT=Path(__file__).resolve().parents[2]
class ResourceMonitorTests(unittest.TestCase):
    def test_preserves_compiler_exit_code_and_does_not_print_arguments_or_environment(self):
        secret='DO_NOT_LOG_PRIVATE_ARG_57923'
        result=subprocess.run(['python3',str(ROOT/'scripts/ci/run-with-resource-snapshots.py'),
            'python3','-c','import sys; sys.exit(7)',secret],cwd=ROOT,
            env={**os.environ,'PRIVATE_TOKEN':secret},capture_output=True,text=True,timeout=10)
        self.assertEqual(result.returncode,7,result.stderr)
        self.assertIn('[ci-resource]',result.stdout)
        self.assertNotIn(secret,result.stdout+result.stderr)
        self.assertLess(len(result.stdout),25000)

    def test_interruption_cleans_up_compiler_descendants_that_ignore_term(self):
        import signal
        import tempfile
        import time
        with tempfile.TemporaryDirectory() as tmp:
            pid_file=Path(tmp)/'child.pid'
            child_code='import signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); time.sleep(60)'
            parent_code='import pathlib,subprocess,sys,time; child=subprocess.Popen([sys.executable,"-c",sys.argv[2]]); pathlib.Path(sys.argv[1]).write_text(str(child.pid)); time.sleep(60)'
            proc=subprocess.Popen(['python3',str(ROOT/'scripts/ci/run-with-resource-snapshots.py'),
                                   'python3','-c',parent_code,str(pid_file),child_code],
                                   cwd=ROOT,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            child_pid=None
            try:
                deadline=time.monotonic()+5
                while not pid_file.exists() and time.monotonic()<deadline:time.sleep(0.02)
                self.assertTrue(pid_file.exists(),'child did not start')
                child_pid=int(pid_file.read_text())
                time.sleep(0.1)
                proc.send_signal(signal.SIGTERM)
                self.assertEqual(proc.wait(timeout=5),143)
                time.sleep(0.05)
                state=subprocess.run(['ps','-p',str(child_pid),'-o','stat='],capture_output=True,text=True).stdout.strip()
                self.assertTrue(not state or state.startswith('Z'),'compiler descendant survived interruption')
            finally:
                if proc.poll() is None:proc.kill();proc.wait()
                if child_pid:
                    try:os.kill(child_pid,signal.SIGKILL)
                    except ProcessLookupError:pass

    def test_cancellation_fails_even_when_compiler_handles_signal_as_success(self):
        import signal
        import tempfile
        import time
        for interruption in [signal.SIGTERM,signal.SIGINT]:
            with self.subTest(signal=interruption), tempfile.TemporaryDirectory() as tmp:
                ready=Path(tmp)/'ready'
                code='import os,pathlib,signal,sys,time; signal.signal(signal.SIGTERM,lambda *_:sys.exit(0)); signal.signal(signal.SIGINT,lambda *_:sys.exit(0)); pathlib.Path(sys.argv[1]).write_text(str(os.getpid())); time.sleep(60)'
                proc=subprocess.Popen(['python3',str(ROOT/'scripts/ci/run-with-resource-snapshots.py'),
                                       'python3','-c',code,str(ready)],cwd=ROOT,
                                       stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
                try:
                    deadline=time.monotonic()+5
                    while not ready.exists() and time.monotonic()<deadline:time.sleep(0.02)
                    self.assertTrue(ready.exists())
                    proc.send_signal(interruption)
                    self.assertEqual(proc.wait(timeout=5),128+interruption)
                finally:
                    if proc.poll() is None:proc.kill();proc.wait()
                    if ready.exists():
                        try:os.kill(int(ready.read_text()),signal.SIGKILL)
                        except ProcessLookupError:pass
