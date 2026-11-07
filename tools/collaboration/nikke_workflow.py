"""Local NIKKE worktrees, integration, and serialized, version-checked deployment."""
import argparse
import base64
import ctypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid

HERE = Path(__file__).resolve().parent
EMPTY_IGNORE = HERE/'empty-gitignore'
EMPTY_IGNORE.touch(exist_ok=True)
if os.name=='nt':
    from ctypes import wintypes
    KERNEL32=ctypes.WinDLL('kernel32',use_last_error=True)
    KERNEL32.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,
        wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE]
    KERNEL32.CreateFileW.restype=wintypes.HANDLE
    KERNEL32.CloseHandle.argtypes=[wintypes.HANDLE]
    KERNEL32.CloseHandle.restype=wintypes.BOOL

class WorkflowError(RuntimeError):
    pass

def now():
    return datetime.now(timezone.utc).isoformat()

def sha(path):
    value=hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda:stream.read(1024*1024),b''):
            value.update(block)
    return value.hexdigest()

def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))

def write_json(path,data):
    path=Path(path)
    path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_name(path.name+'.'+uuid.uuid4().hex+'.tmp')
    temporary.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    os.replace(temporary,path)

def within(path,parent):
    path=Path(path).resolve()
    if not path.is_relative_to(Path(parent).resolve()):
        raise WorkflowError(f'Path must stay inside {parent}: {path}')
    return path

class FileLock:
    """OS lock, held for the complete transaction and released even after a crash."""
    def __init__(self,path,timeout=45):
        self.path=Path(path)
        self.timeout=timeout
        self.handle=None

    def __enter__(self):
        self.path.parent.mkdir(parents=True,exist_ok=True)
        if os.name!='nt':
            self.handle=self.path.open('a+b')
        deadline=time.monotonic()+self.timeout
        while True:
            try:
                if os.name=='nt':
                    # Share-mode exclusion remains active for inherited handles, even if the parent dies.
                    handle=KERNEL32.CreateFileW(str(self.path),0xC0000000,0,None,4,0x80,None)
                    if handle==ctypes.c_void_p(-1).value:
                        code=ctypes.get_last_error()
                        if code not in (32,33):
                            raise WorkflowError(f'Cannot open operation lock ({code}): {self.path}')
                        raise OSError(code,'File is already open exclusively')
                    self.handle=handle
                else:
                    import fcntl
                    fcntl.flock(self.handle.fileno(),fcntl.LOCK_EX|fcntl.LOCK_NB)
                return self
            except OSError as exc:
                if time.monotonic()>=deadline:
                    if self.handle is not None and os.name!='nt':
                        self.handle.close()
                    self.handle=None
                    raise WorkflowError(f'Operation locked by another session: {self.path}') from exc
                time.sleep(0.1)

    def __exit__(self,*args):
        if self.handle is not None:
            try:
                if os.name=='nt':
                    KERNEL32.CloseHandle(self.handle)
                else:
                    # Closing only our descriptor preserves an inherited child's flock.
                    self.handle.close()
            finally:
                self.handle=None

