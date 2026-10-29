#!/usr/bin/env python3
"""Same-checkout, same-run, same-attempt Release outputs. Never fall back to stale assemblies."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import subprocess
import sys
import tarfile

PROJECTS = ('Zayra.Api', 'Zayra.Api.Tests')
REQUIRED = (
    'backend-dotnet/Zayra.Api/bin/Release/net8.0/Zayra.Api.dll',
    'backend-dotnet/Zayra.Api/bin/Release/net8.0/Zayra.Api.pdb',
    'backend-dotnet/Zayra.Api/obj/project.assets.json',
    'backend-dotnet/Zayra.Api.Tests/obj/project.assets.json',
    'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/Zayra.Api.Tests.dll',
    'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/Zayra.Api.Tests.pdb',
    'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/Zayra.Api.Tests.deps.json',
    'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/Zayra.Api.Tests.runtimeconfig.json',
    'backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/testhost.dll',
)


def digest(stream):
    result = hashlib.sha256()
    for chunk in iter(lambda: stream.read(1024 * 1024), b''):
        result.update(chunk)
    return result.hexdigest()


def allowed(name):
    parts = PurePosixPath(name).parts
    return (len(parts) >= 4 and name == str(PurePosixPath(name)) and
            '..' not in parts and parts[0] == 'backend-dotnet' and parts[1] in PROJECTS and
            ((parts[2] == 'bin' and parts[3] == 'Release' and len(parts) > 4) or
             (parts[2] == 'obj' and (len(parts) == 4 or parts[3] == 'Release'))))


def identity(root):
    sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    if not re.fullmatch(r'[0-9a-f]{40}', sha) or sha != os.environ.get('GITHUB_SHA'):
        raise ValueError('checked-out commit must match GITHUB_SHA (including the PR merge commit)')
    values = {key: os.environ.get(key, '') for key in ('GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT')}
    if any(not re.fullmatch(r'[1-9][0-9]*', value) for value in values.values()):
        raise ValueError('workflow run and attempt must be positive integers')
    return {'version': 1, 'sha': sha, 'run_id': values['GITHUB_RUN_ID'],
            'run_attempt': values['GITHUB_RUN_ATTEMPT'], 'configuration': 'Release',
            'framework': 'net8.0', 'platform': sys.platform, 'architecture': platform.machine()}


def require_outputs(names):
    if not set(REQUIRED).issubset(names):
        raise ValueError('required Release runtime, PDB or restore assets are absent')
    if not any(name.startswith('backend-dotnet/Zayra.Api.Tests/bin/Release/net8.0/Fixtures/') for name in names):
        raise ValueError('test fixture outputs are absent')


def pack(root, directory, provenance):
    files = {}
    for project in PROJECTS:
        for folder in ('bin', 'obj'):
            for path in sorted((root / 'backend-dotnet' / project / folder).rglob('*')):
                name = path.relative_to(root).as_posix()
                if path.is_file() and allowed(name):
                    if path.is_symlink():
                        raise ValueError('build outputs must be regular files')
                    with path.open('rb') as stream:
                        files[name] = digest(stream)
    require_outputs(files)
    directory.mkdir(parents=True, exist_ok=False)
    payload = directory / 'outputs.tar.gz'
    with tarfile.open(payload, 'w:gz') as archive:
        for name in files:
            archive.add(root / name, arcname=name, recursive=False)
    with payload.open('rb') as stream:
        archive_hash = digest(stream)
    manifest = {**provenance, 'archive_sha256': archive_hash, 'files': files}
    (directory / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print('Packed %d verified Release output files' % len(files))


def restore(root, directory, provenance):
    manifest = json.loads((directory / 'manifest.json').read_text())
    if any(manifest.get(key) != value for key, value in provenance.items()):
        raise ValueError('artifact provenance mismatch; rerun all jobs to produce this run attempt')
    files = manifest.get('files')
    if not isinstance(files, dict) or not files or any(not allowed(name) for name in files):
        raise ValueError('invalid artifact file manifest')
    require_outputs(files)
    payload = directory / 'outputs.tar.gz'
    with payload.open('rb') as stream:
        if digest(stream) != manifest.get('archive_sha256'):
            raise ValueError('artifact archive SHA-256 mismatch')
    with tarfile.open(payload, 'r:gz') as archive:
        members = archive.getmembers()
        if len(members) != len(files) or {member.name for member in members} != files.keys():
            raise ValueError('archive entries disagree with manifest')
        # Validate every byte and destination before writing ANY restored output.
        for member in members:
            if not member.isfile() or not allowed(member.name):
                raise ValueError('artifact contains an unsafe archive entry')
            target = root / member.name
            if not target.resolve().is_relative_to(root):
                raise ValueError('artifact destination escapes checkout')
            with archive.extractfile(member) as stream:
                if digest(stream) != files[member.name]:
                    raise ValueError('artifact file SHA-256 mismatch')
        for member in members:
            target = root / member.name
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(member) as source, target.open('wb') as destination:
                shutil.copyfileobj(source, destination)
            target.chmod(member.mode & 0o777)
    print('Restored %d hash-verified Release output files' % len(files))


if __name__ == '__main__':
    try:
        if len(sys.argv) != 3 or sys.argv[1] not in ('pack', 'restore'):
            raise ValueError('usage: backend-build-artifact.py <pack|restore> <artifact-directory>')
        root = Path.cwd().resolve()
        operation = pack if sys.argv[1] == 'pack' else restore
        operation(root, Path(sys.argv[2]).resolve(), identity(root))
    except (ValueError, OSError, tarfile.TarError, subprocess.CalledProcessError) as error:
        print('::error::' + str(error), file=sys.stderr)
        sys.exit(1)
