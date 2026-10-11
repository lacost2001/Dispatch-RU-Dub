"""Deterministic install/update/recovery tests using synthetic files, never the game."""
import hashlib,json,pathlib,shutil,struct,subprocess,tempfile,zipfile,os
os.chdir(pathlib.Path(__file__).resolve().parent)
pathlib.Path("tests-output").mkdir(exist_ok=True)
ROOT=pathlib.Path(tempfile.mkdtemp(prefix='patcher-v2-',dir='tests-output')).resolve()
BIN=ROOT/'bin';BIN.mkdir();EXE=BIN/'Dispatch-RU-Patcher.exe'
shutil.copy2('bin/Dispatch-RU-Patcher.exe',EXE)
BACK='.dispatch-ru-backup-v01';results=[]
def sha(b):return hashlib.sha256(b).hexdigest()
original={n:bytes(range(256))*17+n.encode() for n in ['One.bank','Two.bank']}
def package(version):
    targets={n:b[:500]+version.encode()*350+b[500:] for n,b in original.items()};banks=[]
    with zipfile.ZipFile(BIN/'Dispatch-RU.data','w') as z:
        for n,b in original.items():
            new=targets[n];delta=b'DRUP0001'+bytes.fromhex(sha(b))+bytes.fromhex(sha(new))+struct.pack('<QI',len(new),1)+struct.pack('<BQ',1,len(new))+new
            banks.append(dict(name=n,source_sha256=sha(b),target_sha256=sha(new),source_bytes=len(b),target_bytes=len(new),patch_sha256=sha(delta)))
            z.writestr(n+'.drup',delta)
        m=dict(format=1,version=version,banks=banks);z.writestr('manifest.json',json.dumps(m))
    return m,targets
def game(name):
    p=ROOT/name;p.mkdir();(p/'Master.bank').write_bytes(b'master')
    for n,b in original.items():(p/n).write_bytes(b)
    return p
def run(p,op,ok=True,exe=EXE):
    result=subprocess.run([str(exe),op,str(p),str(p/'log.txt')])
    assert (result.returncode==0)==ok,(op,(p/'log.txt').read_text(encoding='utf-8-sig'))
def verify(p,expected):assert all((p/n).read_bytes()==b for n,b in expected.items())
old,oldbytes=package('0.1-preview');p=game('update')
run(p,'install');verify(p,oldbytes);results.append('clean_install')
new,newbytes=package('0.2-preview');run(p,'check');verify(p,oldbytes)
run(p,'install');verify(p,newbytes);verify(p/BACK,original);results.append('update_old_release_using_original_backups')
run(p,'install');verify(p,newbytes);results.append('idempotent_update')
run(p,'restore');verify(p,original);results.append('restore_after_update')
old,oldbytes=package('0.1-preview');run(p,'install')
new,newbytes=package('0.2-preview');(p/'One.bank').write_bytes(newbytes['One.bank'])
(p/BACK/'transaction.json').write_text(json.dumps(dict(status='committing',manifest=new,previous_manifest=old)))
run(p,'install',False);run(p,'restore');verify(p,original);results.append('recover_interrupted_old_to_new_update')
run(p,'install');(p/'One.bank').write_bytes(b'foreign mod');run(p,'restore',False)
assert (p/'One.bank').read_bytes()==b'foreign mod';results.append('foreign_mod_not_overwritten')
q=game('damaged_payload');data=bytearray((BIN/'Dispatch-RU.data').read_bytes());data[len(data)//3]^=1;(BIN/'Dispatch-RU.data').write_bytes(data)
run(q,'install',False);verify(q,original);results.append('damaged_payload_no_mutation')
package('0.1-preview');q=game('fault_upgrade');run(q,'install');package('0.2-preview')
source=pathlib.Path('DispatchRuPatcher.cs').read_text(encoding='utf8');needle='File.Replace(Path.Combine(stage,b.name),Path.Combine(root,b.name),null);'
assert source.count(needle)==1
source=source.replace(needle,'if(replaced.Count==1)throw new IOException("Injected commit failure");'+needle)
fault=ROOT/'fault.cs';fault.write_text(source,encoding='utf-8-sig');fault_exe=BIN/'Fault.exe'
subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/target:winexe','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Web.Extensions.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll','/out:'+str(fault_exe),str(fault),str(pathlib.Path('DispatchRuUpdater.cs').resolve())],check=True)
run(q,'install',False,fault_exe);verify(q,original);results.append('failed_upgrade_restores_all_original_banks')
report=dict(passed=len(results),tests=results)
pathlib.Path('tests-output/update_tests.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))
