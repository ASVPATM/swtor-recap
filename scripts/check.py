#!/usr/bin/env python3
"""Local validation; all replay state goes to an explicit isolated artifact directory."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import platform
import xml.etree.ElementTree as ET
import time

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--quick',action='store_true')
args=parser.parse_args()
os.chdir(ROOT)
for key,value in {'DOTNET_CLI_TELEMETRY_OPTOUT':'1','DOTNET_GENERATE_ASPNET_CERTIFICATE':'false','DOTNET_CLI_HOME':str(ROOT/'.artifacts/dotnet-home'),'NUGET_PACKAGES':str(ROOT/'.artifacts/nuget')}.items():os.environ.setdefault(key,value)
artifact=ROOT/'.artifacts'/f'check-{time.time_ns()}'
artifact.mkdir(parents=True)
dotnet=['dotnet'] if os.name=='nt' else ['bash',str(ROOT/'scripts/dotnet.sh')]
def run(*arguments):
    subprocess.run(dotnet+list(arguments),check=True)

run('restore','SwtorRecap.slnx','--locked-mode','--disable-parallel')
run('build','SwtorRecap.slnx','-c','Release','--no-restore','--disable-build-servers','-m:1','-p:UseSharedCompilation=false')
for suite in ['Core','Integration']+([] if args.quick else ['Desktop','Windows']):
    run('test',f'tests/SwtorRecap.{suite}.Tests','-c','Release','--no-build','--no-restore','--logger',f'trx;LogFileName={suite}.trx','--results-directory',str(artifact/'tests'))
tool=str(ROOT/'src/SwtorRecap.Tools/bin/Release/net10.0/SwtorRecap.Tools.dll')
if not args.quick:
    with (artifact/'replay-output.json').open('w') as output:
        subprocess.run(dotnet+[tool,'replay','--manifest','tests/Fixtures/manifest.json','--data-dir',str(artifact/'replay'),'--seed','88319','--verify'],stdout=output,check=True)
    with (artifact/'catalog-output.json').open('w') as output:
        subprocess.run(dotnet+[tool,'catalog','validate','--output','data/catalog/coverage.json'],stdout=output,check=True)
    report=json.loads((artifact/'replay'/'replay-result.json').read_text())
    print(json.dumps({'ok':report['ok'],'fixtures':len(report['results']),'sqlite':report['sqliteVersion'],'replaySeconds':report['elapsedSeconds'],'artifacts':str(artifact)},indent=2))
agent_deps=json.loads((ROOT/'src/SwtorRecap.Agent/bin/Release/net10.0/SwtorRecap.Agent.deps.json').read_text())
assert not any(any(ui in name.lower() for ui in ['avalonia','skia','webview']) for name in agent_deps['libraries']), 'Agent contains a rendering dependency'
suites={}
for path in sorted((artifact/'tests').glob('*.trx')):
    tree=ET.parse(path)
    counts=tree.find('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters')
    suites[path.stem]={key:int(counts.attrib[key]) for key in ['total','executed','passed','failed','notExecuted']}
    suites[path.stem]['skipped']=sum(r.get('outcome')=='NotExecuted' for r in tree.findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult'))
validation={'ok':True,'platform':platform.platform(),'quick':args.quick,'tests':suites}
if not args.quick:validation['replay']={'ok':report['ok'],'fixtures':len(report['results']),'sqliteVersion':report['sqliteVersion'],'elapsedSeconds':report['elapsedSeconds'],'recapP95Milliseconds':report['recapP95Milliseconds']}
(artifact/'validation.json').write_text(json.dumps(validation,indent=2)+'\n')
(ROOT/'.artifacts/latest-validation.json').write_text(json.dumps(validation,indent=2)+'\n')
print('Validation completed. Windows-only skips are reported by the test runner, never counted as passes.')
