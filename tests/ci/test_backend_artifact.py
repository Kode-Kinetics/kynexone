"""Immutable build handoff regression tests using actual archive files."""
import hashlib
import io
import tarfile
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[2]
SCRIPT=ROOT/'scripts/ci/backend-build-artifact.py'
REQUIRED=[
 'backend-dotnet/Zayra.Api/bin/Release/net8.0/Zayra.Api.dll',
 'backend-dotnet/Zayra.Api/bin/Release/net8.0/Zayra.Api.pdb',
 'backend-dotnet/Zayra.Api/obj/project.assets.json',
 'backend-dotnet/Zayra.Api.Tests/obj/project.assets.json',
 'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/Zayra.Api.Tests.dll',
 'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/Zayra.Api.Tests.pdb',
 'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/Zayra.Api.Tests.deps.json',
 'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/Zayra.Api.Tests.runtimeconfig.json',
 'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/testhost.dll',
 'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/Fixtures/example.csv']

class ArtifactTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.base=Path(self.tmp.name);self.repo=self.base/'repo';self.repo.mkdir()
        subprocess.run(['git','init','-q',str(self.repo)],check=True)
        for key,value in [('user.email','ci@example.invalid'),('user.name','CI')]:
            subprocess.run(['git','-C',str(self.repo),'config',key,value],check=True)
        (self.repo/'source').write_text('source')
        subprocess.run(['git','-C',str(self.repo),'add','.'],check=True)
        subprocess.run(['git','-C',str(self.repo),'commit','-qm','base'],check=True)
        self.sha=subprocess.check_output(['git','-C',str(self.repo),'rev-parse','HEAD'],text=True).strip()
        self.env={**os.environ,'GITHUB_SHA':self.sha,'GITHUB_RUN_ID':'123','GITHUB_RUN_ATTEMPT':'2'}
        for relative in REQUIRED:
            path=self.repo/relative;path.parent.mkdir(parents=True,exist_ok=True);path.write_text(relative)
        stale=self.repo/'backend-dotnet/Zayra.Api.Tests/TestResults/stale.trx';stale.parent.mkdir();stale.write_text('stale')
        (self.repo/REQUIRED[-1]).chmod(0o755)
        self.artifact=self.base/'artifact'

    def run_script(self,command,env=None):
        return subprocess.run(['python3',str(SCRIPT),command,str(self.artifact)],cwd=self.repo,
                              env=env or self.env,capture_output=True,text=True)

    def pack(self):
        result=self.run_script('pack');self.assertEqual(result.returncode,0,result.stderr)

    def test_round_trip_preserves_every_output_and_fixture_without_stale_results(self):
        self.pack()
        for relative in REQUIRED:(self.repo/relative).unlink()
        result=self.run_script('restore')
        self.assertEqual(result.returncode,0,result.stderr)
        for relative in REQUIRED:self.assertEqual((self.repo/relative).read_text(),relative)
        self.assertEqual((self.repo/REQUIRED[-1]).stat().st_mode & 0o777,0o755)
        manifest=json.loads((self.artifact/'manifest.json').read_text())
        self.assertEqual(manifest['sha'],self.sha)
        self.assertFalse(any('TestResults' in name for name in manifest['files']))

    def test_missing_artifact_and_missing_required_output_fail(self):
        self.assertNotEqual(self.run_script('restore').returncode,0)
        (self.repo/REQUIRED[0]).unlink()
        self.assertNotEqual(self.run_script('pack').returncode,0)

    def test_corrupt_payload_or_manifest_hash_fails_before_extraction(self):
        self.pack()
        payload=self.artifact/'outputs.tar.gz'
        payload.write_bytes(payload.read_bytes()+b'corrupt')
        self.assertNotEqual(self.run_script('restore').returncode,0)

    def test_wrong_sha_run_or_attempt_fails(self):
        self.pack()
        for key,value in [('GITHUB_SHA','f'*40),('GITHUB_RUN_ID','456'),('GITHUB_RUN_ATTEMPT','3')]:
            with self.subTest(key=key):
                self.assertNotEqual(self.run_script('restore',{**self.env,key:value}).returncode,0)

    def test_checked_out_commit_must_match_expected_sha(self):
        self.assertNotEqual(self.run_script('pack',{**self.env,'GITHUB_SHA':'f'*40}).returncode,0)

    def test_file_hash_manifest_cannot_be_corrupt_or_omit_files(self):
        self.pack()
        path=self.artifact/'manifest.json';original=path.read_text()
        for mode in ['corrupt','omit']:
            manifest=json.loads(original)
            if mode=='corrupt':manifest['files'][REQUIRED[0]]='0'*64
            else:manifest['files'].pop(REQUIRED[0])
            path.write_text(json.dumps(manifest))
            self.assertNotEqual(self.run_script('restore').returncode,0)

    def test_unsafe_or_duplicate_archive_entries_fail_before_writing(self):
        self.pack()
        payload=self.artifact/'outputs.tar.gz'
        original=payload.read_bytes()
        manifest_path=self.artifact/'manifest.json'
        original_manifest=manifest_path.read_text()
        for mode in ['symlink','traversal','duplicate']:
            with self.subTest(mode=mode):
                manifest=json.loads(original_manifest)
                with tarfile.open(fileobj=io.BytesIO(original),mode='r:gz') as source:
                    entries=[(member,source.extractfile(member).read()) for member in source.getmembers()]
                with tarfile.open(payload,'w:gz') as archive:
                    for index,(member,data) in enumerate(entries):
                        if index==0 and mode=='symlink':
                            member.type=tarfile.SYMTYPE;member.linkname='/tmp/escape';member.size=0
                            archive.addfile(member)
                        elif index==0 and mode=='traversal':
                            del manifest['files'][member.name]
                            member.name='../../escape';manifest['files'][member.name]=hashlib.sha256(data).hexdigest()
                            archive.addfile(member,io.BytesIO(data))
                        else:archive.addfile(member,io.BytesIO(data))
                    if mode=='duplicate':
                        archive.addfile(entries[-1][0],io.BytesIO(entries[-1][1]))
                manifest['archive_sha256']=hashlib.sha256(payload.read_bytes()).hexdigest()
                manifest_path.write_text(json.dumps(manifest))
                (self.repo/REQUIRED[-1]).unlink(missing_ok=True)
                result=self.run_script('restore')
                self.assertNotEqual(result.returncode,0,result.stdout)
                self.assertFalse((self.repo/REQUIRED[-1]).exists())