class Workflow:
    def __init__(self,settings=None):
        default_settings=HERE/'settings.json'
        if not default_settings.is_file():
            default_settings=Path(r'D:\Claude Lab\NIKKE\.tools\collaboration\settings.json')
        self.settings=Path(settings or default_settings).resolve()
        self.config=read_json(self.settings)
        self.root=Path(self.config['root']).resolve()
        self.repo=Path(self.config['sourceRepo']).resolve()
        self.live=Path(self.config['liveDirectory']).resolve()
        self.ref=self.config['integrationRef']
        self.state=Path(self.config['stateDirectory']).resolve()
        self.state.mkdir(parents=True,exist_ok=True)

    def git(self,*args,repo=None,data=None,check=True):
        repo=Path(repo or self.repo).resolve()
        result=subprocess.run(['git','--no-optional-locks','-c',f'safe.directory={repo.as_posix()}',
            '-c',f'core.excludesFile={EMPTY_IGNORE.as_posix()}',
            '-c','user.name=Codex local integration','-c','user.email=codex@local.invalid',
            '-c','commit.gpgsign=false','-C',str(repo),*args],input=data,capture_output=True)
        if check and result.returncode:
            raise WorkflowError(result.stderr.decode(errors='replace').strip() or result.stdout.decode(errors='replace').strip())
        return result

    def revision(self,ref='HEAD',repo=None):
        return self.git('rev-parse','--verify',ref+'^{commit}',repo=repo).stdout.decode().strip()

    def session_for(self,repo):
        repo=Path(repo).resolve()
        for record in (self.state/'sessions').glob('*.json'):
            session=read_json(record)
            if Path(session['sourceDirectory']).resolve()==repo:
                if not (repo/'.git').is_file():
                    raise WorkflowError('A separate linked Git worktree is required')
                common=self.git('rev-parse','--git-common-dir',repo=repo).stdout.decode().strip()
                if (repo/common).resolve() != (self.repo/'.git').resolve():
                    raise WorkflowError('The worktree belongs to a different repository')
                return session
        raise WorkflowError('Unregistered worktree; run start for this chat first')

    def initialize_fixtures(self,session):
        destination=Path(session['workspaceDirectory'])/'Server and Server selector'
        destination.mkdir(parents=True,exist_ok=True)
        packs=list((self.live/'cache').rglob('StaticData.pack'))
        if len(packs)!=1:
            raise WorkflowError('Expected one StaticData.pack in the live static cache')
        inputs=[packs[0],self.live/'gamecommon.json']
        for source in inputs:
            target=within(destination/source.relative_to(self.live),destination)
            target.parent.mkdir(parents=True,exist_ok=True)
            if not target.exists() or sha(source)!=sha(target):
                shutil.copy2(source,target)
        # Reuse only NPC fixture data, with a synthetic empty human; never copy live saves.
        fixture=self.root/'.tools/counsel-affinity-20261108/full-suite-root/Server and Server selector/db.json'
        target=destination/'db.json'
        if fixture.is_file() and not target.exists():
            source=read_json(fixture)
            core={'DbVersion':5,'BondRewardLevelsMigrated':True,
                  'LauncherTokenKey':base64.b64encode(bytes(32)).decode(),
                  'EncryptionTokenKey':base64.b64encode(bytes(32)).decode(),
                  'Users':[{'ID':1552239268}]+[u for u in source['Users'] if u.get('ManagedNpc') is not None]}
            write_json(target,core)
        write_json(destination.parent/'fixture-provenance.json',{'createdUtc':now(),
            'staticFiles':[{'relative':str(p.relative_to(self.live)),'sha256':sha(p)} for p in inputs],
            'human':'synthetic empty fixture','liveSaveCopied':False,'liveDirectoryLinked':False})

    def start(self,session_id,initialize=True):
        if not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}',session_id):
            raise WorkflowError('Invalid session identifier')
        registry=self.state/'sessions'/f'{session_id}.json'
        with FileLock(self.state/'integration.lock'):
            if registry.exists():
                record=read_json(registry)
                self.session_for(record['sourceDirectory'])
            else:
                workspace=within(self.root/'.worktrees'/session_id,self.root/'.worktrees')
                repo=workspace/'src/EpinelPS'
                if repo.exists():
                    raise WorkflowError('Session directory already exists without a matching registration')
                repo.parent.mkdir(parents=True,exist_ok=True)
                base=self.revision(self.ref)
                self.git('worktree','add','--detach',str(repo),base)
                record={'session':session_id,'workspaceDirectory':str(workspace),'sourceDirectory':str(repo),
                    'baseCommit':base,'createdUtc':now()}
                write_json(registry,record)
            # One session id may be reused only by its owning chat. Different ids never share data.
            if initialize:
                self.initialize_fixtures(record)
            return record

    def source_identity(self,repo):
        repo=Path(repo).resolve()
        self.session_for(repo)
        if self.git('status','--porcelain=v1',repo=repo).stdout:
            raise WorkflowError('A clean committed worktree is required')
        files=self.git('ls-files','-z',repo=repo).stdout.split(b'\0')
        digest=hashlib.sha256()
        for item in sorted(n for n in files if n):
            path=within(repo/os.fsdecode(item),repo)
            digest.update(item+b'\0'+sha(path).encode()+b'\0')
        return {'commit':self.revision(repo=repo),'sourceDigest':digest.hexdigest()}

    def integrate(self,repo):
        repo=Path(repo).resolve()
        identity=self.source_identity(repo)
        with FileLock(self.state/'integration.lock'):
            previous=self.revision(self.ref)
            head=identity['commit']
            if self.git('merge-base','--is-ancestor',head,previous,check=False).returncode==0:
                return {'status':'already-integrated','commit':previous}
            if self.git('merge-base','--is-ancestor',previous,head,check=False).returncode==0:
                merged=head
            else:
                merge=self.git('merge-tree','--write-tree',previous,head,check=False)
                if merge.returncode:
                    raise WorkflowError('Integration conflict; resolve and test in your own worktree before retrying: '+merge.stdout.decode(errors='replace'))
                tree=merge.stdout.decode().splitlines()[0]
                merged=self.git('commit-tree',tree,'-p',previous,'-p',head,
                    data=b'Merge isolated NIKKE session into integration\n').stdout.decode().strip()
            self.git('update-ref',self.ref,merged,previous)
            record={'status':'integrated','previous':previous,'commit':merged,'sessionCommit':head,'utc':now()}
            write_json(self.state/'last-integration.json',record)
            return record

    def sync(self,repo):
        repo=Path(repo).resolve()
        self.source_identity(repo)
        with FileLock(self.state/'integration.lock'):
            self.git('merge','--ff-only',self.ref,repo=repo)
        return self.source_identity(repo)

    def dotnet_environment(self,repo):
        environment=os.environ.copy()
        environment['DOTNET_ROOT']=str(self.root/'.tools/dotnet')
        environment['DOTNET_CLI_HOME']=str(Path(repo)/'.nikke/cli')
        environment['NUGET_PACKAGES']=str(self.root/'.tools/cli/.nuget/packages')
        environment['NIKKE_WORKSPACE_ROOT']=self.session_for(repo)['workspaceDirectory']
        return environment

    def dotnet(self,repo,args,log):
        Path(log).parent.mkdir(parents=True,exist_ok=True)
        flags=['-p:NuGetAudit=false','-p:TargetPlatformSdkPath='+str(self.root/'.tools/dotnet'),
               '-p:TargetPlatformDisplayName=Windows','-v:q']
        with Path(log).open('w',encoding='utf-8') as output:
            result=subprocess.run([str(self.root/'.tools/dotnet/dotnet.exe'),*args,*flags],
                cwd=repo,env=self.dotnet_environment(repo),stdout=output,stderr=subprocess.STDOUT)
        if result.returncode:
            raise WorkflowError(f'Build failed; see {log}')

    def test(self,repo,suites):
        repo=Path(repo).resolve()
        identity=self.source_identity(repo)
        work=repo/'.nikke/checks'/datetime.now().strftime('%Y%m%d-%H%M%S-%f')
        work.mkdir(parents=True)
        projects={p.parent.name:p for p in (repo/'tests').glob('*/*.csproj')}
        names=sorted(projects) if suites==['all'] else suites
        if not names or any(name not in projects for name in names):
            raise WorkflowError('Unknown regression suite')
        runs=[]
        for name in names:
            self.dotnet(repo,['build',str(projects[name]),'-c','Release','--ignore-failed-sources'],work/f'{name}-build.log')
            exe=projects[name].parent/'bin/Release/net10.0/win-x64'/f'{name}.exe'
            modes=['default']
            if name in {'LostSectorRewardRegression','DailyMissionRegression','WeeklyMissionRegression',
                        'ArenaRegression','ArenaCubeRegression','ManagedNpcRegression','GuildRegression',
                        'ShootingRangeRegression','CharacterToggleRegression','DuplicateCoreRegression',
                        'ChallengeStageRegression','InterceptionRegression','ActivityMissionRegression',
                        'SoloRaidRankingRegression','FriendRegression','LiberateResetRegression'}:
                modes.append('--verify-saved')
            for mode in modes:
                log=work/f'{name}-{mode}.log'
                with log.open('w',encoding='utf-8') as output:
                    result=subprocess.run([str(exe),*([mode] if mode!='default' else [])],cwd=exe.parent,
                        env=self.dotnet_environment(repo),stdout=output,stderr=subprocess.STDOUT)
                row={'suite':name,'mode':mode,'exit':result.returncode,
                    'groups':sum(line.startswith('PASS') for line in log.read_text(encoding='utf-8',errors='replace').splitlines())}
                runs.append(row)
                print(json.dumps(row),flush=True)
                if result.returncode:
                    raise WorkflowError(f'Regression failed; see {log}')
        if self.source_identity(repo)!=identity:
            raise WorkflowError('Source changed during regression tests')
        proof={'status':'passed',**identity,'runs':runs,'utc':now()}
        write_json(work/'tests.json',proof)
        return {'proof':str(work/'tests.json'),**proof}

    def publish(self,repo):
        repo=Path(repo).resolve()
        identity=self.source_identity(repo)
        if identity['commit']!=self.revision(self.ref):
            raise WorkflowError('Publish only the latest integration commit; integrate and sync first')
        directory=repo/'.nikke/releases'/datetime.now().strftime('%Y%m%d-%H%M%S-%f')
        self.dotnet(repo,['publish',str(repo/'EpinelPS/EpinelPS.csproj'),'-c','Release','-r','win-x64',
            '--ignore-failed-sources','-o',str(directory/'publish')],directory/'publish.log')
        if self.source_identity(repo)!=identity:
            raise WorkflowError('Source changed during publish')
        candidate=directory/'publish/EpinelPS.exe'
        payload_root=candidate.parent
        proof={'status':'passed',**identity,'candidate':str(candidate),'candidateSha256':sha(candidate),'utc':now(),
            'payloadRoot':str(payload_root),'payload':[{'relative':str(p.relative_to(payload_root)),
                'sha256':sha(p)} for p in sorted(payload_root.rglob('*')) if p.is_file()]}
        write_json(directory/'build.json',proof)
        return {'proof':str(directory/'build.json'),**proof}

    def verify_payload(self,proof,repo):
        folder=within(proof['payloadRoot'],repo)
        expected={row['relative']:row['sha256'] for row in proof['payload']}
        actual={str(p.relative_to(folder)) for p in folder.rglob('*') if p.is_file()}
        if actual!=set(expected):
            raise WorkflowError('Published payload inventory changed')
        for relative,expected_hash in expected.items():
            if sha(within(folder/relative,folder))!=expected_hash:
                raise WorkflowError('Published payload hash changed: '+relative)

    def generation(self):
        path=self.state/'deployment-generation.json'
        return read_json(path)['generation'] if path.exists() else 'baseline'

    def seal(self,repo,build_file,tests_file,installer):
        with FileLock(self.state/'deployment.lock'):
            with FileLock(self.state/'integration.lock'):
                return self._seal_locked(repo,build_file,tests_file,installer)

    def _seal_locked(self,repo,build_file,tests_file,installer):
        repo=Path(repo).resolve()
        identity=self.source_identity(repo)
        if identity['commit']!=self.revision(self.ref):
            raise WorkflowError('Release must use the latest integration commit')
        build_file=within(build_file,repo);tests_file=within(tests_file,repo);installer=within(installer,repo)
        build=read_json(build_file);tests=read_json(tests_file)
        for proof in [build,tests]:
            if proof.get('status')!='passed' or any(proof.get(k)!=v for k,v in identity.items()):
                raise WorkflowError('Build/test proof does not match the current source')
        if not tests.get('runs') or any(run.get('exit')!=0 for run in tests['runs']):
            raise WorkflowError('Passing regression evidence is required')
        candidate=within(build['candidate'],repo)
        self.verify_payload(build,repo)
        if sha(candidate)!=build['candidateSha256']:
            raise WorkflowError('Candidate hash differs from the build proof')
        if installer.suffix.lower()!='.ps1':
            raise WorkflowError('Expected a reviewed PowerShell installer with backup, rollback and health checks')
        manifest={'schemaVersion':1,'utc':now(),'sourceDirectory':str(repo),**identity,
            'expectedInstalledSha256':sha(self.live/'EpinelPS.exe'),'candidate':str(candidate),
            'expectedDeploymentGeneration':self.generation(),
            'candidateSha256':sha(candidate),'installer':str(installer),'installerSha256':sha(installer),
            'payloadRoot':build['payloadRoot'],'payload':build['payload'],
            'evidence':[{'path':str(p),'sha256':sha(p)} for p in [build_file,tests_file]]}
        path=build_file.parent/('release-'+uuid.uuid4().hex[:10]+'.json')
        write_json(path,manifest)
        return str(path)

    def validate_release(self,path):
        manifest=read_json(path)
        if manifest.get('schemaVersion')!=1:
            raise WorkflowError('Unsupported release manifest')
        repo=Path(manifest['sourceDirectory']).resolve()
        identity=self.source_identity(repo)
        if manifest['commit']!=self.revision(self.ref):
            raise WorkflowError('Release is stale against the latest integration commit')
        if any(manifest.get(k)!=v for k,v in identity.items()):
            raise WorkflowError('Release source identity changed')
        if sha(self.live/'EpinelPS.exe')!=manifest['expectedInstalledSha256']:
            raise WorkflowError('The installed release changed; rebuild/review this package before deployment')
        if manifest.get('expectedDeploymentGeneration')!=self.generation():
            raise WorkflowError('Deployment generation changed; recheck and seal against the new installed state')
        for name in ['candidate','installer']:
            artifact=within(manifest[name],repo)
            if sha(artifact)!=manifest[name+'Sha256']:
                raise WorkflowError(name+' hash changed')
        for evidence in manifest['evidence']:
            if sha(within(evidence['path'],repo))!=evidence['sha256']:
                raise WorkflowError('Evidence hash changed')
        self.verify_payload(manifest,repo)
        return manifest

    def run_installer(self,installer,output,locks):
        command=[self.config.get('powershell','pwsh'),'-NoProfile','-NonInteractive','-File',
            str(HERE/'run-installer.ps1'),'-Installer',str(installer)]
        options={'cwd':Path(installer).parent,'stdout':output,'stderr':subprocess.STDOUT}
        if os.name=='nt':
            handles=[lock.handle for lock in locks]
            command+=['-LockHandles',','.join(str(handle) for handle in handles)]
            startup=subprocess.STARTUPINFO()
            startup.lpAttributeList={'handle_list':handles}
            options.update(startupinfo=startup,close_fds=True,creationflags=subprocess.CREATE_NO_WINDOW)
            for handle in handles:
                os.set_handle_inheritable(handle,True)
            try:
                process=subprocess.Popen(command,**options)
            finally:
                for handle in handles:
                    os.set_handle_inheritable(handle,False)
        else:
            options['pass_fds']=tuple(lock.handle.fileno() for lock in locks)
            process=subprocess.Popen(command,**options)
        return process.wait()

    def deploy(self,path):
        # Keep integration stable throughout deployment; every publisher uses the same two locks.
        with FileLock(self.state/'deployment.lock') as deployment_lock:
            with FileLock(self.state/'integration.lock') as integration_lock:
                manifest=self.validate_release(path)
                generation=uuid.uuid4().hex
                # A failed or resource-only installer also invalidates every pre-existing manifest.
                write_json(self.state/'deployment-generation.json',{'generation':generation,'utc':now()})
                operation={'status':'installing','manifest':str(Path(path).resolve()),'startedUtc':now(),
                    'pid':os.getpid(),'commit':manifest['commit'],'generation':generation}
                write_json(self.state/'deployment-status.json',operation)
                log=self.state/('deployment-'+uuid.uuid4().hex+'.log')
                try:
                    with log.open('w',encoding='utf-8') as output:
                        code=self.run_installer(manifest['installer'],output,[deployment_lock,integration_lock])
                    if code:
                        raise WorkflowError(f'Installer failed ({code}); see {log}')
                    if sha(self.live/'EpinelPS.exe')!=manifest['candidateSha256']:
                        raise WorkflowError('Installer exited without installing the sealed candidate')
                    operation.update(status='installed',completedUtc=now(),installedSha256=sha(self.live/'EpinelPS.exe'),log=str(log))
                    write_json(self.state/'deployment-status.json',operation)
                    return operation
                except Exception as exc:
                    operation.update(status='failed',completedUtc=now(),error=str(exc),log=str(log))
                    write_json(self.state/'deployment-status.json',operation)
                    raise

    def status(self):
        return {'integrationRef':self.ref,'integrationCommit':self.revision(self.ref),
            'installedSha256':sha(self.live/'EpinelPS.exe'),
            'deploymentGeneration':self.generation(),
            'sessions':[read_json(p) for p in (self.state/'sessions').glob('*.json')],
            'deployment':read_json(self.state/'deployment-status.json') if (self.state/'deployment-status.json').exists() else None}

