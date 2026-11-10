#!/usr/bin/env python3
"""Build the self-contained Windows ZIP and, on Windows, the per-user installer."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--rid', default='win-x64', choices=['win-x64'])
parser.add_argument('--installer', action='store_true', help='Compile Setup.exe using Inno Setup on Windows')
args = parser.parse_args()
if args.installer and os.name != 'nt':
    parser.error('--installer requires Windows and Inno Setup')
os.chdir(ROOT)
for key, value in {'DOTNET_CLI_TELEMETRY_OPTOUT':'1', 'DOTNET_GENERATE_ASPNET_CERTIFICATE':'false', 'DOTNET_CLI_HOME':str(ROOT/'.artifacts/dotnet-home'), 'NUGET_PACKAGES':str(ROOT/'.artifacts/nuget')}.items():
    os.environ.setdefault(key, value)
dotnet = ['dotnet'] if os.name == 'nt' else ['bash', str(ROOT/'scripts/dotnet.sh')]
version = ET.parse(ROOT/'Directory.Build.props').findtext('.//Version')
assert re.fullmatch(r'\d+\.\d+\.\d+', version)
out = ROOT/'artifacts'/'SwtorRecap-win-x64'
if out.exists():
    shutil.rmtree(out)  # This script's generated application directory only.
out.mkdir(parents=True)
products = [('Agent', out/'agent', 'SwtorRecap.Agent'), ('Desktop', out, 'SwtorRecap')]
for project, directory, assembly in products:
    subprocess.run(dotnet+['publish', f'src/SwtorRecap.{project}/SwtorRecap.{project}.csproj', '-c', 'Release', '-r', args.rid, '--self-contained', 'true', '--disable-build-servers', '-m:1', '-p:RestoreLockedMode=true', '-p:UseSharedCompilation=false', '-p:DebugType=None', '-p:DebugSymbols=false', f'-p:PathMap={ROOT}=/_/', '-p:PublishAot=false', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false', '-p:PublishSingleFile=false', '-o', str(directory)], check=True)
    assert (directory/f'{assembly}.exe').is_file()
    assert (directory/'e_sqlite3.dll').is_file()
deps = json.loads((out/'agent/SwtorRecap.Agent.deps.json').read_text())
assert not any(any(ui in name.lower() for ui in ['avalonia','skia','webview']) for name in deps['libraries'])
assert (out/'libSkiaSharp.dll').is_file()
(out/'README.txt').write_text(f'''SWTOR Recap {version} - Windows x64

PORTABLE SETUP
1. Extract the entire ZIP. Keep all files and the agent folder together.
2. Open SwtorRecap.exe. No command file, administrator access, or separate
   .NET installation is needed. A short loading window opens the dashboard.
3. In SWTOR: Preferences > Combat Logging > Enable combat logging to file.
   Launching the game through Steam needs no special settings.
4. Recap finds the CombatLogs folder under your Windows Documents folder.
   If necessary, select it in Recap Settings and save.

REPLACE AN OLDER VERSION
1. Close SWTOR. Export a history backup from Recap Settings.
2. Disable Start with Windows if enabled. Quit tracker from its tray menu,
   then close the dashboard.
3. Extract this ZIP into a new folder, or run the separate Setup.exe installer.
   Keep the old application folder until the new version works.
4. Open the new SwtorRecap.exe (or SWTOR Recap in the Start menu if installed).
   It uses the existing history in %LOCALAPPDATA%\\SwtorRecap.
5. With SWTOR closed and the original logs available, use Settings >
   Reprocess saved history > Reprocess saved logs. This adds NPC kills to old
   recordings and corrects the older full-run counting bug.
6. Re-enable Start with Windows if wanted. Do not mix old and new DLLs.

COUNTS
Damage and healing: your character's raw logged amounts. Effective amounts
appear in recording details. Companion and party output is excluded.
NPC kills: a recorded death after your character deals MORE than half of
that target's logged maximum health in effective damage. Exactly half does
not qualify. Someone else can land the final blow. Known bosses, players,
companions, training dummies, unknown health and unobserved deaths are excluded.
Boss wins: supported encounters with your participation and victory evidence.
Clears: a supported final-boss defeat. Quest turn-in is separate.
Full runs: all required supported encounters observed without a recording gap.

Closing the dashboard leaves the tray tracker running. After SWTOR closes,
Session returns to its ready screen and your recording stays in History.
For a complete Hammer Station run, expect 3 boss encounters, 1 clear and
1 full run. NPC kills depend on your contribution and recorded evidence.

FILES AND SECURITY
SWTOR writes combat_*.txt. Recap only reads those files. StarParse can read
alongside it. Recap's history and backups stay in %LOCALAPPDATA%\\SwtorRecap.
This test build is unsigned and can show a Windows publisher/reputation warning.
An EXE installer does not remove that warning. Do not disable Windows security.
If blocked, save the exact message and use the source's SHA256SUMS.txt to check
that the downloaded file matches. A checksum is not a publisher signature.

See docs/GUIDE.md for setup, replacement, coverage and troubleshooting.
''', encoding='utf-8')
for name in ['LICENSE', 'THIRD_PARTY_NOTICES.txt']:
    shutil.copy2(ROOT/name, out/name)
for name in ['docs', 'assets']:
    shutil.copytree(ROOT/name, out/name)
shutil.copy2(ROOT/'data/catalog/coverage.json', out/'catalog-coverage.json')
shutil.copytree(ROOT/'data/licenses', out/'licenses')
package_keys = set()
for _, directory, assembly in products:
    package_keys.update(json.loads((directory/f'{assembly}.deps.json').read_text())['libraries'])
cache = Path(os.environ.get('SWTOR_NUGET_PACKAGES', os.environ['NUGET_PACKAGES']))
license_index = {}
for key in sorted(package_keys):
    package = cache/key.lower()
    if not package.is_dir():
        continue
    info = {}
    for spec in package.glob('*.nuspec'):
        for element in ET.parse(spec).iter():
            tag = element.tag.rsplit('}',1)[-1]
            if tag in ['license', 'copyright', 'repository']:
                info[tag] = {'value':element.text, **element.attrib}
    for source in package.rglob('*'):
        if source.is_file() and any(word in source.name.lower() for word in ['license','licence','notice']):
            target = out/'licenses'/key/source.relative_to(package)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
    license_index[key] = info
(out/'licenses/dependencies.json').write_text(json.dumps(license_index, indent=2)+'\n')
commit = os.environ.get('SWTOR_SOURCE_COMMIT') or subprocess.check_output(['git','rev-parse','HEAD'], text=True).strip()
assert re.fullmatch('[0-9a-f]{40}', commit)
sdk = subprocess.check_output(dotnet+['--version'], text=True).strip()
runtime = json.loads((out/'agent/SwtorRecap.Agent.runtimeconfig.json').read_text())['runtimeOptions']['includedFrameworks'][0]['version']
versions = (ROOT/'src/SwtorRecap.Core/Domain.cs').read_text()
files = {path.relative_to(out).as_posix():hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(out.rglob('*')) if path.is_file()}
manifest = dict(applicationVersion=version, sourceCommit=commit, uncommittedImplementation=bool(subprocess.check_output(['git','status','--porcelain'], text=True).strip()), sdk=sdk, runtime=runtime, schema=1, parser=re.search(r'Parser = "([^"]+)"',versions)[1], metrics=re.search(r'Metrics = "([^"]+)"',versions)[1], catalog=re.search(r'Catalog = "([^"]+)"',versions)[1], rid=args.rid, signed=False, sha256=files)
validation = ROOT/'.artifacts/latest-validation.json'
if validation.exists():
    checks = json.loads(validation.read_text())
    manifest['validation'] = {k:checks[k] for k in ['ok','quick','tests','replay'] if k in checks}
(out/'release-manifest.json').write_text(json.dumps(manifest, indent=2)+'\n')
archive = out.with_suffix('.zip')
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for path in sorted(out.rglob('*')):
        if path.is_file():
            z.write(path, path.relative_to(out.parent).as_posix())
artifacts = [archive]
if args.installer:
    compiler = shutil.which('ISCC.exe') or str(Path(os.environ.get('ProgramFiles(x86)',r'C:\Program Files (x86)'))/'Inno Setup 6/ISCC.exe')
    if not Path(compiler).is_file():
        raise SystemExit('Install Inno Setup 6 from https://jrsoftware.org/isinfo.php to compile Setup.exe.')
    subprocess.run([compiler, f'/DAppVersion={version}', f'/DPackageDir={out}', str(ROOT/'installer/SwtorRecap.iss')], check=True)
    artifacts.append(ROOT/'artifacts'/f'SwtorRecap-{version}-Setup.exe')
checksums = ''.join(f'{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n' for path in artifacts)
(ROOT/'artifacts/SHA256SUMS.txt').write_text(checksums)
print(json.dumps([{'path':str(path), 'sha256':hashlib.sha256(path.read_bytes()).hexdigest(), 'bytes':path.stat().st_size} for path in artifacts], indent=2))
