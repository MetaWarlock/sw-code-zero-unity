"""Negative controls for the existing static-ROM sprite exporter, not runtime acceptance."""
import contextlib
import importlib.util
import io
from pathlib import Path
import shutil
import tempfile
import unittest

from PIL import Image

ART = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("gdd_sprite_export", ART / "Docs/GameDesignDocument/export_sprites.py")
export = importlib.util.module_from_spec(spec)
spec.loader.exec_module(export)


class SpriteExportControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tiles, cls.banks = export.load_resources()
        cls.original_output = export.OUTPUT_DIR

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(dir=ART / "Temp", prefix="sprite-controls-")
        self.output = Path(self.folder.name)
        shutil.copytree(self.original_output, self.output, dirs_exist_ok=True)
        export.OUTPUT_DIR = self.output

    def tearDown(self):
        export.OUTPUT_DIR = self.original_output
        self.folder.cleanup()

    def verify(self):
        with contextlib.redirect_stdout(io.StringIO()):
            export.verify_existing_strips(self.tiles, self.banks)

    def test_original_documentation_export(self):
        self.verify()

    def test_missing_strip_rejected(self):
        (self.output / export.ANIMATIONS_TO_EXPORT[0][1]).unlink()
        with self.assertRaisesRegex(ValueError, "Missing or extra"):
            self.verify()

    def test_extra_strip_rejected(self):
        shutil.copyfile(self.output / export.ANIMATIONS_TO_EXPORT[0][1], self.output / "anim_FF_extra.png")
        with self.assertRaisesRegex(ValueError, "Missing or extra"):
            self.verify()

    def test_pixel_mutant_rejected(self):
        path = self.output / export.ANIMATIONS_TO_EXPORT[0][1]
        with Image.open(path) as source:
            mutant = source.convert("RGBA")
        mutant.putpixel((24, 24), (1, 2, 3, 255))
        mutant.save(path)
        with self.assertRaisesRegex(ValueError, "RGBA byte mismatch"):
            self.verify()

    def test_manifest_mutant_rejected(self):
        path = self.output / "manifest.json"
        data = export.json.loads(path.read_text(encoding="utf-8"))
        data["animations"][0]["chrBank4KiB"] ^= 1
        path.write_text(export.json.dumps(data), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "table context"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
