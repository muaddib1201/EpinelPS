import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('nikke_workflow', HERE/'nikke_workflow.py')
workflow = importlib.util.module_from_spec(spec)
spec.loader.exec_module(workflow)

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

class WorkflowTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='nikke-test-', dir=HERE)
        self.root = Path(self.tmp.name)
        self.repo = self.root/'src/EpinelPS'
        self.repo.mkdir(parents=True)
        subprocess.run(['git','init','-q',str(self.repo)],check=True)
        self.git('config','user.name','Workflow test')
        self.git('config','user.email','workflow@local.invalid')
        (self.repo/'source.txt').write_text('baseline\n')
        (self.repo/'.gitignore').write_text('.nikke/\n')
        self.git('add','.')
        self.git('commit','-qm','baseline')
        self.git('branch','nikke/integration')
        self.live=self.root/'Server and Server selector'
        self.live.mkdir()
        (self.live/'EpinelPS.exe').write_bytes(b'baseline executable')
        self.settings=self.root/'settings.json'
        self.settings.write_text(json.dumps({'root':str(self.root),'sourceRepo':str(self.repo),
            'integrationRef':'refs/heads/nikke/integration','liveDirectory':str(self.live),
            'stateDirectory':str(self.root/'control')}))
        self.w=workflow.Workflow(self.settings)

    def tearDown(self):
        self.assertTrue(self.root.resolve().is_relative_to(HERE.resolve()))
        self.tmp.cleanup()

    def git(self,*args,cwd=None):
        return subprocess.check_output(['git','-c',f'safe.directory={(cwd or self.repo).as_posix()}',
            '-C',str(cwd or self.repo),*args],stderr=subprocess.DEVNULL).decode().strip()

    def release(self):
        session=self.w.start('release',initialize=False)
        repo=Path(session['sourceDirectory'])
        artifacts=repo/'.nikke/release'
        artifacts.mkdir(parents=True)
        (artifacts/'publish').mkdir()
        candidate=artifacts/'publish/EpinelPS.exe'
        candidate.write_bytes(b'combined replacement')
        (artifacts/'publish/page.html').write_text('verified page')
        installer=artifacts/'install.ps1'
        q=lambda p:str(p).replace("'","''")
        installer.write_text("$ErrorActionPreference='Stop'\n[IO.File]::Copy('"+q(candidate)+"','"+q(self.live/'EpinelPS.exe')+"',$true)\n")
        identity=self.w.source_identity(repo)
        build=artifacts/'build.json'
        tests=artifacts/'tests.json'
        build.write_text(json.dumps({'status':'passed',**identity,'candidate':str(candidate),'candidateSha256':sha(candidate),
            'payloadRoot':str(candidate.parent),'payload':[{'relative':p.name,'sha256':sha(p)} for p in candidate.parent.iterdir()]}))
        tests.write_text(json.dumps({'status':'passed',**identity,'runs':[{'suite':'fixture','exit':0,'groups':1}]}))
        return repo,installer,self.w.seal(repo,build,tests,installer)

    def test_sessions_have_separate_files_and_indexes(self):
        original=sha(self.repo/'.git/index')
        a=Path(self.w.start('alpha',initialize=False)['sourceDirectory'])
        b=Path(self.w.start('beta',initialize=False)['sourceDirectory'])
        (a/'source.txt').write_text('alpha only\n')
        self.assertEqual((b/'source.txt').read_text(),'baseline\n')
        self.assertNotEqual(self.git('rev-parse','--absolute-git-dir',cwd=a),self.git('rev-parse','--absolute-git-dir',cwd=b))
        self.assertEqual(sha(self.repo/'.git/index'),original)

    def test_session_path_traversal_is_rejected(self):
        with self.assertRaises(workflow.WorkflowError):
            self.w.start('../escape',initialize=False)

    def test_existing_session_is_reused(self):
        a=self.w.start('alpha',initialize=False)
        self.assertEqual(a,self.w.start('alpha',initialize=False))

    def test_cross_process_lock_blocks_other_process(self):
        path=self.root/'shared.lock'
        with workflow.FileLock(path,timeout=0):
            code='from nikke_workflow import FileLock; import sys;\nwith FileLock(sys.argv[1],timeout=0): pass'
            child=subprocess.run([sys.executable,'-c',code,str(path)],cwd=HERE,capture_output=True)
            self.assertNotEqual(child.returncode,0)
            self.assertIn(b'locked',child.stderr.lower())
        with workflow.FileLock(path,timeout=0):
            pass

    def test_stale_installed_release_is_rejected(self):
        repo,installer,manifest=self.release()
        (self.live/'EpinelPS.exe').write_bytes(b'another session installed this')
        with self.assertRaisesRegex(workflow.WorkflowError,'installed'):
            self.w.deploy(manifest)
        self.assertEqual((self.live/'EpinelPS.exe').read_bytes(),b'another session installed this')

    def test_stale_integration_is_rejected(self):
        repo,installer,manifest=self.release()
        (repo/'source.txt').write_text('new fix\n')
        self.git('add','source.txt',cwd=repo)
        self.git('-c','user.name=Test','-c','user.email=test@local.invalid','commit','-qm','new fix',cwd=repo)
        self.w.integrate(repo)
        with self.assertRaisesRegex(workflow.WorkflowError,'integration'):
            self.w.deploy(manifest)

    def test_candidate_tampering_is_rejected(self):
        repo,installer,manifest=self.release()
        candidate=Path(json.loads(Path(manifest).read_text())['candidate'])
        candidate.write_bytes(b'tampered')
        with self.assertRaisesRegex(workflow.WorkflowError,'hash'):
            self.w.deploy(manifest)

    def test_sibling_asset_tampering_is_rejected(self):
        repo,installer,manifest=self.release()
        asset=Path(json.loads(Path(manifest).read_text())['candidate']).parent/'page.html'
        asset.write_text('changed after verification')
        with self.assertRaisesRegex(workflow.WorkflowError,'hash'):
            self.w.deploy(manifest)

    def test_new_unverified_payload_file_is_rejected(self):
        repo,installer,manifest=self.release()
        folder=Path(json.loads(Path(manifest).read_text())['candidate']).parent
        (folder/'extra.html').write_text('unverified')
        with self.assertRaisesRegex(workflow.WorkflowError,'inventory'):
            self.w.deploy(manifest)

    def test_dirty_source_cannot_be_sealed(self):
        repo,installer,manifest=self.release()
        (repo/'source.txt').write_text('untested change\n')
        with self.assertRaisesRegex(workflow.WorkflowError,'clean'):
            self.w.deploy(manifest)

    def test_successful_deployment_records_result_and_stale_retry_fails(self):
        repo,installer,manifest=self.release()
        result=self.w.deploy(manifest)
        self.assertEqual(result['status'],'installed')
        self.assertEqual((self.live/'EpinelPS.exe').read_bytes(),b'combined replacement')
        with self.assertRaisesRegex(workflow.WorkflowError,'installed'):
            self.w.deploy(manifest)

    def test_failed_installer_releases_shared_lock(self):
        repo,installer,manifest=self.release()
        installer.write_text('exit 7\n')
        data=json.loads(Path(manifest).read_text())
        data['installerSha256']=sha(installer)
        Path(manifest).write_text(json.dumps(data))
        with self.assertRaisesRegex(workflow.WorkflowError,'(?i)installer'):
            self.w.deploy(manifest)
        with workflow.FileLock(self.w.state/'deployment.lock',timeout=0):
            pass

    def test_same_executable_deployment_still_invalidates_queued_releases(self):
        repo,installer,manifest=self.release()
        data=json.loads(Path(manifest).read_text())
        candidate=Path(data['candidate'])
        candidate.write_bytes((self.live/'EpinelPS.exe').read_bytes())
        build_file=Path(data['evidence'][0]['path'])
        tests_file=Path(data['evidence'][1]['path'])
        proof=json.loads(build_file.read_text())
        proof['candidateSha256']=sha(candidate)
        proof['payload']=[{'relative':p.name,'sha256':sha(p)} for p in candidate.parent.iterdir()]
        build_file.write_text(json.dumps(proof))
        sealed=self.w.seal(repo,build_file,tests_file,installer)
        self.w.deploy(sealed)
        with self.assertRaisesRegex(workflow.WorkflowError,'generation'):
            self.w.deploy(sealed)

    def test_failed_installer_invalidates_prepared_releases(self):
        repo,installer,manifest=self.release()
        installer.write_text('exit 7\n')
        data=json.loads(Path(manifest).read_text())
        data['installerSha256']=sha(installer)
        Path(manifest).write_text(json.dumps(data))
        with self.assertRaisesRegex(workflow.WorkflowError,'(?i)installer'):
            self.w.deploy(manifest)
        with self.assertRaisesRegex(workflow.WorkflowError,'generation'):
            self.w.deploy(manifest)

    def test_nonconflicting_commits_are_merged_without_resetting_source(self):
        a=Path(self.w.start('alpha',initialize=False)['sourceDirectory'])
        b=Path(self.w.start('beta',initialize=False)['sourceDirectory'])
        for repo,name in [(a,'alpha.txt'),(b,'beta.txt')]:
            (repo/name).write_text(name)
            self.git('add',name,cwd=repo)
            self.git('-c','user.name=Test','-c','user.email=test@local.invalid','commit','-qm',name,cwd=repo)
            self.w.integrate(repo)
        tip=self.git('rev-parse','nikke/integration')
        self.assertEqual(self.git('show',tip+':alpha.txt'),'alpha.txt')
        self.assertEqual(self.git('show',tip+':beta.txt'),'beta.txt')
        self.assertEqual((self.repo/'source.txt').read_text(),'baseline\n')

    def test_conflicting_changes_stop_without_moving_integration(self):
        worktrees=[Path(self.w.start(name,initialize=False)['sourceDirectory']) for name in ['alpha','beta']]
        for repo,text in zip(worktrees,['alpha\n','beta\n']):
            (repo/'source.txt').write_text(text)
            self.git('add','source.txt',cwd=repo)
            self.git('-c','user.name=Test','-c','user.email=test@local.invalid','commit','-qm',text.strip(),cwd=repo)
        self.w.integrate(worktrees[0])
        before=self.git('rev-parse','nikke/integration')
        with self.assertRaisesRegex(workflow.WorkflowError,'conflict'):
            self.w.integrate(worktrees[1])
        self.assertEqual(self.git('rev-parse','nikke/integration'),before)
        self.assertEqual((worktrees[1]/'source.txt').read_text(),'beta\n')

    def test_two_installers_serialize_and_only_one_can_replace_the_same_base(self):
        repo,installer,manifest=self.release()
        installer.write_text('Start-Sleep -Milliseconds 400\n'+installer.read_text())
        data=json.loads(Path(manifest).read_text())
        data['installerSha256']=sha(installer)
        Path(manifest).write_text(json.dumps(data))
        command=[sys.executable,str(HERE/'nikke_workflow.py'),'--settings',str(self.settings),'deploy','--manifest',str(manifest)]
        first=subprocess.Popen(command,cwd=HERE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        second=subprocess.Popen(command,cwd=HERE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        a=first.communicate(timeout=25);b=second.communicate(timeout=25)
        self.assertEqual(sorted([first.returncode,second.returncode]),[0,1],(a,b))
        self.assertEqual((self.live/'EpinelPS.exe').read_bytes(),b'combined replacement')

    def test_installer_keeps_lock_after_its_python_parent_is_terminated(self):
        repo,installer,manifest=self.release()
        ready=installer.parent/'writer-ready.txt'
        done=installer.parent/'writer-done.txt'
        q=lambda p:str(p).replace("'","''")
        installer.write_text("[IO.File]::WriteAllText('"+q(ready)+"','ready')\nStart-Sleep -Milliseconds 1800\n"+
            installer.read_text()+"\n[IO.File]::WriteAllText('"+q(done)+"','done')\n")
        data=json.loads(Path(manifest).read_text())
        data['installerSha256']=sha(installer)
        Path(manifest).write_text(json.dumps(data))
        command=[sys.executable,str(HERE/'nikke_workflow.py'),'--settings',str(self.settings),'deploy','--manifest',str(manifest)]
        parent=subprocess.Popen(command,cwd=HERE,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        deadline=time.monotonic()+15
        while not ready.exists() and time.monotonic()<deadline:
            time.sleep(0.05)
        self.assertTrue(ready.exists(),'Installer did not start')
        parent.kill();parent.wait(timeout=5)
        try:
            with self.assertRaisesRegex(workflow.WorkflowError,'locked'):
                with workflow.FileLock(self.w.state/'deployment.lock',timeout=0):
                    pass
        finally:
            while not done.exists() and time.monotonic()<deadline:
                time.sleep(0.05)
            with workflow.FileLock(self.w.state/'deployment.lock',timeout=5):
                pass
        self.assertTrue(done.exists(),'Installer did not finish after parent exit')

    def test_installer_does_not_pass_its_locks_to_a_background_server(self):
        repo,installer,manifest=self.release()
        done=installer.parent/'background-done.txt'
        sleeper=installer.parent/'background.py'
        sleeper.write_text('import time\nfrom pathlib import Path\ntime.sleep(2)\nPath('+repr(str(done))+').write_text("done")\n')
        q=lambda p:str(p).replace("'","''")
        installer.write_text("$background = Start-Process -FilePath '"+q(sys.executable)+"' -ArgumentList '\""+q(sleeper)+"\"' -WindowStyle Hidden -PassThru\n"+installer.read_text())
        data=json.loads(Path(manifest).read_text())
        data['installerSha256']=sha(installer)
        Path(manifest).write_text(json.dumps(data))
        try:
            self.w.deploy(manifest)
            self.assertFalse(done.exists(),'Background process ended before lock inheritance could be checked')
            with workflow.FileLock(self.w.state/'deployment.lock',timeout=0):
                pass
        finally:
            deadline=time.monotonic()+10
            while not done.exists() and time.monotonic()<deadline:
                time.sleep(0.05)
        self.assertTrue(done.exists())

if __name__=='__main__':
    unittest.main(verbosity=2)
