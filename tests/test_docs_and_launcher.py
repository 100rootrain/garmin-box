from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]


class BeginnerGuideTests(unittest.TestCase):
    def test_readme_leads_with_the_exe_setup(self):
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
        for heading in ("## 준비물", "## 1. 설치하기", "## 2. 매일 자동 실행", "## 3. 다시 열기", "## 고급 설정"):
            self.assertIn(heading, readme)
        self.assertLess(readme.index("GarminBox.exe"), readme.index("## 고급 설정"))
        linux = readme.split("### Linux / OCI", 1)[1].split("### 개발", 1)[0]
        self.assertIn("garmin_activities.db", linux)
        self.assertLess(linux.index('source "$HOME/.garmin-box.env"'), linux.index(".venv/bin/python sync_supa.py"))

    def test_windows_launchers_call_the_installed_exe(self):
        vbs = (ROOT / "run_garmin_batch_hidden.vbs").read_text(encoding="utf-8")
        ps1 = (ROOT / "run_garmin_batch.ps1").read_text(encoding="utf-8")
        for launcher in (vbs, ps1):
            self.assertIn("garmin-box\\GarminBox.exe", launcher)
            self.assertIn("--sync", launcher)
            self.assertNotIn("C:\\Users\\qoreh", launcher)
        code = [line for line in vbs.splitlines() if not line.lstrip().startswith("'")]
        self.assertTrue(all(line.isascii() for line in code))   # WSH는 UTF-8을 읽지 못해 한글 MsgBox가 깨진다
        self.assertNotIn("Start-Transcript", ps1)          # 정제 전 출력을 파일에 남기지 않는다
        self.assertNotIn("garmin_tokens.json", ps1)        # 토큰 삭제 재시도는 EXE 경로에서 제거

    def test_english_readme_accepts_uri_dsn(self):
        self.assertNotIn("not a URL", (ROOT / "README.en.md").read_text(encoding="utf-8"))
