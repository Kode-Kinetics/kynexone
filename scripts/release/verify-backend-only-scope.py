#!/usr/bin/env python3
"""One-off, read-only scope check for the documented backend-only release candidate."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys

FORBIDDEN=('frontend/','mobile/','backend-dotnet/Zayra.Api/Migrations/','backend-dotnet/Zayra.Api/Models/','backend-dotnet/Zayra.Api/Domain/','backend-dotnet/Zayra.Api/Data/','backend-dotnet/Zayra.Api/Controllers/','backend-dotnet/Zayra.Api/Application/','backend-dotnet/Zayra.Api/Infrastructure/Http/','backend-dotnet/Zayra.Api.Tests/Security/','backend-dotnet/Zayra.Api.Tests/Architecture/')

def path_errors(changed, allowed):
    allowed=set(allowed)
    return ['forbidden or unplanned change: '+name for name in changed if name not in allowed or name.startswith(FORBIDDEN)]

def method(source, name):
    members=list(re.finditer(r'(?m)^    (?:public|private|internal|protected) [^\n]*',source))
    for index,member in enumerate(members):
        if re.search(r'\b'+re.escape(name)+r'\s*\(',member.group()):
            end=members[index+1].start() if index+1<len(members) else len(source)
            return source[member.start():end]
    raise ValueError('critical method not found: '+name)

def method_errors(before, after, names):
    return ['changed critical business method: '+name for name in names if method(before,name)!=method(after,name)]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ledger',default='docs/releases/backend-only-pr282-scope.json')
    args=parser.parse_args();root=Path(__file__).resolve().parents[2]
    ledger=json.loads((root/args.ledger).read_text());base=ledger['base_sha']
    def git(*args):return subprocess.check_output(['git',*args],cwd=root,text=True)
    source_base=ledger['source_base_sha']
    if base != source_base:
        advanced=git('diff','--name-only',source_base,base).splitlines()
        unexpected=set(advanced)-set(ledger['allowed_main_advance_paths'])
        if unexpected:raise ValueError('unreviewed main advancement: '+', '.join(sorted(unexpected)))
    changed=set(git('diff','--name-only',base).splitlines())
    changed.update(git('ls-files','--others','--exclude-standard').splitlines())
    errors=path_errors(changed,ledger['allowed_candidate_paths'])
    for path,names in ledger['critical_methods'].items():
        errors.extend(path+': '+message for message in method_errors(git('show',base+':'+path),(root/path).read_text(encoding='utf-8-sig'),names))
    if errors:raise ValueError('\n'.join(errors))
    print('Verified %d candidate paths; main client/model/migration boundaries preserved'%len(changed))

if __name__=='__main__':
    try:main()
    except (ValueError,OSError,subprocess.CalledProcessError) as error:
        print(str(error),file=sys.stderr);sys.exit(1)
