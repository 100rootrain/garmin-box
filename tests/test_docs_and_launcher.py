from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]


class BeginnerGuideTests(unittest.TestCase):
    def test_readme_and_launcher_support_a_beginner_windows_setup(self):
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
        launcher = (ROOT / "run_garmin_batch_hidden.vbs").read_text(encoding="utf-8")
        for heading in ("## 준비물", "## 1. 설치하기", "## 4. 처음 실행하기", "## 5. 매일 자동으로 실행하기", "## 고급 설정"):
            self.assertIn(heading, readme)
        linux = readme.split("### Linux / OCI", 1)[1].split("### 개발", 1)[0]
        self.assertIn("garmin_activities.db", linux)
        self.assertLess(linux.index('source "$HOME/.garmin-box.env"'), linux.index(".venv/bin/python sync_supa.py"))
        self.assertIn("GetParentFolderName(WScript.ScriptFullName)", launcher)
        self.assertNotIn("C:\\Users\\qoreh", launcher)