def main():
    if os.name=='nt':
        ctypes.windll.kernel32.SetErrorMode(0x8003)
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--settings',type=Path)
    sub=parser.add_subparsers(dest='command',required=True)
    start=sub.add_parser('start');start.add_argument('--session',required=True)
    sub.add_parser('status')
    for command in ['integrate','sync','publish','test']:
        item=sub.add_parser(command);item.add_argument('--worktree',required=True,type=Path)
        if command=='test':item.add_argument('--suite',nargs='+',default=['all'])
    seal=sub.add_parser('seal')
    for arg in ['worktree','build','tests','installer']:seal.add_argument('--'+arg,required=True,type=Path)
    for command in ['check-release','deploy']:
        item=sub.add_parser(command);item.add_argument('--manifest',required=True,type=Path)
    args=parser.parse_args()
    w=Workflow(args.settings)
    if args.command=='start':result=w.start(args.session)
    elif args.command=='status':result=w.status()
    elif args.command=='integrate':result=w.integrate(args.worktree)
    elif args.command=='sync':result=w.sync(args.worktree)
    elif args.command=='test':result=w.test(args.worktree,args.suite)
    elif args.command=='publish':result=w.publish(args.worktree)
    elif args.command=='seal':result={'manifest':w.seal(args.worktree,args.build,args.tests,args.installer)}
    elif args.command=='check-release':
        with FileLock(w.state/'deployment.lock'):
            with FileLock(w.state/'integration.lock'):
                result=w.validate_release(args.manifest)
    else:result=w.deploy(args.manifest)
    print(json.dumps(result,ensure_ascii=False,indent=2))

if __name__=='__main__':
    try:main()
    except (WorkflowError,OSError,ValueError) as exc:
        print('ERROR: '+str(exc),file=sys.stderr)
        sys.exit(1)
