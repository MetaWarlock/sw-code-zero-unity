import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
import subprocess
import zipfile
from unittest.mock import patch
import preserve
from retire_backups import child, info, prepare, restore_file, verify_catalog, walk


class RetentionTests(unittest.TestCase):
    def setUp(self):
        scratch = Path(__file__).resolve().parents[2] / 'Temp'
        scratch.mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=scratch)
        self.root = Path(self.temp.name).resolve()
        for name in ('keep1', 'keep2', 'old'):
            folder = self.root / name
            folder.mkdir()
            (folder / 'input.bin').write_bytes(b'irreplaceable recording')
            data = {'schema': 1, 'complete': True, 'files': {'input.bin': info(folder / 'input.bin')}}
            (folder / 'manifest.json').write_text(json.dumps(data))

    def tearDown(self):
        self.temp.cleanup()

    def prepare(self):
        with contextlib.redirect_stdout(io.StringIO()):
            return prepare(self.root, ['keep1', 'keep2'], ['old'], 'saved', self.root)

    def test_dedup_and_unique_survive_original_removal(self):
        unique = self.root / 'old' / 'unique.bin'
        unique.write_bytes(b'unique source WIP')
        manifest = self.root / 'old' / 'manifest.json'
        data = json.loads(manifest.read_text())
        data['files']['unique.bin'] = info(unique)
        manifest.write_text(json.dumps(data))
        result = self.prepare()
        saved = self.root / result['covered'][info(unique)['sha256']]['path']
        unique.unlink()
        self.assertEqual(saved.read_bytes(), b'unique source WIP')
        self.assertNotIn(info(self.root / 'keep1' / 'input.bin')['sha256'], result['objects'])
        self.assertTrue(result['complete'])
        self.assertTrue((self.root / 'old').is_dir(), 'preparation must never delete old folders')

    def test_tampered_keeper_refused(self):
        (self.root / 'keep1' / 'input.bin').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'Manifest mismatch'):
            self.prepare()

    def test_unexpected_retired_file_refused(self):
        (self.root / 'old' / 'surprise.bin').write_bytes(b'new file')
        with self.assertRaisesRegex(ValueError, 'Unexpected/missing'):
            self.prepare()

    def test_outside_or_nested_target_refused(self):
        for name in ('..', '../outside', 'old/input.bin'):
            with self.assertRaises(ValueError):
                child(self.root, name)

    def test_overlap_refused(self):
        with self.assertRaises(ValueError):
            prepare(self.root, ['keep1', 'keep2'], ['keep1'], 'saved', self.root)

    def test_existing_output_refused(self):
        (self.root / 'saved').mkdir()
        with self.assertRaises(ValueError):
            self.prepare()

    def test_fourth_full_snapshot_refused_before_copying(self):
        with patch.object(preserve, 'ROOT', self.root / 'project'):
            with self.assertRaisesRegex(ValueError, 'Three full snapshots'):
                preserve.snapshot(self.root / 'fourth')
        self.assertFalse((self.root / 'fourth').exists())

    def test_incomplete_full_snapshot_refused(self):
        manifest = self.root / 'old/manifest.json'
        data = json.loads(manifest.read_text())
        data['complete'] = False
        manifest.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'Incomplete snapshot'):
            self.prepare()

    def test_failed_preparation_can_resume_without_deleting_sources(self):
        path = self.root / 'old/surprise.bin'
        path.write_bytes(b'surprise')
        with self.assertRaises(ValueError):
            self.prepare()
        path.unlink()
        result = self.prepare()
        self.assertTrue(result['complete'])
        self.assertTrue((self.root / 'old/input.bin').exists())

    def test_restore_refuses_overwrite_and_verifies_bytes(self):
        self.prepare()
        catalog = self.root / 'saved/catalog.json'
        target = self.root / 'recovered.bin'
        self.assertEqual(restore_file(catalog, 'old', 'input.bin', target)['result'], 'PASS')
        self.assertEqual(target.read_bytes(), b'irreplaceable recording')
        with self.assertRaises(ValueError):
            restore_file(catalog, 'old', 'input.bin', target)
        (self.root / 'keep1/input.bin').write_bytes(b'tampered')
        with self.assertRaises(ValueError):
            verify_catalog(catalog, self.root)

    def test_git_history_consolidation_and_verification(self):
        repo = self.root / 'repo'
        repo.mkdir()
        def git(*args):
            return subprocess.check_output(['git', '-C', str(repo), *args], stderr=subprocess.DEVNULL)
        git('init')
        (repo / 'source.txt').write_text('historical source')
        git('add', 'source.txt')
        git('-c', 'user.name=Retention fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'fixture')
        bundle = self.root / 'old/git-history.bundle'
        git('bundle', 'create', str(bundle), '--all')
        manifest = self.root / 'old/manifest.json'
        data = json.loads(manifest.read_text())
        data['files']['git-history.bundle'] = info(bundle)
        manifest.write_text(json.dumps(data))
        with contextlib.redirect_stdout(io.StringIO()):
            result = prepare(self.root, ['keep1', 'keep2'], ['old'], 'saved', repo)
        bundle.unlink()
        self.assertEqual(result['history']['heads'], 1)
        self.assertEqual(verify_catalog(self.root / 'saved/catalog.json', repo)['result'], 'PASS')

    def test_pilot_zip_expanded_without_new_zip(self):
        archive = self.root / '_archive'
        archive.mkdir()
        bundle = archive / '20261031-ZERO-74-qoder-mm5sound-final.zip'
        with zipfile.ZipFile(bundle, 'w') as output:
            output.writestr('unique.bin', b'unique pilot data')
            data = {'complete': True, 'files': {'unique.bin': {'bytes': 17, 'sha256': __import__('hashlib').sha256(b'unique pilot data').hexdigest()}}}
            output.writestr('manifest.json', json.dumps(data))
        with contextlib.redirect_stdout(io.StringIO()):
            result = prepare(self.root, ['keep1', 'keep2'], ['old', '_archive'], 'saved', self.root)
        self.assertEqual(verify_catalog(self.root / 'saved/catalog.json', self.root)['result'], 'PASS')
        self.assertTrue(result['snapshots']['_archive']['expandedZips'])
        self.assertEqual(list((self.root / 'saved').rglob('*.zip')), [])

    def test_link_refused(self):
        link = self.root / 'old' / 'linked'
        try:
            link.symlink_to(self.root / 'keep1', target_is_directory=True)
        except OSError as exc:
            self.skipTest(str(exc))
        with self.assertRaises(ValueError):
            walk(self.root / 'old')


if __name__ == '__main__':
    unittest.main()
