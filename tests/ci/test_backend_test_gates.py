"""Executable regressions for populated backend CI lanes and their required gate."""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / 'scripts/ci'
WORKFLOW = ROOT / '.github/workflows/ci.yml'


def workflow_run(job, step):
    # Read the literal shell block without an optional YAML dependency on hosted runners.
    section = WORKFLOW.read_text().split('\n  '+job+':\n', 1)[1]
    section = re.split(r'\n  [A-Za-z][A-Za-z0-9_-]*:', section, maxsplit=1)[0]
    block = section.split('      - '+step+'\n', 1)[1]
    block = block.split('      - ', 1)[0]
    script = block.split('        run: |\n', 1)[1]
    lines = []
    for line in script.splitlines():
        if line.strip() and not line.startswith('          '): break
        lines.append(line)
    return textwrap.dedent('\n'.join(lines))

class ShardTests(unittest.TestCase):
    def run_shard(self, listing, shard='1', total='4', code=0):
        with tempfile.TemporaryDirectory() as tmp:
            shim = Path(tmp) / 'dotnet'
            shim.write_text('#!/usr/bin/env python3\nimport os,sys\nargs=sys.argv[1:]\nassert args[0] == "test" and "--list-tests" in args and "--no-build" in args\nassert args[args.index("--filter")+1] == "Category=Integration"\nprint(os.environ["TEST_LISTING"])\nsys.exit(int(os.environ["TEST_EXIT"]))\n')
            shim.chmod(0o755)
            return subprocess.run(['bash', str(CI/'shard-tests.sh'), 'tests.csproj', shard, total],
                env={**os.environ, 'PATH': tmp+os.pathsep+os.environ['PATH'],
                     'TEST_LISTING': listing, 'TEST_EXIT': str(code)},capture_output=True,text=True)

    def test_rejects_empty_unrecognized_and_malformed_discovery(self):
        for listing in ['', 'No test is available', '    Warning: adapter unavailable',
                        'The following Tests are available:\n    not-a-test',
                        'The following Tests are available:\n    Demo.Valid.Test\n    bad entry']:
            with self.subTest(listing=listing):
                result = self.run_shard(listing)
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertEqual(result.stdout, '')

    def test_validates_positive_integer_and_range_arguments(self):
        listing = 'The following Tests are available:\n    Demo.Valid.Test'
        for shard,total in [('0','4'),('5','4'),('-1','4'),('a','4'),('1','0'),('1','2.0'),('01','4')]:
            with self.subTest(shard=shard,total=total):
                self.assertNotEqual(self.run_shard(listing,shard,total).returncode,0)

    def test_failed_discovery_cannot_emit_filter(self):
        result=self.run_shard('The following Tests are available:\n    Demo.Valid.Test', code=1)
        self.assertNotEqual(result.returncode,0)
        self.assertEqual(result.stdout,'')

    def test_populated_shards_are_deterministic_complete_and_disjoint(self):
        names=['Demo.A.Test(x: 1)','Demo.A.Test(x: 2)','Demo.B.Test','Demo.C.Test','Demo.D.Test','Demo.E.Test']
        outputs=[]
        for shard in range(1,5):
            first=self.run_shard('The following Tests are available:\n'+''.join('    '+n+'\n' for n in names),str(shard))
            second=self.run_shard('The following Tests are available:\n'+''.join('    '+n+'\n' for n in reversed(names)),str(shard))
            self.assertEqual(first.returncode,0,first.stderr)
            self.assertEqual(first.stdout,second.stdout)
            classes=re.findall(r'FullyQualifiedName~(Demo\.[A-E])\.',first.stdout)
            self.assertTrue(classes,first.stdout)
            outputs.extend(classes)
        self.assertEqual(sorted(outputs), ['Demo.A','Demo.B','Demo.C','Demo.D','Demo.E'])

    def test_rejects_class_substrings_that_would_execute_in_multiple_shards(self):
        listing='The following Tests are available:\n    Demo.A.One\n    Prefix.Demo.A.Two\n    Demo.B.Three\n    Demo.C.Four'
        for value in [listing,listing.replace('Prefix.Demo.A.Two','Prefix.demo.a.Two')]:
            result=self.run_shard(value)
            self.assertNotEqual(result.returncode,0,result.stdout)

    def test_rejects_empty_requested_shard(self):
        for shard in range(1,5):
            self.assertNotEqual(self.run_shard('The following Tests are available:\n    Demo.A.Test',str(shard)).returncode,0)

class DocsClassificationTests(unittest.TestCase):
    def classify(self, paths):
        # Exercise the exact workflow diff step against a disposable real Git history.
        script=workflow_run('changes','id: diff')
        with tempfile.TemporaryDirectory() as tmp:
            repo=Path(tmp)
            def git(*args):
                return subprocess.check_output(['git','-C',tmp,*args],text=True).strip()
            git('init','-q'); git('config','user.email','ci@example.invalid'); git('config','user.name','CI')
            (repo/'initial').write_text('initial'); git('add','.');git('commit','-qm','base')
            base=git('rev-parse','HEAD')
            for path in paths:
                file=repo/path;file.parent.mkdir(parents=True,exist_ok=True);file.write_text('changed')
            git('add','.');git('commit','-qm','change')
            output=repo/'output'
            result=subprocess.run(['bash','-c',script],cwd=repo,capture_output=True,text=True,
                env={**os.environ,'EVENT':'pull_request','BASE_SHA':base,'HEAD_SHA':git('rev-parse','HEAD'),'GITHUB_OUTPUT':str(output)})
            self.assertEqual(result.returncode,0,result.stderr)
            return output.read_text().strip()

    def test_consumed_docs_and_executable_inputs_run_required_tests(self):
        for path in ['docs/schema/DATA_DICTIONARY.md','docs/schema/M3_COST_ACCOUNTING_VALIDATION.md',
                     'docs/DEPLOY_ROLLBACK_RUNBOOK.md','docs/runbooks/org-per-company-precheck.sql',
                     'backend-dotnet/Zayra.Api.Tests/Fixtures/readme.md','artifacts/check.sql','LICENSE-check.sh']:
            with self.subTest(path=path):
                self.assertEqual(self.classify([path]),'docs_only=false')

    def test_known_top_level_prose_can_skip(self):
        self.assertEqual(self.classify(['README.md','LICENSE']),'docs_only=true')

