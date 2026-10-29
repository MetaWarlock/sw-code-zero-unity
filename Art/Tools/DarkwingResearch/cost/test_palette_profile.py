"""Integration controls for paletteprofile; synthetic original records are never goldens."""
import csv
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = next(p for p in Path(__file__).resolve().parents if (p / 'ProjectSettings').is_dir())
PROJECT = ROOT / 'Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj'


class PaletteProfileTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (ROOT / 'Art/Temp').mkdir(parents=True, exist_ok=True)
        result = subprocess.run(['dotnet', 'build', str(PROJECT), '-c', 'Release', '--nologo'],
                                cwd=ROOT, capture_output=True, text=True, timeout=90)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='palette-profile-test-', dir=ROOT / 'Art/Temp')
        self.addCleanup(self.temp.cleanup)
        self.session = Path(self.temp.name)
        (self.session / 'input.bin').write_bytes(bytes(3))
        (self.session / 'replay1').mkdir()
        self.capture = self.session / 'replay1/buffers.bin'
        self.capture.write_bytes(bytes(3 * 4384))

    def run_profile(self, first=2, last=2, name='profile.csv'):
        output = self.session / name
        result = subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT),
                                 '-c', 'Release', '--', 'paletteprofile', str(first), str(last),
                                 str(output), '--session', str(self.session)],
                                cwd=ROOT, capture_output=True, text=True, timeout=90)
        return result, output

    def test_real_neighbours_and_no_original_state_seed(self):
        first, path = self.run_profile(name='zeros.csv')
        self.assertEqual(first.returncode, 0, first.stderr)
        with path.open() as handle:
            before = list(csv.DictReader(handle))
        self.assertEqual([int(r['poll']) for r in before], [1, 2, 3])
        self.capture.write_bytes(bytes([255]) * (3 * 4384))
        second, path = self.run_profile(name='changed-original.csv')
        self.assertEqual(second.returncode, 0, second.stderr)
        with path.open() as handle:
            after = list(csv.DictReader(handle))
        for a, b in zip(before, after):
            self.assertNotEqual(a['original0300'], b['original0300'])
            self.assertEqual({k: v for k, v in a.items() if k.startswith('native')},
                             {k: v for k, v in b.items() if k.startswith('native')})
            self.assertEqual(len(a['native3f00']), 64)

    def test_missing_capture_rejected_without_output(self):
        self.capture.unlink()
        result, output = self.run_profile()
        self.assertEqual(result.returncode, 2)
        self.assertFalse(output.exists())

    def test_incomplete_capture_rejected_without_output(self):
        self.capture.write_bytes(bytes(3 * 4384 - 1))
        result, output = self.run_profile()
        self.assertEqual(result.returncode, 2)
        self.assertIn('capture length', result.stderr)
        self.assertFalse(output.exists())

    def test_impossible_or_missing_neighbour_rejected(self):
        for first, last in [(1, 2), (2, 3), (3, 2)]:
            with self.subTest(first=first, last=last):
                result, output = self.run_profile(first, last)
                self.assertEqual(result.returncode, 2)
                self.assertFalse(output.exists())

    def test_existing_output_preserved(self):
        output = self.session / 'profile.csv'
        output.write_bytes(b'preserve this output')
        result, _ = self.run_profile()
        self.assertEqual(result.returncode, 2)
        self.assertEqual(output.read_bytes(), b'preserve this output')


if __name__ == '__main__':
    unittest.main()
