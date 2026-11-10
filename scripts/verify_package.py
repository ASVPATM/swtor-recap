#!/usr/bin/env python3
"""Check the shipped ZIP, including every file hash and the Windows GUI app hosts."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zipfile

parser=argparse.ArgumentParser()
parser.add_argument('archive',nargs='?',default='artifacts/SwtorRecap-win-x64.zip')
args=parser.parse_args()
path=Path(args.archive)
with zipfile.ZipFile(path) as archive:
    assert archive.testzip() is None, 'ZIP CRC failure'
    names=archive.namelist()
    prefix='SwtorRecap-win-x64/'
    assert len(names)==len(set(names)), 'Duplicate archive paths'
    assert all(n.startswith(prefix) and '..' not in n.split('/') and '\\' not in n for n in names)
    manifest=json.loads(archive.read(prefix+'release-manifest.json'))
    files=manifest['sha256']
    assert set(names)=={prefix+n for n in files}|{prefix+'release-manifest.json'}, 'Unmanifested files'
    for name,digest in files.items():
        assert hashlib.sha256(archive.read(prefix+name)).hexdigest()==digest, name
    assert not any(n.lower().endswith(('.cmd','.bat','.ps1','.db','.pfx','.key')) for n in names)
    def pe(name,gui=False):
        data=archive.read(prefix+name)
        offset=struct.unpack_from('<I',data,60)[0]
        assert data[:2]==b'MZ' and data[offset:offset+4]==b'PE\0\0', name
        assert struct.unpack_from('<H',data,offset+4)[0]==0x8664, name+' must be x64'
        optional=offset+24
        assert struct.unpack_from('<H',data,optional)[0]==0x20b
        if gui:
            assert struct.unpack_from('<H',data,optional+68)[0]==2, name+' must not open a console'
            resource_rva,resource_size=struct.unpack_from('<II',data,optional+112+16)
            assert resource_rva and resource_size, name+' icon resources missing'
        return data
    pe('SwtorRecap.exe',True);pe('agent/SwtorRecap.Agent.exe',True)
    pe('e_sqlite3.dll');pe('agent/e_sqlite3.dll');pe('libSkiaSharp.dll')
    ico=archive.read(prefix+'assets/app.ico')
    assert struct.unpack_from('<HHH',ico)==(0,1,7), 'All seven Windows icon sizes are required'
    assert archive.read(prefix+'assets/logo.png').startswith(b'\x89PNG')
    assert 'swtor-recap' in archive.read(prefix+'docs/GUIDE.md').decode()
print(json.dumps({'ok':True,'version':manifest['applicationVersion'],'sourceCommit':manifest['sourceCommit'],'files':len(files),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'bytes':path.stat().st_size},indent=2))