class TrxGateTests(unittest.TestCase):
    def validate(self, contents):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'tests.trx'
            if contents is not None:path.write_text(contents)
            return subprocess.run(['python3',str(CI/'verify-test-results.py'),str(path)],capture_output=True,text=True)

    @staticmethod
    def trx(executed='1', passed='1', failed='0', outcome='Passed'):
        return f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="Completed"><Counters total="1" executed="{executed}" passed="{passed}" failed="{failed}" /></ResultSummary><Results><UnitTestResult outcome="{outcome}"/></Results></TestRun>'

    def test_requires_existing_well_formed_nonzero_executed_results(self):
        for content in [None,'broken','<TestRun/>',self.trx('0','0'),self.trx('1','0','1','Failed'),self.trx('-1'),self.trx('garbage'),self.trx('2'),self.trx().replace('failed="0"','failed="0" notExecuted="1"')]:
            with self.subTest(content=content):self.assertNotEqual(self.validate(content).returncode,0)

    def test_accepts_real_passing_results(self):
        result=self.validate(self.trx())
        self.assertEqual(result.returncode,0,result.stderr)

    def test_passing_executed_tests_can_coexist_with_explicit_skips(self):
        trx=self.trx().replace('total="1"','total="2"').replace('failed="0"','failed="0" notExecuted="1"')
        trx=trx.replace('</Results>','<UnitTestResult outcome="NotExecuted"/></Results>')
        result=self.validate(trx)
        self.assertEqual(result.returncode,0,result.stderr)

    def test_vstest_17_14_1_mixed_pass_skip_uses_zero_placeholder_skip_counter(self):
        # Pinned logger: TestRunSummary.cs initializes _notExecuted to zero, while
        # TrxLogger.cs counts executed as passed+failed and emits skipped result entries.
        trx=self.trx().replace('total="1"','total="2"').replace('failed="0"','failed="0" notExecuted="0"')
        trx=trx.replace('</Results>','<UnitTestResult outcome="NotExecuted"/></Results>')
        result=self.validate(trx)
        self.assertEqual(result.returncode,0,result.stderr)

class AggregateGateTests(unittest.TestCase):
    def test_changes_and_lane_results_fail_closed(self):
        script=workflow_run('backend-tests','name: Require every backend lane')
        for changes,docs,unit,integration,expected in [
            ('success','false','success','success',0),('success','true','skipped','skipped',0),
            ('failure','true','skipped','skipped',1),('cancelled','false','success','success',1),
            ('success','','success','success',1),('success','true','success','skipped',1),
            ('success','false','success','skipped',1),('success','false','cancelled','success',1)]:
            with self.subTest(changes=changes,docs=docs,unit=unit,integration=integration):
                result=subprocess.run(['bash','-c',script],cwd=ROOT,capture_output=True,text=True,
                    env={**os.environ,'BUILD':'skipped' if docs=='true' else 'success','CHANGES':changes,'DOCS_ONLY':docs,'UNIT':unit,'INTEGRATION':integration})
                self.assertEqual(result.returncode,expected,result.stdout+result.stderr)

class AggregateBuildFailureTests(unittest.TestCase):
    def test_failed_or_cancelled_build_cannot_pass_successful_test_statuses(self):
        script=workflow_run('backend-tests','name: Require every backend lane')
        for build in ['failure','cancelled','skipped','']:
            result=subprocess.run(['bash','-c',script],capture_output=True,text=True,
                env={**os.environ,'BUILD':build,'CHANGES':'success','DOCS_ONLY':'false','UNIT':'success','INTEGRATION':'success'})
            self.assertNotEqual(result.returncode,0)

class AggregateEvidenceTests(unittest.TestCase):
    def run_evidence(self, missing=None, zero=None):
        with tempfile.TemporaryDirectory() as tmp:
            for lane in ['unit','shard-1','shard-2','shard-3','shard-4']:
                if lane==missing:continue
                folder=Path(tmp)/('backend-results-'+lane+'-'+'a'*40+'-123-2')
                folder.mkdir()
                (folder/(lane+'.trx')).write_text(TrxGateTests.trx('0','0') if lane==zero else TrxGateTests.trx())
            return subprocess.run(['python3',str(CI/'verify-backend-results.py'),tmp],capture_output=True,text=True,
                env={**os.environ,'GITHUB_SHA':'a'*40,'GITHUB_RUN_ID':'123','GITHUB_RUN_ATTEMPT':'2'})

    def test_requires_unit_and_all_four_nonzero_shards(self):
        for lane in ['unit','shard-1','shard-2','shard-3','shard-4']:
            with self.subTest(missing=lane):self.assertNotEqual(self.run_evidence(missing=lane).returncode,0)
            with self.subTest(zero=lane):self.assertNotEqual(self.run_evidence(zero=lane).returncode,0)
        result=self.run_evidence()
        self.assertEqual(result.returncode,0,result.stderr)
