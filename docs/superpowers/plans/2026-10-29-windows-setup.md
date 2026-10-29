# garmin-box Windows 최초 설정 프로그램 구현 계획

> **에이전트 작업자용:** 필수 하위 스킬: superpowers:subagent-driven-development(권장) 또는 superpowers:executing-plans로 작업 단위로 실행한다. 단계는 체크박스(`- [ ]`)로 추적한다.

**목표:** EXE 하나로 환경 준비 → 정보 입력 → 연결 확인 → 첫 동기화 → 매일 자동 실행 등록을 끝내고, 재실행 시 설정 변경·지금 동기화·최근 결과 확인을 제공한다.

**아키텍처:** C# WinForms EXE(`GarminBox.exe`)가 설치·입력·잠금·Job·예약을 맡고, 수집·판정은 새 Python 진입점 `run_batch.py` 하나가 맡는다. EXE는 앱 전용 portable Python으로 `run_batch.py`를 `-I -X utf8 --await-start`로 실행하고, Job 연결 뒤 `START` 한 줄을 보낸다. `run_batch.py`는 GarminDB CLI·`sync_supa.py`·`update_gist.py`를 절대 경로 하위 프로세스로 실행하고, 원격 ID 집합이 SQLite에 모두 있을 때만 업로드한다.

**기술 스택:** .NET 10 WinForms(`net10.0-windows`, self-contained 단일 파일), xUnit, CPython 3.13.18(python-build-standalone 20261026), GarminDB 3.9.0, garminconnect 0.3.11, psycopg 3.3.6, Windows Task Scheduler COM(`Schedule.Service`), Job Object P/Invoke.

**근거 문서:** [설계](../specs/2026-10-29-windows-setup-design.md), [실행 검증 기록](../verification/2026-10-29-windows-verification.md). 설계와 이 계획이 다르면 설계를 따르고 이 계획을 고친다.

---

## 소스에서 확인한 사실 (계획의 전제)

번들의 GarminDB 3.9.0·garminconnect 0.3.11 소스로 확인했다. 실행 검증이 아니므로 작업 15에서 실제로 확인한다.

- `garmindb_cli.py`는 로그인 실패 시 `sys.exit()`로 **종료 코드 0**을 낸다. 활동 목록·상세·FIT 오류도 로그만 남긴다. → 0이 아닌 종료 코드는 즉시 실패로, 0이어도 아래 로그인 표식과 SQLite ID 대조를 모두 통과해야 완료로 본다.
- `Download.login()`은 로그인에 성공할 때마다 프로필 폴더(`gc_config.get_fit_files_dir()`)의 `user-settings.json`을 덮어쓴다. → CLI 실행 전후로 이 파일의 수정 시각이 바뀌지 않았으면 0으로 끝난 로그인 실패로 본다.
- 로그인 뒤의 목록·요약·import 실패(`__get_activity_summaries`는 `GarminConnectAuthError`를 로그만 남기고 빈 목록으로 끝난다)는 따로 판별하지 않는다. 새 활동이 있으면 원격 ID 대조에서 누락돼 `incomplete`로 막힌다. 새 활동이 없으면 `--overwrite` 없는 GarminDB는 이미 받은 활동을 다시 받지 않으므로, 업로드가 읽는 `activities` 테이블은 정상 실행과 같다. 업로드 대상을 `activities` 밖(상세·FIT 기반 표)으로 넓히면 이 전제를 다시 확인한다.
- `GarminConnectAuthAdapter(gc_config, garmin_factory=None, mfa_prompt=None)`는 토큰 파일(`gc_config.get_token_store_file()` = `~/.GarminDb/garmin_tokens.json`)로 먼저 로그인하고, 실패하면 자격증명으로 폴백하며, garminconnect 예외를 `GarminConnectAuthError`로 감싼다. 어댑터는 `gc_config`의 `get_user/get_password/get_token_store_file/get_garmin_base_domain`만 쓴다.
- garminconnect 0.3.11 `Garmin.login(path)`는 path가 없으면 자격증명 로그인 후 그 path에 토큰을 쓰고(실패는 무시), API가 캐시 토큰을 거부하면 자격증명으로 다시 로그인한다.
- 다운로드 개수는 설정의 `data.download_latest_activities`(`--latest`)와 `data.download_all_activities`(없으면)로 정해진다. 활동 목록 URL은 `Download.garmin_connect_activity_search_url`.
- `GarminConnectConfigManager`는 설정 파일이 없거나 깨졌으면 `sys.exit(-1)`한다.
- 거리 단위는 GarminDB 설정이 아니라 Garmin 계정 설정에서 import 때 정한다. 설정 파일의 `settings.metric`은 바꾸지 않는다.
- CLI는 현재 폴더에 원문 `garmindb.log`를 쓴다. → CLI는 임시 폴더를 cwd로 실행하고 끝나면 지운다.

## 공통 약속

| 항목 | 값 |
|---|---|
| `run_batch.py` 모드 | `check`(연결 확인, 토큰 캐시 외 변경 없음) / `setup`(테이블 생성 + 최초 전체 수집) / `sync`(매일 수집) |
| 추가 인자 | `--await-start`(EXE만 넘김), `--no-prompt`(MFA가 필요하면 기다리지 않고 재인증 필요로 종료) |
| 종료 코드 | `0` 성공, `1` 실패, `3` 시작 신호 없음, `4` Garmin 재인증 필요 (`2`는 argparse 오류와 겹쳐 쓰지 않음) |
| stdout | 한 줄 JSON 이벤트: `progress{stage,message}`, `warning{message}`, `mfa_required`, `check_result{garmin,postgres,github}`, `done{activities,gist_url}`, `failed{kind,message}` |
| `failed.kind` | `input`, `not_configured`, `config`, `account`, `auth`, `reauth`, `rate_limit`, `network`, `garmin`, `incomplete`, `schema`, `supabase`, `gist`, `permission`, `timeout`, `unexpected` |
| 앱 상태 | `%LOCALAPPDATA%\garmin-box\settings.json` = `{"schedule_time":"09:10","config_complete":false}` (C# 작성), `status.json` = `{"finished_at","mode","result":"success|failed|reauth","message","warnings",...}` (Python 작성, 시간 초과 시 C#) |
| 경로 기준 | C#과 Python 모두 환경 변수 `USERPROFILE`·`LOCALAPPDATA`를 기준으로 한다 (테스트가 임시 폴더로 바꿀 수 있게) |
| Garmin 토큰 | 운영 `~/.GarminDb/garmin_tokens.json`, 연결 확인용 `~/.GarminDb/garmin_tokens.check.json`. 확인용 토큰은 EXE가 설정을 저장할 때만 운영 토큰으로 바꾸고, 확인 결과를 버리면 지운다 |

공통 명령 (저장소 루트, PowerShell). `-I`는 저장소 루트를 `sys.path`에서 빼므로 `tests/__init__.py`를 두고 `discover -t .`로 루트를 넣는다. `-m unittest tests.xxx` 형태는 쓰지 않는다.

```powershell
$PY = (Resolve-Path build\out\runtime\python\python.exe)                       # 작업 1에서 만든다
& $PY -I -X utf8 -m unittest discover -s tests -t . -v                         # Python 테스트 전체
& $PY -I -X utf8 -m unittest discover -s tests -t . -p test_run_batch.py -v    # 파일 하나
dotnet test GarminBox.slnx                                                      # C# 테스트 (작업 7부터)
```

저장소 루트의 `test_update_gist.py`(pytest 형식 함수)는 위 명령에 포함되지 않고 `tools/verify_windows_bundle.py`가 실행한다.

## 파일 구조

| 파일 | 책임 |
|---|---|
| `build/windows-requirements.lock` | 배포 승인 의존성 목록 (검증 목록에서 시작, 검증 목록과 별도) |
| `build/build-runtime.ps1` | 런타임 내려받기·해시 확인·의존성 설치 → `build/out/runtime` |
| `build/release.ps1` | payload ZIP·해시, 다른 경로 번들 검증, 전체 테스트, self-contained 단일 EXE publish |
| `run_batch.py` (신규) | 수집 진입점: 시작 신호, 설정 확인, Garmin 로그인·MFA 플래그, 원격 목록·SQLite 대조·보충, 업로드·gist, 상태·정제 로그 |
| `sync_supa.py` (수정) | 원격 0건 확인 시에만 0행 동기화 허용, 연결 제한 시간 |
| `update_gist.py` (수정) | 연결·요청 제한 시간 |
| `tests/__init__.py` (신규, 빈 파일) | `-I` 모드 unittest discovery가 `tests`를 패키지로 import하게 한다 |
| `tests/test_run_batch.py` (신규) | 진입점 판정 로직·프로세스 경계 테스트 |
| `tests/test_docs_and_launcher.py` (수정) | 새 README·런처 내용 확인 |
| `run_garmin_batch.ps1`, `run_garmin_batch_hidden.vbs` (수정) | 설치된 EXE의 `--sync`를 부르는 수동 런처 |
| `tools/verify_windows_bundle.py` (수정) | `tests/test_*.py` 전체를 번들로 실행 |
| `GarminBox.slnx` | 솔루션 |
| `src/GarminBox/Program.cs` | 인자 분기, 사전점검, `--sync [--scheduled]` |
| `src/GarminBox/Paths.cs` | 고정 경로, 비 ASCII·OS·쓰기 사전점검 |
| `src/GarminBox/AppLock.cs` | `Local\garmin-box` named mutex |
| `src/GarminBox/ConfigFiles.cs` | `.env`·Garmin 설정 병합·`settings.json`·`status.json`·원자적 저장 |
| `src/GarminBox/SecretAcl.cs` | 비밀 파일·토큰 폴더 ACL |
| `src/GarminBox/PythonRunner.cs` | `Process` + Job Object + 시작 신호 + 이벤트·MFA |
| `src/GarminBox/Installer.cs` | payload 무결성·압축 해제·EXE 복사·시작 메뉴 |
| `src/GarminBox/Scheduler.cs` | 작업 XML 생성·검증·기존 작업 탐지(순수) + COM 등록 |
| `src/GarminBox/MainForm.cs` | 입력 화면과 흐름 |
| `src/GarminBox.Tests/*` | xUnit 테스트, `runner_fixture.py` |

---

### 작업 1: 배포 의존성 잠금과 런타임 빌드

**파일:** 생성 `build/windows-requirements.lock`, `build/build-runtime.ps1`, `tests/__init__.py`(빈 파일) · 수정 `.gitignore`

- [ ] **1단계: 잠금 파일 만들기**

`docs/superpowers/verification/windows-runtime-requirements.txt`를 복사하고 첫 줄에 주석을 단다.

```text
# 배포 승인 목록. 검증 기록의 windows-runtime-requirements.txt에서 시작했다. 바꾸면 Windows 번들 검증을 다시 한다.
cached-property==2.0.1
... (검증 목록 26줄 그대로)
```

- [ ] **2단계: `build/build-runtime.ps1` 작성**

```powershell
# 배포용 Python 런타임을 만든다: 고정 배포본 내려받기 → SHA-256 확인 → 잠금 목록 설치. 출력: build\out\runtime\python
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'out'
$name = 'cpython-3.13.18+20261026-x86_64-pc-windows-msvc-install_only_stripped.tar.gz'
$url = "https://github.com/astral-sh/python-build-standalone/releases/download/20261026/$($name.Replace('+', '%2B'))"
$sha = '7369f7a35f261222a6ac8b3a87c1c88eeebee8af945d85346202591a64038dd3'

New-Item -ItemType Directory -Force $out | Out-Null
$archive = Join-Path $out $name
if (-not (Test-Path $archive)) { Invoke-WebRequest $url -OutFile $archive }
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $sha) { throw "runtime SHA-256 mismatch: $archive" }

$runtime = Join-Path $out 'runtime'
if (Test-Path $runtime) { Remove-Item -Recurse -Force $runtime }
New-Item -ItemType Directory $runtime | Out-Null
tar -xzf $archive -C $runtime
$py = Join-Path $runtime 'python\python.exe'
# shortcut: 버전만 고정하고 wheel 해시는 고정하지 않음. 공급망 요구가 생기면 --require-hashes 잠금으로 바꾼다.
& $py -I -m pip install --no-deps --no-warn-script-location -r (Join-Path $PSScriptRoot 'windows-requirements.lock')
if ($LASTEXITCODE) { throw 'pip install failed' }
& $py -I -m pip --isolated check
if ($LASTEXITCODE) { throw 'pip check failed' }
```

- [ ] **3단계: `.gitignore`에 빌드 산출물 추가**

```text
build/out/
src/GarminBox/payload/
bin/
obj/
```

- [ ] **4단계: 실행해 확인**

실행: `powershell -ExecutionPolicy Bypass -File build\build-runtime.ps1` 후 `& build\out\runtime\python\python.exe -I -c "import garmindb, garminconnect, psycopg; print('ok')"`
기대: `ok`. 빈 `tests/__init__.py`를 만든 뒤 기존 테스트도 이 Python으로 통과: `& $PY -I -X utf8 -m unittest discover -s tests -t . -v` → `OK` (`__init__.py`가 없으면 `Start directory is not importable`).

- [ ] **5단계: 커밋** — `git add build .gitignore` · `chore: Windows 배포 런타임 빌드 스크립트와 의존성 잠금`

---

### 작업 2: `run_batch.py` 기반 — 시작 신호·이벤트·`.env`·비밀값 제거·설정 확인

**파일:** 생성 `run_batch.py`, `tests/test_run_batch.py`

- [ ] **1단계: 실패하는 테스트 작성** (`tests/test_run_batch.py`)

```python
"""run_batch.py 판정 로직 검사. 외부 서비스는 테스트 대역으로 대신한다."""
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

import run_batch
from run_batch import Failure

ROOT = Path(__file__).resolve().parents[1]


class StartSignalTests(unittest.TestCase):
    def start(self, args, stdin_text):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as home:
            env = {**os.environ, "USERPROFILE": home, "LOCALAPPDATA": home}
            stdin = {"input": stdin_text} if stdin_text is not None else {"stdin": subprocess.DEVNULL}
            done = subprocess.run([sys.executable, "-I", "-X", "utf8", str(ROOT / "run_batch.py"), *args],
                                  env=env, capture_output=True, text=True, encoding="utf-8", timeout=60, **stdin)
            return done, (Path(home) / "garmin-box").exists()

    def test_eof_before_start_does_nothing(self):
        done, touched = self.start(["--await-start", "sync"], None)
        self.assertEqual(done.returncode, run_batch.EXIT_NOT_STARTED)
        self.assertFalse(touched)

    def test_other_first_line_does_nothing(self):
        done, touched = self.start(["--await-start", "sync"], "123456\n")
        self.assertEqual(done.returncode, run_batch.EXIT_NOT_STARTED)
        self.assertFalse(touched)

    def test_start_line_runs_and_blocks_unconfigured_sync(self):
        done, touched = self.start(["--await-start", "sync"], "START\n")
        self.assertEqual(done.returncode, run_batch.EXIT_FAIL)
        self.assertEqual(json.loads(done.stdout.splitlines()[-1])["kind"], "not_configured")
        self.assertTrue(touched)

    def test_without_flag_does_not_wait_for_start(self):
        done, _ = self.start(["sync"], None)
        self.assertEqual(done.returncode, run_batch.EXIT_FAIL)


class EnvAndRedactionTests(unittest.TestCase):
    def test_env_file_keeps_equals_unicode_and_empty(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / ".garmin-box.env"
            path.write_text("SUPA_DSN=host=db password='a=b' 한글\n# memo\nGIST_FILENAME=\n=broken\n", encoding="utf-8")
            self.assertEqual(run_batch.read_env_file(path),
                             {"SUPA_DSN": "host=db password='a=b' 한글", "GIST_FILENAME": ""})

    def test_redacts_known_and_shaped_secrets(self):
        redact = run_batch.make_redactor(["ghp_secretvalue", "pa=ss"])
        text = redact("postgresql://postgres:pw0rd@db.x.co:5432/postgres ghp_secretvalue host=db password='a b' "
                      "?ticket=ST-123&x=1 Authorization: Bearer abc.def pa=ss")
        for leaked in ("pw0rd", "ghp_secretvalue", "'a b'", "ST-123", "abc.def", "pa=ss"):
            self.assertNotIn(leaked, text)

    def test_log_file_gets_only_redacted_text(self):
        with tempfile.TemporaryDirectory() as tmp:
            log = run_batch.Log(Path(tmp), run_batch.make_redactor(["ghp_secretvalue"]))
            log("token ghp_secretvalue")
            log.file.close()
            written = next(Path(tmp).glob("*.log")).read_text(encoding="utf-8")
        self.assertIn("***", written)
        self.assertNotIn("ghp_secretvalue", written)
```

- [ ] **2단계: 실패 확인** — `& $PY -I -X utf8 -m unittest discover -s tests -t . -p test_run_batch.py -v` → `ModuleNotFoundError: No module named 'run_batch'`

- [ ] **3단계: `run_batch.py` 기반 구현**

```python
#!/usr/bin/env python3
"""garmin-box 수집 진입점. Windows에서는 GarminBox.exe가 `-I -X utf8 --await-start`로 실행한다.

모드: check(연결 확인) / setup(테이블 생성 + 최초 전체 수집) / sync(매일 수집).
stdout은 한 줄 JSON 이벤트, 종료 코드는 EXIT_*. 파일 로그는 비밀값을 지운 뒤에만 쓴다.
"""
import argparse
import json
import os
import re
import sqlite3
import subprocess
import sys
import tempfile
import threading
import traceback
from contextlib import closing, suppress
from datetime import datetime
from pathlib import Path

EXIT_OK, EXIT_FAIL, EXIT_NOT_STARTED, EXIT_REAUTH = 0, 1, 3, 4
HERE = Path(__file__).resolve().parent
GARMINDB_DIR = Path.home() / ".GarminDb"
GARMIN_CONFIG = GARMINDB_DIR / "GarminConnectConfig.json"
TOKEN_FILE = GARMINDB_DIR / "garmin_tokens.json"         # GarminConnectConfigManager.get_token_store_file()과 같은 경로
CHECK_TOKEN = GARMINDB_DIR / "garmin_tokens.check.json"  # 연결 확인용. 운영 토큰 교체는 EXE가 저장할 때만 한다
ENV_FILE = Path.home() / ".garmin-box.env"
ENV_KEYS = ("SUPA_DSN", "GITHUB_TOKEN", "GIST_ID", "GIST_FILENAME")
REAUTH_MESSAGE = "Garmin 재인증이 필요합니다. garmin-box를 열어 비밀번호 칸을 비운 채 연결 확인을 누르세요."

REDACT = lambda text: text  # main()에서 실제 비밀값으로 바꾼다
WARNINGS = []


class Failure(Exception):
    """사용자에게 보여 줄 실패. kind는 UI가 원인을 구분하는 값."""

    def __init__(self, kind, message):
        super().__init__(message)
        self.kind = kind


def emit(event, **data):
    data = {key: REDACT(value) if isinstance(value, str) else value for key, value in data.items()}
    print(json.dumps({"event": event, **data}, ensure_ascii=False), flush=True)


def warn(message):
    WARNINGS.append(message)
    emit("warning", message=message)


def read_env_file(path):
    """KEY=VALUE, 첫 '='만 구분자 (SUPA_DSN 안의 '=' 보존). 기존 run_garmin_batch.ps1과 같은 규칙."""
    values = {}
    if path.is_file():
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            line = line.strip()
            i = line.find("=")
            if line.startswith("#") or i < 1:
                continue
            values[line[:i].strip()] = line[i + 1:]
    return values


_SECRET_PATTERNS = (
    (re.compile(r"(postgres(?:ql)?://[^:/@\s]+:)[^@\s]+@"), r"\1***@"),
    (re.compile(r"(?i)\b(password|passwd|access_token|refresh_token|token|ticket|code)=('[^']*'|[^\s&\"']+)"), r"\1=***"),
    (re.compile(r"(?i)\b(bearer)\s+\S+"), r"\1 ***"),
)


def make_redactor(secrets):
    """알려진 비밀값은 그대로, 형식이 드러나는 비밀값(DSN·URL 쿼리·Bearer)은 패턴으로 지운다."""
    exact = sorted({s for s in secrets if s and len(s) >= 4}, key=len, reverse=True)

    def redact(text):
        for secret in exact:
            text = text.replace(secret, "***")
        for pattern, replacement in _SECRET_PATTERNS:
            text = pattern.sub(replacement, text)
        return text
    return redact


def secrets_of(values):
    found = [values.get("SUPA_DSN"), values.get("GITHUB_TOKEN"), os.environ.get("GARMIN_PASSWORD")]
    with suppress(Exception):
        found.append(json.loads(GARMIN_CONFIG.read_text(encoding="utf-8"))["credentials"]["password"])
    with suppress(Exception):
        from psycopg.conninfo import conninfo_to_dict
        found.append(conninfo_to_dict(values["SUPA_DSN"]).get("password"))
    return found


class Log:
    """비밀값을 지운 줄만 파일에 쓴다. 원문을 먼저 쓰고 나중에 가리지 않는다."""

    def __init__(self, directory, redact):
        directory.mkdir(parents=True, exist_ok=True)
        # shortcut: 날짜별 파일을 지우지 않는다(하루 수 KB). 오래 쓰면 보관 개수 제한을 넣는다.
        self.file = open(directory / f"{datetime.now():%Y%m%d}.log", "a", encoding="utf-8")
        self.redact = redact

    def __call__(self, text):
        self.file.write(f"{datetime.now():%H:%M:%S} {self.redact(text)}\n")
        self.file.flush()


def need(values, key, label):
    if not values.get(key):
        raise Failure("input", f"{label}을(를) 입력하세요.")
    return values[key]


def require_config_complete(state_dir):
    try:
        settings = json.loads((state_dir / "settings.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        settings = {}
    if settings.get("config_complete") is not True:
        raise Failure("not_configured", "설정이 완료되지 않았습니다. garmin-box를 열어 설정을 저장하세요.")


def write_status(state_dir, mode, result, message, **extra):
    status = {"finished_at": datetime.now().astimezone().isoformat(timespec="seconds"), "mode": mode,
              "result": result, "message": message, "warnings": WARNINGS, **extra}
    path = state_dir / "status.json"
    temp = path.with_name("status.json.tmp")
    temp.write_text(json.dumps(status, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temp, path)


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    # EXE가 Job 연결을 마친 뒤에만 시작한다. 그 전에는 앱 import·네트워크·설정 변경·자식 실행을 하지 않는다.
    if "--await-start" in argv and sys.stdin.readline() != "START\n":
        return EXIT_NOT_STARTED
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("check", "setup", "sync"))
    parser.add_argument("--await-start", action="store_true")
    parser.add_argument("--no-prompt", action="store_true")
    args = parser.parse_args(argv)

    global REDACT
    state_dir = Path(os.environ["LOCALAPPDATA"]) / "garmin-box"
    values = {**read_env_file(ENV_FILE), **{k: os.environ[k] for k in ENV_KEYS if os.environ.get(k)}}
    REDACT = make_redactor(secrets_of(values))
    log = Log(state_dir / "logs", REDACT)
    log(f"start {args.mode}")
    try:
        if args.mode == "check":
            return check(values)
        require_config_complete(state_dir)
        summary = sync(args.mode, values, not args.no_prompt, log)
        write_status(state_dir, args.mode, "success", "동기화 완료", **summary)
        emit("done", **summary)
        log(f"done {summary}")
        return EXIT_OK
    except Failure as error:
        failure = error
    except Exception as error:
        log(traceback.format_exc())
        failure = Failure("unexpected", f"{type(error).__name__}: {error}")
    emit("failed", kind=failure.kind, message=str(failure))
    log(f"failed {failure.kind}: {failure}")
    if args.mode != "check":
        write_status(state_dir, args.mode, "reauth" if failure.kind == "reauth" else "failed", REDACT(str(failure)))
    return EXIT_REAUTH if failure.kind == "reauth" else EXIT_FAIL


if __name__ == "__main__":
    sys.exit(main())
```

`check`·`sync`는 작업 4~6에서 채운다. 이 단계에서는 테스트가 import만 하도록 두 함수를 `raise NotImplementedError`로 둔다. 이후 작업의 함수는 모두 `main()`과 `if __name__ == "__main__":` 위에 추가한다.

- [ ] **4단계: 통과 확인** — 같은 명령 → `OK`
- [ ] **5단계: 커밋** — `feat: run_batch 진입점의 시작 신호·이벤트·로그 정제`

---

### 작업 3: 하위 프로세스 실행 (`-I`, 시간 제한, stdin 닫기)

**파일:** 수정 `run_batch.py`, `tests/test_run_batch.py`

- [ ] **1단계: 실패하는 테스트**

```python
class ChildProcessTests(unittest.TestCase):
    def script(self, tmp, body):
        path = Path(tmp) / "child.py"
        path.write_text(body, encoding="utf-8")
        return path

    def test_runs_absolute_script_isolated_with_utf8(self):
        with tempfile.TemporaryDirectory() as tmp:
            lines = []
            code = run_batch.run_child([self.script(tmp, "import sys\nprint('한글 출력', sys.flags.isolated)\nsys.exit(3)\n")],
                                       dict(os.environ), lines.append, timeout=60)
        self.assertEqual(code, 3)
        self.assertEqual(lines, ["한글 출력 1"])

    def test_stdin_is_closed_so_prompts_cannot_block(self):
        with tempfile.TemporaryDirectory() as tmp:
            lines = []
            run_batch.run_child([self.script(tmp, "try:\n    input('MFA code: ')\nexcept EOFError:\n    print('eof')\n")],
                                dict(os.environ), lines.append, timeout=60)
        self.assertEqual(lines[-1], "MFA code: eof")

    def test_timeout_kills_child(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(Failure) as caught:
                run_batch.run_child([self.script(tmp, "import time\ntime.sleep(60)\n")], dict(os.environ), print, timeout=1)
        self.assertEqual(caught.exception.kind, "timeout")
```

- [ ] **2단계: 실패 확인** — `AttributeError: module 'run_batch' has no attribute 'run_child'`

- [ ] **3단계: 구현**

```python
def run_child(args, env, log, timeout, on_line=None):
    """번들 Python으로 절대 경로 스크립트를 `-I -X utf8` 하위 프로세스로 실행한다 (`-I`라 import로는 못 부른다).

    stdin은 닫아 예기치 않은 기본 MFA 프롬프트에서도 멈추지 않고, cwd는 임시 폴더라 원문 garmindb.log가 남지 않는다.
    """
    with tempfile.TemporaryDirectory(prefix="garmin-box-", ignore_cleanup_errors=True) as cwd:
        proc = subprocess.Popen([sys.executable, "-I", "-X", "utf8", *map(str, args)], cwd=cwd, env=env,
                                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, encoding="utf-8", errors="replace")
        timed_out = threading.Event()
        timer = threading.Timer(timeout, lambda: (timed_out.set(), proc.kill()))
        timer.start()
        try:
            for line in proc.stdout:
                line = line.rstrip()
                log(line)
                if on_line:
                    on_line(line)
        finally:
            timer.cancel()
        code = proc.wait()
    if timed_out.is_set():
        raise Failure("timeout", f"{Path(args[0]).name}이(가) {timeout // 60}분 안에 끝나지 않아 중단했습니다.")
    return code
```

- [ ] **4단계: 통과 확인** — `OK`
- [ ] **5단계: 커밋** — `feat: run_batch 하위 스크립트 격리 실행과 시간 제한`

---

### 작업 4: Garmin 로그인·MFA 플래그·원격 목록

**파일:** 수정 `run_batch.py`, `tests/test_run_batch.py`

- [ ] **1단계: 실패하는 테스트**

```python
class FakeGarmin:
    """garminconnect.Garmin 대역: 로그인 중 MFA 콜백을 부르고, 실패는 라이브러리처럼 감싼다."""

    def __init__(self, email=None, password=None, is_cn=False, prompt_mfa=None):
        self.prompt_mfa = prompt_mfa

    def login(self, tokenstore=None):
        from garminconnect import GarminConnectAuthenticationError
        try:
            self.code = self.prompt_mfa()
        except Exception as error:
            raise GarminConnectAuthenticationError("MFA failed") from error

    def connectapi(self, path, **kwargs):
        return {"displayName": "runner", "fullName": "러너"}


class WrongPassword(FakeGarmin):
    def login(self, tokenstore=None):
        from garminconnect import GarminConnectAuthenticationError
        raise GarminConnectAuthenticationError("401")


class GarminLoginTests(unittest.TestCase):
    def config(self, tmp):
        return run_batch.EnteredLogin("me@example.com", "pw", Path(tmp) / "tokens.json")

    def test_scheduled_mfa_is_reauth_even_when_wrapped(self):
        with tempfile.TemporaryDirectory() as tmp, self.assertRaises(Failure) as caught:
            run_batch.garmin_login(self.config(tmp), False, FakeGarmin)
        self.assertEqual(caught.exception.kind, "reauth")

    def test_interactive_mfa_reads_one_code_line(self):
        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(sys, "stdin", io.StringIO("123456\n")), \
                redirect_stdout(io.StringIO()) as out:
            adapter = run_batch.garmin_login(self.config(tmp), True, FakeGarmin)
        self.assertEqual(adapter.client.code, "123456")
        self.assertEqual(adapter.display_name, "runner")
        self.assertIn('"mfa_required"', out.getvalue())

    def test_wrong_password_is_auth_not_reauth(self):
        with tempfile.TemporaryDirectory() as tmp, self.assertRaises(Failure) as caught:
            run_batch.garmin_login(self.config(tmp), False, WrongPassword)
        self.assertEqual(caught.exception.kind, "auth")


class FakeAdapter:
    def __init__(self, total, rows):
        self.client = SimpleNamespace(count_activities=lambda: total)
        self.rows, self.params = rows, None

    def connectapi(self, path, params=None):
        self.params = params
        return self.rows


class RemoteListTests(unittest.TestCase):
    def test_requests_at_least_2000_and_returns_id_set(self):
        adapter = FakeAdapter(2, [{"activityId": 1}, {"activityId": 2}])
        self.assertEqual(run_batch.remote_activities(adapter), (2, {"1", "2"}))
        self.assertEqual(adapter.params, {"start": "0", "limit": "2000"})

    def test_large_account_raises_limit_to_total(self):
        adapter = FakeAdapter(2500, [{"activityId": i} for i in range(2500)])
        run_batch.remote_activities(adapter)
        self.assertEqual(adapter.params["limit"], "2500")

    def test_short_list_is_not_collected(self):
        with self.assertRaises(Failure) as caught:
            run_batch.remote_activities(FakeAdapter(3, [{"activityId": 1}, {"activityId": 2}]))
        self.assertEqual(caught.exception.kind, "incomplete")

    def test_unknown_count_is_not_an_empty_account(self):
        with self.assertRaises(Failure):
            run_batch.remote_activities(FakeAdapter(None, []))

    def test_confirmed_empty_account(self):
        self.assertEqual(run_batch.remote_activities(FakeAdapter(0, [])), (0, set()))
```

- [ ] **2단계: 실패 확인** — `AttributeError: ... 'EnteredLogin'`

- [ ] **3단계: 구현**

```python
LIST_LIMIT_FLOOR = 2000  # 실제 계정 검증: 직접 API limit=2000 단일 요청이 전체 1,138건을 반환


class EnteredLogin:
    """연결 확인용 설정 대역: 저장 전 입력값으로 자격증명 로그인한다. GarminConnectAuthAdapter가 쓰는 메서드만 둔다."""

    def __init__(self, user, password, token_file):
        self.user, self.password, self.token_file = user, password, token_file

    def get_user(self):
        return self.user

    def get_password(self):
        return self.password

    def get_garmin_base_domain(self):
        return "garmin.com"  # shortcut: garmin.cn 계정은 연결 확인을 지원하지 않는다. 요청이 오면 입력란을 추가한다.

    def get_token_store_file(self):
        return str(self.token_file)


def saved_garmin_config():
    if not GARMIN_CONFIG.is_file():
        raise Failure("not_configured", "Garmin 설정 파일이 없습니다. garmin-box를 열어 설정을 저장하세요.")
    from garmindb.garmin_connect_config_manager import GarminConnectConfigManager
    try:
        return GarminConnectConfigManager(str(GARMINDB_DIR))
    except SystemExit:  # GarminDB는 깨진 설정에서 sys.exit(-1)한다
        raise Failure("config", f"Garmin 설정 파일을 읽지 못했습니다: {GARMIN_CONFIG}") from None


def garmin_failure(error):
    """원인 사슬의 garminconnect 예외 타입으로 구분한다. 재인증 판단에는 쓰지 않는다."""
    from garminconnect import (GarminConnectAuthenticationError, GarminConnectConnectionError,
                               GarminConnectTooManyRequestsError)
    kinds = ((GarminConnectTooManyRequestsError, "rate_limit", "Garmin 요청 제한(429)입니다. 한두 시간 뒤 다시 시도하세요."),
             (GarminConnectAuthenticationError, "auth", "Garmin 로그인에 실패했습니다. 이메일·비밀번호를 확인하세요."),
             (GarminConnectConnectionError, "network", "Garmin에 연결하지 못했습니다. 인터넷 연결을 확인하세요."))
    seen = error
    while seen is not None:
        if isinstance(seen, Failure):
            return seen
        for cls, kind, message in kinds:
            if isinstance(seen, cls):
                return Failure(kind, message)
        seen = seen.__cause__ or seen.__context__
    return Failure("garmin", f"Garmin 오류: {type(error).__name__}: {error}")


def garmin_login(config, prompt_allowed, factory=None):
    """GarminDB와 같은 인증 어댑터·토큰 경로로 한 번 로그인한다 (429에서 반복 로그인하지 않는다).

    재인증 판단은 예외 타입·문구·stdin EOF가 아니라 MFA 콜백이 먼저 세운 플래그로 한다.
    """
    from garmindb.garmin_connect_auth_adapter import GarminConnectAuthAdapter
    asked = {"mfa": False}

    def prompt():
        asked["mfa"] = True
        if not prompt_allowed:
            raise Failure("reauth", REAUTH_MESSAGE)
        emit("mfa_required")
        code = sys.stdin.readline().strip()
        if not code:
            raise Failure("auth", "Garmin 인증 코드가 입력되지 않았습니다.")
        return code

    adapter = GarminConnectAuthAdapter(config, garmin_factory=factory, mfa_prompt=prompt)
    try:
        adapter.login()
    except Exception as error:
        if asked["mfa"] and not prompt_allowed:
            raise Failure("reauth", REAUTH_MESSAGE) from error
        raise garmin_failure(error) from error
    return adapter


def remote_activities(adapter):
    """성공한 전체 건수 N과 직접 API 목록의 원격 ID 집합. 목록 고유 ID 수가 N과 다르면 수집하지 않는다."""
    from garmindb.download import Download
    try:
        total = adapter.client.count_activities()
        rows = adapter.connectapi(Download.garmin_connect_activity_search_url,
                                  params={"start": "0", "limit": str(max(LIST_LIMIT_FLOOR, total or 0))})
    except Exception as error:
        raise garmin_failure(error) from error
    if type(total) is not int or not isinstance(rows, list):  # None·실패를 빈 계정으로 보지 않는다
        raise Failure("garmin", "Garmin 활동 수 또는 목록을 확인하지 못했습니다. 잠시 뒤 다시 시도하세요.")
    ids = {str(row["activityId"]) for row in rows}
    if len(ids) != total:
        # shortcut: 직접 페이징 없음. 단일 요청이 잘리는 계정이 확인되면 그 제약을 검증한 뒤 추가한다.
        raise Failure("incomplete", f"Garmin 활동 목록({len(ids)}건)이 전체 건수({total}건)와 다릅니다. 잠시 뒤 다시 시도하세요.")
    return total, ids
```

- [ ] **4단계: 통과 확인** — `OK`
- [ ] **5단계: 커밋** — `feat: Garmin 로그인 재인증 플래그와 원격 활동 목록 확인`

---

### 작업 5: SQLite 대조·보충 수집·업로드 (`setup`/`sync`)

**파일:** 수정 `run_batch.py`, `sync_supa.py`, `update_gist.py`, `tests/test_run_batch.py`

- [ ] **1단계: 실패하는 테스트**

```python
class CollectTests(unittest.TestCase):
    def setUp(self):
        run_batch.WARNINGS.clear()

    def collect(self, remote, local, full_first=False, latest_adds=(), all_adds=()):
        calls, local = [], set(local)

        def run_cli(latest):
            calls.append("latest" if latest else "all")
            local.update(latest_adds if latest else all_adds)
        with redirect_stdout(io.StringIO()):
            result = run_batch.collect(set(remote), full_first, run_cli, lambda: set(local))
        return calls, result

    def test_latest_covers_remote(self):
        calls, _ = self.collect({"1", "2"}, {"1"}, latest_adds={"2"})
        self.assertEqual(calls, ["latest"])

    def test_missing_beyond_latest_triggers_one_full_run(self):
        remote = {str(i) for i in range(30)}
        calls, local = self.collect(remote, set(), latest_adds={str(i) for i in range(25)}, all_adds=remote)
        self.assertEqual(calls, ["latest", "all"])
        self.assertEqual(local, remote)

    def test_same_count_but_missing_id_blocks_upload(self):
        with self.assertRaises(Failure) as caught:
            self.collect({"1", "2", "3"}, {"1", "2", "4"})
        self.assertEqual(caught.exception.kind, "incomplete")

    def test_setup_runs_full_without_latest(self):
        calls, _ = self.collect({"1"}, set(), full_first=True, all_adds={"1"})
        self.assertEqual(calls, ["all"])

    def test_deleted_remote_activity_warns_and_continues(self):
        _, local = self.collect({"1"}, {"1", "99"})
        self.assertEqual(local, {"1", "99"})
        self.assertEqual(len(run_batch.WARNINGS), 1)

    def test_empty_remote_with_old_local_rows_warns_not_zero(self):
        _, local = self.collect(set(), {"5"})
        self.assertEqual(local, {"5"})
        self.assertTrue(run_batch.WARNINGS)

    def test_confirmed_empty_account_allows_zero(self):
        _, local = self.collect(set(), set(), full_first=True)
        self.assertEqual(local, set())


class GarminDbRunTests(unittest.TestCase):
    """이전 동기화로 SQLite ID가 이미 완전해도 이번 CLI 실패는 업로드 전에 막는다."""

    def run_cli(self, tmp, code, logged_in):
        marker = Path(tmp) / "user-settings.json"
        marker.write_text("{}", encoding="utf-8")
        os.utime(marker, (0, 0))  # 이전 실행이 남긴 로그인 표식

        def run(args, env, log, timeout, on_line=None):
            if logged_in:
                marker.write_text("{}", encoding="utf-8")  # Download.login() 성공
            return code
        return lambda latest: run_batch.run_garmindb(["garmindb_cli.py"], {}, print, 60, marker, run)

    def test_failed_cli_blocks_even_when_ids_are_complete(self):
        for code, logged_in in ((42, True), (0, False)):  # 비정상 종료 / 0으로 끝난 로그인 실패
            with self.subTest(code=code, logged_in=logged_in), tempfile.TemporaryDirectory() as tmp, \
                    redirect_stdout(io.StringIO()), self.assertRaises(Failure):
                run_batch.collect({"1"}, False, self.run_cli(tmp, code, logged_in), lambda: {"1"})

    def test_logged_in_cli_with_complete_ids_passes(self):
        with tempfile.TemporaryDirectory() as tmp, redirect_stdout(io.StringIO()):
            local = run_batch.collect({"1"}, False, self.run_cli(tmp, 0, True), lambda: {"1"})
        self.assertEqual(local, {"1"})


class UploadTests(unittest.TestCase):
    def test_supabase_failure_blocks_gist(self):
        calls = []

        def run(args, env, log, timeout, on_line=None):
            calls.append(Path(args[0]).name)
            return 1
        with self.assertRaises(Failure) as caught:
            run_batch.upload({}, print, run)
        self.assertEqual(caught.exception.kind, "supabase")
        self.assertEqual(calls, ["sync_supa.py"])


class ConfigUpdateTests(unittest.TestCase):
    def test_download_all_is_raised_and_unknown_keys_kept(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "GarminConnectConfig.json"
            path.write_text(json.dumps({"data": {"download_all_activities": 1000}, "custom": {"keep": True}}), encoding="utf-8")
            run_batch.ensure_download_all(path, 2500)
            data = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(data["data"]["download_all_activities"], 2500)
        self.assertEqual(data["custom"], {"keep": True})


class SchemaTests(unittest.TestCase):
    COLUMNS = [("activity_id", "bigint", 0), ("start_time", "timestamp without time zone", None), ("sport", "text", None),
               ("name", "text", None), ("distance_km", "numeric", 2), ("elapsed_time", "text", None),
               ("moving_time", "text", None), ("avg_speed", "numeric", 2), ("updated_at", "timestamp with time zone", None)]

    def test_repository_schema_passes(self):
        self.assertEqual(run_batch.schema_problems(self.COLUMNS), [])

    def test_silent_rounding_and_missing_column_fail(self):
        columns = [c for c in self.COLUMNS if c[0] != "sport"]
        columns = [("distance_km", "numeric", 0) if c[0] == "distance_km" else c for c in columns]
        problems = run_batch.schema_problems(columns)
        self.assertEqual(len(problems), 2)


class SyncSupaTests(unittest.TestCase):
    def test_zero_rows_need_confirmed_empty_account(self):
        import sqlite3
        from contextlib import closing
        import sync_supa
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            db = Path(tmp) / "garmin_activities.db"
            with closing(sqlite3.connect(db)) as lite:
                lite.execute("create table activities (activity_id, start_time, sport, name, distance, elapsed_time, moving_time, avg_speed)")
            with mock.patch.object(sync_supa, "SQLITE_PATH", db), mock.patch.object(sync_supa.psycopg, "connect") as connect, \
                    mock.patch.dict(os.environ, {"SUPA_DSN": "host=x"}):
                cursor = connect.return_value.__enter__.return_value.cursor.return_value.__enter__.return_value
                cursor.fetchone.return_value = (0,)
                with self.assertRaises(SystemExit):
                    sync_supa.main()
                connect.assert_not_called()
                os.environ["GARMIN_BOX_ALLOW_EMPTY"] = "1"
                sync_supa.main()
                cursor.executemany.assert_not_called()
```

- [ ] **2단계: 실패 확인** — `AttributeError: ... 'collect'`

- [ ] **3단계: `run_batch.py` 구현**

```python
CLI_TIMEOUT = {"latest": 30 * 60, "all": 6 * 60 * 60}
SCRIPT_TIMEOUT = 10 * 60
REQUIRED_COLUMNS = ("activity_id", "start_time", "sport", "name", "distance_km",
                    "elapsed_time", "moving_time", "avg_speed", "updated_at")
COLUMNS_SQL = """
    select column_name, data_type, numeric_scale from information_schema.columns
    where table_schema = current_schema() and table_name = 'garmin_activity'
"""


def schema_problems(columns):
    """필수 열과, 오류 없이 반올림되는 거리·속도 소수점 손실(예: numeric(8,0))을 찾는다."""
    found = {name: (dtype, scale) for name, dtype, scale in columns}
    problems = [f"{name} 열이 없습니다" for name in REQUIRED_COLUMNS if name not in found]
    for name in ("distance_km", "avg_speed"):
        dtype, scale = found.get(name, (None, None))
        if dtype and not (dtype in ("real", "double precision") or (dtype == "numeric" and (scale is None or scale >= 2))):
            problems.append(f"{name}({dtype}, 소수 {scale}자리)는 소수점 둘째 자리를 보존하지 않습니다")
    return problems


def create_schema(dsn):
    """create table if not exists만 실행한다. 기존 테이블은 바꾸거나 지우지 않는다."""
    import psycopg
    with psycopg.connect(dsn, connect_timeout=30) as pg:
        pg.execute((HERE / "schema.sql").read_text(encoding="utf-8"))
        problems = schema_problems(pg.execute(COLUMNS_SQL).fetchall())
    if problems:
        raise Failure("schema", "Supabase garmin_activity 테이블을 그대로 쓸 수 없습니다: " + "; ".join(problems)
                      + ". 테이블은 변경하지 않았습니다.")


def activities_db(config):
    """GarminDB 경로 계산을 재사용해 DB(빈 계정도 테이블)를 준비하고 절대 경로를 돌려준다."""
    from garmindb.garmindb import ActivitiesDb
    ActivitiesDb(config.get_db_params()).engine.dispose()
    return Path(config.get_db_dir()) / "garmin_activities.db"


def local_ids(db_path):
    """sync_supa.py의 업로드 대상과 같은 조건(start_time is not null), ID는 문자열로."""
    with closing(sqlite3.connect(db_path)) as db:
        return {str(row[0]) for row in db.execute("select activity_id from activities where start_time is not null")}


def ensure_download_all(config_file, count):
    """전체 수집이 원격 전체를 받도록 download_all_activities를 count 이상으로 둔다 (다른 설정은 보존)."""
    data = json.loads(config_file.read_text(encoding="utf-8"))
    if (data.get("data", {}).get("download_all_activities") or 0) >= count:
        return
    data.setdefault("data", {})["download_all_activities"] = count
    temp = config_file.with_name(config_file.name + ".tmp")
    temp.write_text(json.dumps(data, indent=4, ensure_ascii=False), encoding="utf-8")
    os.replace(temp, config_file)


def collect(remote, full_first, run_cli, read_local):
    """CLI 다운로드·import 뒤 원격 ID가 SQLite에 모두 있어야 완료다 (건수가 같아도 ID가 빠지면 실패).

    필수 요약 실패는 ID 누락으로 드러나고, 원본 FIT만 없는 수동 활동은 요약 행이 있어 통과한다.
    """
    missing = None
    if not full_first:
        run_cli(latest=True)
        missing = remote - read_local()
        if missing:
            warn(f"최근 기록 밖에서 빠진 {len(missing)}건을 전체 수집으로 보충합니다.")
    if full_first or missing:
        run_cli(latest=False)
        if still := remote - read_local():
            raise Failure("incomplete", f"Garmin 기록 {len(still)}건을 가져오지 못해 Supabase·gist를 갱신하지 않았습니다. 다시 동기화하세요.")
    local = read_local()
    if extra := local - remote:
        warn(f"Garmin에 없는 기록 {len(extra)}건이 로컬·Supabase·gist 통계에 남아 있습니다. 삭제는 반영하지 않습니다.")
    return local


def run_garmindb(args, env, log, timeout, login_marker, run=run_child, on_line=None):
    """garmindb_cli를 한 번 실행한다. 0이 아닌 종료는 실패이고, 0이어도 이번 실행에서 로그인 표식이 갱신되지 않았으면 실패다.

    GarminDB 3.9.0은 로그인 실패를 sys.exit()(종료 코드 0)로 끝내고, 성공할 때마다 user-settings.json을 덮어쓴다.
    시작 시각과 비교하지 않는다: NTFS 수정 시각은 time.time()보다 거칠어(약 15ms) 방금 쓴 파일이 더 이르게 보일 수 있다.
    """
    before = login_marker.stat().st_mtime_ns if login_marker.is_file() else None
    code = run(args, env, log, timeout, on_line)
    if code != 0:
        raise Failure("garmin", f"GarminDB 수집이 실패했습니다(종료 코드 {code}). 로그를 확인하세요.")
    if not login_marker.is_file() or login_marker.stat().st_mtime_ns == before:
        raise Failure("garmin", "GarminDB가 Garmin에 로그인하지 못해 수집하지 않았습니다. 잠시 뒤 다시 동기화하세요.")


def upload(env, log, run=run_child):
    """sync_supa.py는 한 트랜잭션이라 SQL·변환 오류면 전체 롤백 후 실패한다. 그때 gist는 갱신하지 않는다."""
    for script, kind in (("sync_supa.py", "supabase"), ("update_gist.py", "gist")):
        code = run([HERE / script], env, log, SCRIPT_TIMEOUT, lambda line: emit("progress", stage=kind, message=line))
        if code != 0:
            raise Failure(kind, f"{script} 실패(종료 코드 {code}). 로그를 확인하세요.")


def child_env(values, **extra):
    env = {k: v for k, v in os.environ.items() if k not in ("GARMIN_EMAIL", "GARMIN_PASSWORD", "GARMINTOKENS")}
    return {**env, **values, **extra}


def sync(mode, values, prompt_allowed, log, factory=None):
    """setup: 테이블 생성 + 전체 수집, sync: 최근 수집(누락 시 전체 보충). 둘 다 업로드 전 SQLite 대조."""
    for key, label in (("SUPA_DSN", "Supabase 연결 문자열"), ("GITHUB_TOKEN", "GitHub 토큰"), ("GIST_ID", "Gist ID")):
        need(values, key, label)
    if mode == "setup":
        emit("progress", stage="schema", message="Supabase 테이블 확인")
        create_schema(values["SUPA_DSN"])
    config = saved_garmin_config()
    emit("progress", stage="login", message="Garmin 로그인")
    adapter = garmin_login(config, prompt_allowed, factory)
    db_path = activities_db(config)
    total, remote = remote_activities(adapter)
    emit("progress", stage="list", message=f"Garmin 활동 {total}건 확인")
    # Windows 전체 동기화의 DB 경로는 Garmin 설정에서 계산한 값이 우선한다 (.env에 중복 저장하지 않는다)
    env = child_env(values, GARMIN_SQLITE=str(db_path))
    cli = Path(sys.executable).parent / "Scripts" / "garmindb_cli.py"
    login_marker = Path(config.get_fit_files_dir()) / "user-settings.json"

    def run_cli(latest):
        stage = "latest" if latest else "all"
        if not latest:
            ensure_download_all(GARMIN_CONFIG, max(LIST_LIMIT_FLOOR, total))
        run_garmindb([cli, "--activities", "--download", "--import", "--analyze", *(["--latest"] if latest else [])],
                     env, log, CLI_TIMEOUT[stage], login_marker,
                     on_line=lambda line: emit("progress", stage=stage, message=line))

    local = collect(remote, mode == "setup", run_cli, lambda: local_ids(db_path))
    try:
        changed = adapter.client.count_activities() != total
    except Exception:
        changed = True
    if changed:
        warn("수집하는 동안 Garmin 기록 수가 바뀌었거나 확인하지 못했습니다. 잠시 뒤 '지금 동기화'를 다시 실행하세요.")
    if not local:
        env["GARMIN_BOX_ALLOW_EMPTY"] = "1"  # collect가 원격 0건과 로컬 0행을 확인한 경우에만 여기 온다
    upload(env, log)
    return {"activities": len(local), "gist_url": f"https://gist.github.com/{values['GIST_ID']}"}
```

- [ ] **4단계: `sync_supa.py` 수정** (Linux 단독 실행 동작은 그대로)

```python
    if not rows and os.environ.get("GARMIN_BOX_ALLOW_EMPTY") != "1":
        # run_batch.py가 Garmin 원격 0건을 확인한 경우에만 0건 동기화를 허용한다
        sys.exit("no activity rows in sqlite")

    # 한 트랜잭션: SQL·변환 오류면 with 블록이 전체 upsert를 롤백하고 예외로 실패 종료한다
    with psycopg.connect(os.environ["SUPA_DSN"], connect_timeout=30) as pg, pg.cursor() as cur:
        # ponytail: 전체 upsert 매회 실행, 건수가 수천 건 넘어가면 증분으로 전환
        if rows:
            cur.executemany(UPSERT_SQL, rows)
```

- [ ] **5단계: `update_gist.py` 수정** — `psycopg.connect(dsn, connect_timeout=30)`, `urllib.request.urlopen(req, timeout=30)`

- [ ] **6단계: 통과 확인** — `& $PY -I -X utf8 -m unittest discover -s tests -t . -v` → `OK`
- [ ] **7단계: 커밋** — `feat: SQLite ID 대조와 보충 수집 후에만 업로드`

---

### 작업 6: `check` 모드 (Garmin·PostgreSQL·GitHub 확인)

**파일:** 수정 `run_batch.py`, `tests/test_run_batch.py`

- [ ] **1단계: 실패하는 테스트**

```python
class TokenWritingGarmin(FakeGarmin):
    def login(self, tokenstore=None):
        Path(tokenstore).write_text("checked", encoding="utf-8")  # garminconnect가 로그인 후 토큰을 쓰는 것처럼


class GarminCheckTokenTests(unittest.TestCase):
    """A 계정의 로컬 DB가 있는 PC에서 연결 확인이 운영 토큰을 바꾸지 않는지 본다."""

    def setUp(self):
        root = Path(self.enterContext(tempfile.TemporaryDirectory()))
        config = root / "GarminConnectConfig.json"
        config.write_text(json.dumps({"credentials": {"user": "a@example.com"}}), encoding="utf-8")
        self.db = db = root / "garmin_activities.db"
        db.write_bytes(b"")
        self.token, self.check_token = root / "garmin_tokens.json", root / "garmin_tokens.check.json"
        self.token.write_text("A", encoding="utf-8")
        for name, value in (("GARMIN_CONFIG", config), ("TOKEN_FILE", self.token), ("CHECK_TOKEN", self.check_token),
                            ("configured_db_path", lambda: db)):
            self.enterContext(mock.patch.object(run_batch, name, value))

    def check(self, email, factory):
        with mock.patch.dict(os.environ, {"GARMIN_EMAIL": email, "GARMIN_PASSWORD": "pw"}), redirect_stdout(io.StringIO()):
            return run_batch.check_garmin(factory)

    def test_other_account_is_refused_before_login(self):
        calls = []
        with self.assertRaises(Failure) as caught:
            self.check("b@example.com", lambda **kwargs: calls.append(kwargs))
        self.assertEqual(caught.exception.kind, "account")
        self.assertEqual(calls, [])                                   # 로그인을 시도하지 않았다
        self.assertEqual(self.token.read_text(encoding="utf-8"), "A")  # 다음 동기화도 A로 로그인한다
        self.assertFalse(self.check_token.exists())

    def test_check_leaves_production_token_until_save(self):
        result = self.check("A@example.com", TokenWritingGarmin)
        self.assertEqual(result["db_path"], str(self.db))   # 저장 시점 재확인용
        self.assertEqual(self.token.read_text(encoding="utf-8"), "A")
        self.assertEqual(self.check_token.read_text(encoding="utf-8"), "checked")


class GithubCheckTests(unittest.TestCase):
    def fake(self, scopes="gist", owner="me"):
        def get(path, token):
            if path == "/user":
                return {"login": "me"}, scopes
            return {"owner": {"login": owner}, "public": False, "files": {"garmin-stats": {}, "other.md": {}},
                    "html_url": "https://gist.github.com/me/0123456789abcdef0123"}, scopes
        return mock.patch.object(run_batch, "github_get", get)

    def test_lists_files_of_own_gist(self):
        with self.fake():
            result = run_batch.check_github("t", "https://gist.github.com/me/0123456789abcdef0123")
        self.assertEqual(result["files"], ["garmin-stats", "other.md"])
        self.assertEqual(result["gist_id"], "0123456789abcdef0123")
        self.assertFalse(result["public"])

    def test_token_without_gist_scope_is_rejected(self):
        with self.fake(scopes="repo"), self.assertRaises(Failure) as caught:
            run_batch.check_github("t", "0123456789abcdef0123")
        self.assertEqual(caught.exception.kind, "permission")

    def test_gist_of_another_user_is_rejected(self):
        with self.fake(owner="someone"), self.assertRaises(Failure):
            run_batch.check_github("t", "0123456789abcdef0123")

    def test_gist_reference_forms(self):
        for ref in ("0123456789abcdef0123", "https://gist.github.com/0123456789abcdef0123/",
                    "https://gist.github.com/me/0123456789ABCDEF0123"):
            self.assertEqual(run_batch.parse_gist_id(ref), "0123456789abcdef0123")
        with self.assertRaises(Failure):
            run_batch.parse_gist_id("https://github.com/me/repo")
```

- [ ] **2단계: 실패 확인** — `AttributeError: ... 'check_github'`

- [ ] **3단계: 구현**

```python
import urllib.request  # 파일 상단 import에 추가

GIST_REF = re.compile(r"(?:https?://gist\.github\.com/(?:[\w-]+/)?)?([0-9a-f]{7,32})/?", re.IGNORECASE)


def parse_gist_id(ref):
    match = GIST_REF.fullmatch(ref.strip())
    if not match:
        raise Failure("input", "Gist URL 또는 ID 형식이 아닙니다.")
    return match.group(1).lower()


def github_get(path, token):
    request = urllib.request.Request(f"https://api.github.com{path}", headers={
        "Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=20) as response:
        return json.load(response), response.headers.get("X-OAuth-Scopes")


def check_github(token, gist_ref):
    """토큰 사용자와 gist 소유자가 같은지, 갱신할 파일 후보와 공개 여부를 확인한다. gist는 수정하지 않는다."""
    gist_id = parse_gist_id(gist_ref)
    user, scopes = github_get("/user", token)
    # classic 토큰만 범위 헤더가 있다. fine-grained 토큰은 쓰기 권한을 첫 갱신 때 확인한다.
    if scopes is not None and "gist" not in [s.strip() for s in scopes.split(",")]:
        raise Failure("permission", "GitHub 토큰에 gist 권한이 없습니다.")
    gist, _ = github_get(f"/gists/{gist_id}", token)
    owner = (gist.get("owner") or {}).get("login")
    if owner != user["login"]:
        raise Failure("permission", f"gist 소유자({owner})가 토큰 사용자({user['login']})와 다릅니다.")
    return {"login": user["login"], "gist_id": gist_id, "public": gist["public"],
            "files": sorted(gist["files"]), "url": gist["html_url"]}


def check_postgres(dsn):
    import psycopg
    with psycopg.connect(dsn, connect_timeout=15) as pg:
        pg.execute("select 1")
    return {}


def saved_email():
    with suppress(Exception):
        return json.loads(GARMIN_CONFIG.read_text(encoding="utf-8"))["credentials"]["user"] or None
    return None


def refuse_account_change(email):
    """한 Windows 사용자에 한 Garmin 계정: 로컬 DB가 있으면 로그인·토큰 저장 전에 다른 이메일을 거부한다."""
    saved = saved_email()
    if saved and email and email.lower() != saved.lower() and configured_db_path().exists():
        raise Failure("account", "이 PC에는 다른 Garmin 계정의 운동 DB가 있어 계정을 바꿀 수 없습니다.")


def check_garmin(factory=None):
    """입력한 비밀번호가 있으면 토큰을 건너뛰고 자격증명으로 확인한다 (예전 토큰으로 잘못된 비밀번호가 통과하지 않게).

    받은 토큰은 확인용 파일에만 둔다. 운영 토큰은 EXE가 설정을 저장할 때만 바꾸므로, 확인만 하고 저장하지 않거나
    거부되면 다음 동기화는 기존 계정으로 로그인한다.
    """
    CHECK_TOKEN.unlink(missing_ok=True)  # 저장 시점의 확인용 토큰은 항상 마지막 확인의 것
    email, password = os.environ.get("GARMIN_EMAIL", ""), os.environ.get("GARMIN_PASSWORD")
    refuse_account_change(email)
    if password:
        adapter = garmin_login(EnteredLogin(need(os.environ, "GARMIN_EMAIL", "Garmin 이메일"), password, CHECK_TOKEN), True, factory)
        if not CHECK_TOKEN.exists():
            warn("Garmin 로그인 토큰을 저장하지 못했습니다. 다음 동기화에서 다시 로그인합니다.")
    else:
        adapter = garmin_login(saved_garmin_config(), True, factory)  # 같은 계정: 운영 토큰 갱신은 괜찮다
    # db_path: EXE가 저장 직전 잠금 안에서 계정 제약을 다시 확인할 때 쓴다 (확인 뒤 수동 동기화가 DB를 만들 수 있다)
    return {"name": adapter.full_name or adapter.display_name, "db_path": str(configured_db_path())}


def check_item(run):
    try:
        return {"ok": True, **run()}
    except Failure as failure:
        return {"ok": False, "kind": failure.kind, "message": REDACT(str(failure))}
    except Exception as error:
        return {"ok": False, "kind": "unexpected", "message": REDACT(f"{type(error).__name__}: {error}")}


def configured_db_path():
    if GARMIN_CONFIG.is_file():
        with suppress(Failure):
            return Path(saved_garmin_config().get_db_dir()) / "garmin_activities.db"
    return Path.home() / "HealthData" / "DBs" / "garmin_activities.db"


def check(values, factory=None):
    """연결 확인: 테이블·gist를 만들거나 고치지 않는다 (Garmin 확인용 토큰만 저장)."""
    result = {
        "garmin": check_item(lambda: check_garmin(factory)),
        "postgres": check_item(lambda: check_postgres(need(values, "SUPA_DSN", "Supabase 연결 문자열"))),
        "github": check_item(lambda: check_github(need(values, "GITHUB_TOKEN", "GitHub 토큰"),
                                                  need(values, "GIST_ID", "Gist URL 또는 ID"))),
    }
    emit("check_result", **result)
    return EXIT_OK if all(item["ok"] for item in result.values()) else EXIT_FAIL
```

- [ ] **4단계: 통과 확인** — `OK`
- [ ] **5단계: 커밋** — `feat: run_batch 연결 확인 모드`

---

### 작업 7: C# 솔루션 골격·경로 사전점검·잠금

**파일:** 생성 `GarminBox.slnx`, `src/GarminBox/GarminBox.csproj`, `Paths.cs`, `AppLock.cs`, `Program.cs`(임시), `src/GarminBox.Tests/GarminBox.Tests.csproj`, `TestSupport.cs`, `PathsTests.cs`, `AppLockTests.cs`

- [ ] **1단계: 프로젝트 생성**

```powershell
dotnet new sln -n GarminBox --format slnx
dotnet new winforms -n GarminBox -o src\GarminBox -f net10.0
dotnet new xunit -n GarminBox.Tests -o src\GarminBox.Tests -f net10.0
dotnet sln GarminBox.slnx add src\GarminBox src\GarminBox.Tests
dotnet add src\GarminBox.Tests reference src\GarminBox
```

`src/GarminBox/GarminBox.csproj` (self-contained·단일 파일은 publish 명령에서만 지정해 테스트 참조 오류 NETSDK1150을 피한다):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>GarminBox</AssemblyName>
    <ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="GarminBox.Tests" />
  </ItemGroup>
  <ItemGroup Condition="Exists('payload\payload.zip')">
    <EmbeddedResource Include="payload\payload.zip" LogicalName="payload.zip" />
    <EmbeddedResource Include="payload\payload.sha256" LogicalName="payload.sha256" />
  </ItemGroup>
</Project>
```

테스트 프로젝트의 `TargetFramework`를 `net10.0-windows`로 바꾸고 `<UseWindowsForms>true</UseWindowsForms>`를 넣는다. 템플릿의 `Form1.cs`·`UnitTest1.cs`는 지운다.

- [ ] **2단계: 실패하는 테스트**

`src/GarminBox.Tests/TestSupport.cs`:

```csharp
[assembly: CollectionBehavior(DisableTestParallelization = true)]   // TempProfile이 프로세스 환경 변수를 바꾼다

namespace GarminBox.Tests;

sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("garmin-box-test-").FullName;
    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

/// USERPROFILE·LOCALAPPDATA를 임시 폴더로 바꾼다 (Paths와 run_batch.py가 같은 변수를 기준으로 한다).
sealed class TempProfile : IDisposable
{
    readonly string? home = Environment.GetEnvironmentVariable("USERPROFILE");
    readonly string? local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
    public TempDir Dir { get; } = new();

    public TempProfile()
    {
        Environment.SetEnvironmentVariable("USERPROFILE", Dir.Root);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(Dir.Root, "Local"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("USERPROFILE", home);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", local);
        Dir.Dispose();
    }
}

static class Repo
{
    public static string Root { get; } = Find();
    static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GarminBox.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("GarminBox.slnx");
    }
}
```

`PathsTests.cs`:

```csharp
namespace GarminBox.Tests;

public class PathsTests
{
    [Fact]
    public void NonAsciiInstallPathStopsBeforeAnything()
    {
        using var profile = new TempProfile();
        string korean = Path.Combine(profile.Dir.Root, "홍길동");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", korean);
        string? problem = Paths.Preflight();
        Assert.StartsWith("현재 Python 번들은 한글 등 비 ASCII 설치 경로에서 인증서를 읽지 못합니다. 이 PC에서는 설정을 진행할 수 없습니다.", problem);
        Assert.Contains(Path.Combine(korean, "garmin-box"), problem);
        Assert.False(Directory.Exists(korean));   // 파일 배치 전에 멈춘다
    }

    [Fact]
    public void AsciiProfilePasses()
    {
        using var profile = new TempProfile();
        Assert.Null(Paths.Preflight());
    }
}
```

(임시 폴더가 비 ASCII 사용자 프로필 아래에 있으면 `AsciiProfilePasses`가 실패한다. 그런 개발 PC에서는 `TEMP`를 ASCII 경로로 바꿔 실행한다.)

`AppLockTests.cs`:

```csharp
namespace GarminBox.Tests;

public class AppLockTests
{
    static string Name() => $@"Local\garmin-box-test-{Guid.NewGuid():N}";

    [Fact]
    public void SecondHolderIsRefused()
    {
        string name = Name();
        using var first = AppLock.TryAcquire(name);
        AppLock? second = null;
        var thread = new Thread(() => second = AppLock.TryAcquire(name));
        thread.Start(); thread.Join();
        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void AbandonedLockIsOwnedReportedAndReleased()
    {
        string name = Name();
        var thread = new Thread(() => AppLock.TryAcquire(name));   // 해제 없이 스레드 종료 = 비정상 종료한 이전 실행
        thread.Start(); thread.Join();
        using (var recovered = AppLock.TryAcquire(name))
        {
            Assert.NotNull(recovered);
            Assert.True(recovered.WasAbandoned);
        }
        using var again = AppLock.TryAcquire(name);
        Assert.False(again!.WasAbandoned);
    }
}
```

- [ ] **3단계: 실패 확인** — `dotnet test GarminBox.slnx` → `Paths`/`AppLock` 없음 컴파일 오류

- [ ] **4단계: 구현**

`src/GarminBox/Paths.cs`:

```csharp
namespace GarminBox;

/// 고정 경로. USERPROFILE·LOCALAPPDATA 기준이라 자식 Python(Path.home())과 같은 위치를 본다.
static class Paths
{
    static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} 환경 변수가 없습니다.");
    public static string Home => Env("USERPROFILE");
    public static string InstallDir => Path.Combine(Env("LOCALAPPDATA"), "garmin-box");
    public static string Exe => Path.Combine(InstallDir, "GarminBox.exe");
    public static string Runtime => Path.Combine(InstallDir, "runtime");
    public static string Python => Path.Combine(Runtime, "python", "python.exe");
    public static string RunBatch => Path.Combine(Runtime, "app", "run_batch.py");
    public static string CaBundle => Path.Combine(Runtime, "python", "Lib", "site-packages", "certifi", "cacert.pem");
    public static string GarminExample => Path.Combine(Runtime, "python", "Lib", "site-packages", "garmindb", "GarminConnectConfig.json.example");
    public static string Settings => Path.Combine(InstallDir, "settings.json");
    public static string Status => Path.Combine(InstallDir, "status.json");
    public static string GarminDbDir => Path.Combine(Home, ".GarminDb");
    public static string GarminConfig => Path.Combine(GarminDbDir, "GarminConnectConfig.json");
    public static string GarminToken => Path.Combine(GarminDbDir, "garmin_tokens.json");         // GarminDB 운영 토큰
    public static string CheckToken => Path.Combine(GarminDbDir, "garmin_tokens.check.json");    // 연결 확인용 (run_batch.py와 같은 이름)
    public static string EnvFile => Path.Combine(Home, ".garmin-box.env");
    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "garmin-box.lnk");

    /// 첫 실행·재실행 모두 정보 입력 전에 통과해야 한다. 문제가 없으면 null.
    public static string? Preflight()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || !Environment.Is64BitOperatingSystem)
            return "Windows 10/11 64비트에서만 실행할 수 있습니다.";
        // 현재 Python 번들은 비 ASCII 경로의 CA 파일을 libcurl이 읽지 못한다 (실행 검증 기록). 우회하지 않고 멈춘다.
        if (new[] { InstallDir, CaBundle }.FirstOrDefault(p => p.Any(c => c > 0x7F)) is { } bad)
            return $"현재 Python 번들은 한글 등 비 ASCII 설치 경로에서 인증서를 읽지 못합니다. 이 PC에서는 설정을 진행할 수 없습니다.\n경로: {bad}";
        try
        {
            Directory.CreateDirectory(InstallDir);
            string probe = Path.Combine(InstallDir, ".write-check");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return $"설치 폴더에 쓸 수 없습니다. Windows 정책이나 권한을 확인하세요.\n경로: {InstallDir}\n{e.Message}";
        }
        return null;
    }
}
```

`src/GarminBox/AppLock.cs`:

```csharp
namespace GarminBox;

/// 파일 배치·설정 저장·지금 동기화·예약 실행·수동 런처를 직렬화하는 하나뿐인 잠금. 획득한 스레드에서 Dispose한다.
sealed class AppLock : IDisposable
{
    readonly Mutex mutex;
    public bool WasAbandoned { get; }

    AppLock(Mutex mutex, bool abandoned) { this.mutex = mutex; WasAbandoned = abandoned; }

    public static AppLock? TryAcquire(string name = @"Local\garmin-box")
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(0)) return new AppLock(mutex, false);
        }
        catch (AbandonedMutexException)
        {
            // 소유권은 얻었다. 이전 실행이 성공했다는 뜻은 아니며, 모든 작업이 설정 완료 표식·런타임 표식을 매번 다시 확인한다.
            return new AppLock(mutex, true);
        }
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
```

`Program.cs`는 이 단계에서 템플릿을 `ApplicationConfiguration.Initialize(); Application.Run(new Form());`로 두고 작업 12에서 바꾼다.

- [ ] **5단계: 통과 확인** — `dotnet test GarminBox.slnx` → 4개 통과
- [ ] **6단계: 커밋** — `feat: Windows 설정 EXE 골격, 경로 사전점검과 공통 잠금`

---

### 작업 8: 설정 파일·원자적 저장·ACL

**파일:** 생성 `src/GarminBox/ConfigFiles.cs`, `SecretAcl.cs`, `src/GarminBox.Tests/ConfigFilesTests.cs`, `SecretAclTests.cs`

- [ ] **1단계: 실패하는 테스트**

`ConfigFilesTests.cs`:

```csharp
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarminBox.Tests;

public class ConfigFilesTests
{
    const string NoDb = @"Z:\no-such-dir\garmin_activities.db";   // 로컬 DB가 없는 PC

    /// 프로필 ACL이 넓은 PC를 흉내 낸다: 이 폴더에 새로 만드는 파일은 Everyone 읽기를 물려받는다.
    static void AllowEveryoneToRead(string directory)
    {
        var dir = new DirectoryInfo(directory);
        var security = dir.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }

    [Fact]
    public void SecretTempFileIsRestrictedBeforeAnyContent()
    {
        using var temp = new TempDir();
        AllowEveryoneToRead(temp.Root);
        string tempFile = ConfigFiles.CreateProtectedTemp(Path.Combine(temp.Root, ".garmin-box.env"));
        Assert.Equal(0, new FileInfo(tempFile).Length);
        Assert.True(SecretAcl.IsRestricted(new FileInfo(tempFile).GetAccessControl()));
    }

    [Fact]
    public void SecretFileAndBackupAreRestrictedUnderBroadParent()
    {
        using var temp = new TempDir();
        AllowEveryoneToRead(temp.Root);
        string path = Path.Combine(temp.Root, ".garmin-box.env");
        File.WriteAllText(path, "SUPA_DSN=old\n");   // 넓은 권한을 물려받은 기존 파일 → 백업이 된다
        ConfigFiles.AtomicWrite(path, "SUPA_DSN=new\n");
        // 사후 보호가 없으므로, 교체 전에 기존 대상을 보호하지 않으면 File.Replace가 넓은 DACL을 남겨 여기서 실패한다
        Assert.True(SecretAcl.IsRestricted(new FileInfo(path).GetAccessControl()));
        Assert.True(SecretAcl.IsRestricted(new FileInfo(path + ".bak").GetAccessControl()));
        string fresh = Path.Combine(temp.Root, "new.json");
        ConfigFiles.AtomicWrite(fresh, "{}");   // 기존 파일이 없으면 보호된 임시 파일을 그대로 옮긴다
        Assert.True(SecretAcl.IsRestricted(new FileInfo(fresh).GetAccessControl()));
    }

    [Fact]
    public void EnvKeepsEqualsUnicodeAndEmptyValues()
    {
        var values = new Dictionary<string, string> { ["SUPA_DSN"] = "host=db password='a=b' 한글", ["GIST_FILENAME"] = "", ["GITHUB_TOKEN"] = "ghp_x" };
        Assert.Equal(values, ConfigFiles.ParseEnv(ConfigFiles.FormatEnv(values)));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void EnvRejectsLineBreaksAndNul(string value) =>
        Assert.Throws<ArgumentException>(() => ConfigFiles.FormatEnv(new Dictionary<string, string> { ["X"] = value }));

    [Fact]
    public void GarminConfigKeepsUnknownSettingsAndCustomDataPath()
    {
        string existing = """{"directories":{"relative_to_home":false,"base_dir":"D:\\Health"},"custom":{"x":1},"credentials":{"user":"me@x.com","password":"old"}}""";
        var merged = JsonNode.Parse(ConfigFiles.MergeGarminConfig(existing, "{}", "me@x.com", null))!;
        Assert.Equal(@"D:\Health", (string?)merged["directories"]!["base_dir"]);
        Assert.Equal(1, (int)merged["custom"]!["x"]!);
        Assert.Equal("old", (string?)merged["credentials"]!["password"]);   // 빈 입력은 저장된 비밀번호를 지우지 않는다
    }

    [Fact]
    public void NewGarminConfigStartsFromBundledExample()
    {
        string example = """{"credentials":{"user":"","secure_password":false,"password":"","password_file":null},"data":{"download_latest_activities":25}}""";
        var merged = JsonNode.Parse(ConfigFiles.MergeGarminConfig(null, example, "me@x.com", "pw"))!;
        Assert.Equal("pw", (string?)merged["credentials"]!["password"]);
        Assert.Equal(25, (int)merged["data"]!["download_latest_activities"]!);
    }

    [Fact]
    public void CorruptGarminConfigIsNotOverwritten() =>
        Assert.ThrowsAny<JsonException>(() => ConfigFiles.MergeGarminConfig("{ broken", "{}", "a", "b"));

    [Fact]
    public void AtomicWriteKeepsOneBackup()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Root, "a.json");
        foreach (var text in new[] { "1", "2", "3" }) ConfigFiles.AtomicWrite(path, text);
        Assert.Equal("3", File.ReadAllText(path));
        Assert.Equal("2", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void FailedSaveLeavesSyncBlocked()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        Directory.CreateDirectory(Paths.EnvFile);   // 같은 이름의 폴더 → .env 저장 실패
        Directory.CreateDirectory(Paths.GarminDbDir);
        File.WriteAllText(Paths.GarminToken, "A");
        File.WriteAllText(Paths.CheckToken, "B");
        Assert.ThrowsAny<Exception>(() => ConfigFiles.SaveAll(new AppSettings(), "{}", "SUPA_DSN=secret\n", NoDb));
        Assert.False(ConfigFiles.LoadSettings().ConfigComplete);
        Assert.False(File.Exists(Paths.EnvFile + ".tmp"));             // 평문 비밀 임시 파일이 남지 않는다
        Assert.Equal("A", File.ReadAllText(Paths.GarminToken));        // 저장이 끝나지 않으면 운영 토큰도 그대로
    }

    [Fact]
    public void SuccessfulSaveMarksCompleteAndPromotesCheckedToken()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        Directory.CreateDirectory(Paths.GarminDbDir);
        File.WriteAllText(Paths.GarminToken, "old");
        File.WriteAllText(Paths.CheckToken, "checked");
        ConfigFiles.SaveAll(new AppSettings("07:30"), "{}", "SUPA_DSN=x\n", NoDb);
        Assert.Equal(new AppSettings("07:30", true), ConfigFiles.LoadSettings());
        Assert.Contains("\"config_complete\": true", File.ReadAllText(Paths.Settings));   // run_batch.py가 읽는 이름
        Assert.Equal("checked", File.ReadAllText(Paths.GarminToken));
        Assert.False(File.Exists(Paths.CheckToken));
    }

    [Fact]
    public void AccountChangeIsRefusedAtSaveWhenDbAppearedAfterCheck()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        Directory.CreateDirectory(Paths.GarminDbDir);
        File.WriteAllText(Paths.GarminConfig, """{"credentials":{"user":"a@x.com"}}""");
        File.WriteAllText(Paths.GarminToken, "A");
        File.WriteAllText(Paths.CheckToken, "B");                 // DB가 없을 때 B로 확인했다
        string db = Path.Combine(profile.Dir.Root, "garmin_activities.db");
        File.WriteAllBytes(db, []);                               // 그 사이 A 설정의 수동 동기화가 DB를 만들었다
        Assert.Throws<InvalidOperationException>(() =>
            ConfigFiles.SaveAll(new AppSettings(), """{"credentials":{"user":"b@x.com"}}""", "SUPA_DSN=x\n", db));
        Assert.Equal("A", File.ReadAllText(Paths.GarminToken));   // 다음 동기화도 A로 로그인한다
        Assert.Contains("a@x.com", File.ReadAllText(Paths.GarminConfig));
        ConfigFiles.SaveAll(new AppSettings(), """{"credentials":{"user":"A@X.com"}}""", "SUPA_DSN=x\n", db);   // 같은 계정은 통과
    }
}
```

`SecretAclTests.cs`:

```csharp
using System.Security.AccessControl;
using System.Security.Principal;

namespace GarminBox.Tests;

public class SecretAclTests
{
    static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    [Fact]
    public void BroadFolderAccessIsReplacedAndReplacedTokensInherit()
    {
        using var temp = new TempDir();
        var dir = Directory.CreateDirectory(Path.Combine(temp.Root, ".GarminDb"));
        var security = dir.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);

        SecretAcl.ProtectTree(dir.FullName);

        string token = Path.Combine(dir.FullName, "garmin_tokens.json");
        File.WriteAllText(token + ".tmp", "{}");
        File.Move(token + ".tmp", token, overwrite: true);   // garminconnect의 임시 파일 교체 흉내
        Assert.True(SecretAcl.IsRestricted(dir.GetAccessControl()));
        Assert.True(SecretAcl.IsRestricted(new FileInfo(token).GetAccessControl()));
    }

    [Fact]
    public void ExplicitBroadAccessOnExistingFileIsRemoved()
    {
        using var temp = new TempDir();
        string file = Path.Combine(temp.Root, "GarminConnectConfig.json.bak");
        File.WriteAllText(file, "{}");
        var security = new FileInfo(file).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);

        SecretAcl.ProtectTree(temp.Root);

        Assert.True(SecretAcl.IsRestricted(new FileInfo(file).GetAccessControl()));
    }
}
```

- [ ] **2단계: 실패 확인** — 컴파일 오류

- [ ] **3단계: 구현**

`src/GarminBox/ConfigFiles.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarminBox;

/// settings.json: 비밀값 없는 앱 상태. config_complete가 true일 때만 run_batch.py가 setup/sync를 실행한다.
sealed record AppSettings(string ScheduleTime = "09:10", bool ConfigComplete = false);

static class ConfigFiles
{
    static readonly UTF8Encoding Utf8 = new(false);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    /// KEY=VALUE, 첫 '='만 구분자. run_batch.py·기존 run_garmin_batch.ps1과 같은 규칙.
    public static Dictionary<string, string> ParseEnv(string text)
    {
        var values = new Dictionary<string, string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int i = line.IndexOf('=');
            if (line.StartsWith('#') || i < 1) continue;
            values[line[..i].Trim()] = line[(i + 1)..];
        }
        return values;
    }

    public static string FormatEnv(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
            if ((key + value).IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException($"{key} 값에는 줄바꿈이나 NUL 문자를 넣을 수 없습니다.");
        return string.Concat(values.Select(pair => $"{pair.Key}={pair.Value}\n"));
    }

    /// 기존 파일이 있으면 알 수 없는 설정·사용자 지정 데이터 경로를 보존하고, 없으면 번들 GarminDB 예제에서 시작한다.
    /// 깨진 기존 파일은 JsonException으로 거부한다(기본값으로 덮어쓰지 않는다).
    public static string MergeGarminConfig(string? existing, string example, string email, string? password)
    {
        var root = JsonNode.Parse(existing ?? example) as JsonObject ?? throw new JsonException("Garmin 설정이 JSON 객체가 아닙니다.");
        if (root["credentials"] is not JsonObject credentials) root["credentials"] = credentials = new JsonObject();
        credentials["user"] = email;
        if (password is not null)
        {
            credentials["password"] = password;
            credentials["secure_password"] = false;
            credentials["password_file"] = null;
        }
        return root.ToJsonString(Json);
    }

    public static AppSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings), Json) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }   // 없거나 깨졌으면 설정 미완료
    }

    public static void SaveSettings(AppSettings settings) => AtomicWrite(Paths.Settings, JsonSerializer.Serialize(settings, Json));

    /// 내용을 쓰기 전에 접근을 제한·확인한 빈 임시 파일을 만든다 (프로필 ACL이 넓어도 평문이 노출되는 구간이 없게).
    internal static string CreateProtectedTemp(string path)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, []);
        SecretAcl.Protect(temp);   // 제한하지 못하면 예외: 내용을 쓰지 않는다
        return temp;
    }

    /// 보호된 임시 파일에 완성한 뒤 교체하고 직전 파일을 .bak 하나로 보관한다. 실패하면 임시 파일을 지운다.
    /// File.Replace는 기존 대상의 DACL을 새 내용에 그대로 남기므로(실행 확인), 기존 대상·백업을 먼저 보호한다.
    /// 그러면 교체 결과와 백업, Move한 임시 파일 모두 제한된 ACL을 가져 사후 보호가 필요 없다.
    public static void AtomicWrite(string path, string content)
    {
        string backup = path + ".bak";
        foreach (string existing in new[] { path, backup })
            if (File.Exists(existing)) SecretAcl.Protect(existing);   // 제한하지 못하면 예외: 새 내용을 쓰지 않는다
        string? temp = null;
        try
        {
            temp = CreateProtectedTemp(path);
            File.WriteAllText(temp, content, Utf8);   // 기존 파일을 덮어쓰므로 위에서 정한 ACL이 유지된다
            if (File.Exists(path)) File.Replace(temp, path, backup);
            else File.Move(temp, path);
        }
        finally
        {
            if (temp is not null) File.Delete(temp);   // 성공했으면 이미 없다
        }
    }

    static string? UserOf(string json) => (string?)JsonNode.Parse(json)?["credentials"]?["user"];

    /// 잠금을 잡은 상태에서 호출한다. 두 파일 저장과 권한 확인이 모두 끝나야 설정 완료로 표시한다.
    /// 파일 간 자동 롤백은 없다: 중간에 실패하면 미완료 상태로 남아 동기화가 막히고, 사용자가 다시 저장한다.
    /// dbPath는 연결 확인 결과의 garmin.db_path. 확인 뒤 수동 동기화가 DB를 처음 만들었을 수 있어 여기서 다시 막는다
    /// (run_batch.refuse_account_change와 같은 규칙).
    public static void SaveAll(AppSettings settings, string garminJson, string envText, string dbPath)
    {
        string? saved = File.Exists(Paths.GarminConfig) ? UserOf(File.ReadAllText(Paths.GarminConfig)) : null;
        if (!string.IsNullOrEmpty(saved) && !string.Equals(saved, UserOf(garminJson), StringComparison.OrdinalIgnoreCase) && File.Exists(dbPath))
            throw new InvalidOperationException("이 PC에는 다른 Garmin 계정의 운동 DB가 있어 계정을 바꿀 수 없습니다.");
        SaveSettings(settings with { ConfigComplete = false });
        Directory.CreateDirectory(Paths.GarminDbDir);
        SecretAcl.ProtectTree(Paths.GarminDbDir);   // 기존 설정·토큰·백업의 과도한 권한도 여기서 정리
        AtomicWrite(Paths.GarminConfig, garminJson);
        AtomicWrite(Paths.EnvFile, envText);
        // 연결 확인에서 받은 토큰은 설정이 저장된 뒤에만 운영 토큰이 된다. 저장하지 않으면 기존 계정 토큰이 그대로다.
        if (File.Exists(Paths.CheckToken)) File.Move(Paths.CheckToken, Paths.GarminToken, overwrite: true);
        SaveSettings(settings with { ConfigComplete = true });
    }

    public static JsonNode? ReadStatus()
    {
        try { return JsonNode.Parse(File.ReadAllText(Paths.Status)); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    /// Python이 상태를 쓰지 못한 경우(시간 초과로 종료, 시작 실패)에만 쓴다.
    public static void WriteFailureStatus(string mode, string message) => AtomicWrite(Paths.Status, new JsonObject
    {
        ["finished_at"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
        ["mode"] = mode, ["result"] = "failed", ["message"] = message, ["warnings"] = new JsonArray(),
    }.ToJsonString(Json));
}
```

`src/GarminBox/SecretAcl.cs`:

```csharp
using System.Security.AccessControl;
using System.Security.Principal;

namespace GarminBox;

/// 평문 자격증명 파일과 토큰 폴더의 접근을 현재 사용자·SYSTEM·Administrators로 제한한다. 암호화가 아니다.
static class SecretAcl
{
    static readonly SecurityIdentifier[] Allowed =
    [
        WindowsIdentity.GetCurrent().User!,
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    public static bool IsRestricted(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .All(rule => rule.AccessControlType == AccessControlType.Deny || Allowed.Contains((SecurityIdentifier)rule.IdentityReference));

    /// 토큰 폴더는 프로필 상속에 기대지 않고 명시적 상속 규칙을 둔다. 덕분에 나중에 임시 파일로 교체되는 토큰도 같은 권한을 받는다.
    /// 기존 설정·토큰·백업 파일의 명시적 과도한 권한은 파일별로 따로 정리한다.
    public static void ProtectTree(string directory)
    {
        Protect(directory, always: true);
        foreach (string file in Directory.EnumerateFiles(directory)) Protect(file);
    }

    public static void Protect(string path, bool always = false)
    {
        bool isDirectory = Directory.Exists(path);
        if (!always && IsRestricted(Read(path, isDirectory))) return;   // 프로필의 상속 ACL이 이미 충분하면 그대로 쓴다
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in Allowed)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (isDirectory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
        if (!IsRestricted(Read(path, isDirectory))) throw new UnauthorizedAccessException($"접근 권한을 제한하지 못했습니다: {path}");
    }

    static FileSystemSecurity Read(string path, bool isDirectory) =>
        isDirectory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
}
```

- [ ] **4단계: 통과 확인** — `dotnet test GarminBox.slnx` → 전체 통과
- [ ] **5단계: 커밋** — `feat: 설정 파일 원자적 저장과 비밀 파일 접근 제한`

---

### 작업 9: Python 실행기와 Job Object

**파일:** 생성 `src/GarminBox/PythonRunner.cs`, `src/GarminBox.Tests/runner_fixture.py`, `PythonRunnerTests.cs`

Job API를 바꿔 끼우는 테스트 구조는 만들지 않는다(설계). Job 연결 실패 분기는 코드 검토로, EXE 강제 종료는 작업 15에서 확인한다.

- [ ] **1단계: 실패하는 테스트**

`src/GarminBox.Tests/runner_fixture.py`:

```python
"""PythonRunner 검사용: run_batch.py와 같은 시작 신호·이벤트·MFA 규약을 흉내 낸다."""
import json
import subprocess
import sys

if sys.stdin.readline() != "START\n":
    sys.exit(3)


def emit(event, **data):
    print(json.dumps({"event": event, **data}, ensure_ascii=False), flush=True)


if sys.argv[-1] == "echo":
    emit("progress", message="한글 진행 메시지 ✓")
    emit("mfa_required")
    emit("done", code=sys.stdin.readline().strip())
elif sys.argv[-1] == "hang":
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"])
    emit("child", pid=child.pid)
    child.wait()
```

`PythonRunnerTests.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace GarminBox.Tests;

public class PythonRunnerTests
{
    static string Python => Path.Combine(Repo.Root, "build", "out", "runtime", "python", "python.exe");   // 작업 1
    static string Fixture => Path.Combine(Repo.Root, "src", "GarminBox.Tests", "runner_fixture.py");
    static readonly Dictionary<string, string> NoEnv = new();

    [Fact]
    public async Task KoreanEventsAndMfaReplyRoundTripAfterStart()
    {
        var events = new List<JsonElement>();
        var result = await PythonRunner.RunAsync(Python, Fixture, ["echo"], NoEnv, events.Add,
            _ => Task.FromResult<string?>("123456"), TimeSpan.FromSeconds(30), default);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("한글 진행 메시지 ✓", events[0].GetProperty("message").GetString());
        Assert.Equal("123456", events[^1].GetProperty("code").GetString());
    }

    [Fact]
    public async Task TimeoutKillsPythonAndGrandchild()
    {
        int child = 0;
        var result = await PythonRunner.RunAsync(Python, Fixture, ["hang"], NoEnv,
            e => { if (e.GetProperty("event").GetString() == "child") child = e.GetProperty("pid").GetInt32(); },
            _ => Task.FromResult<string?>(null), TimeSpan.FromSeconds(5), default);
        Assert.True(result.TimedOut);
        Assert.NotEqual(0, child);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(child));   // 손자도 Job과 함께 종료
    }

    [Fact]
    public async Task TimeoutDuringMfaPromptReturnsAndCancelsPrompt()
    {
        CancellationToken prompt = default;
        var run = PythonRunner.RunAsync(Python, Fixture, ["echo"], NoEnv, _ => { },
            token => { prompt = token; return new TaskCompletionSource<string?>().Task; },   // 사용자가 입력하지 않는 대화상자
            TimeSpan.FromSeconds(1), default);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15))));   // 입력을 기다리며 멈추지 않는다
        Assert.True((await run).TimedOut);
        Assert.True(prompt.IsCancellationRequested);   // 화면은 이 토큰으로 대화상자를 닫는다
    }
}
```

- [ ] **2단계: 실패 확인** — 컴파일 오류

- [ ] **3단계: 구현** (`src/GarminBox/PythonRunner.cs`)

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GarminBox;

sealed record RunResult(int ExitCode, bool TimedOut, bool Cancelled, string Stderr);

static class PythonRunner
{
    static readonly UTF8Encoding Utf8 = new(false);

    /// run_batch.py를 Job에 넣어 실행한다. 콜백은 호출자 컨텍스트(GUI에서는 UI 스레드)에서 불린다 — ConfigureAwait(false)를 쓰지 않는다.
    public static async Task<RunResult> RunAsync(string python, string script, IEnumerable<string> args,
        IReadOnlyDictionary<string, string> env, Action<JsonElement> onEvent, Func<CancellationToken, Task<string?>> askMfa,
        TimeSpan timeout, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            // Windows 기본 인코딩이면 한글 메시지가 깨지고 JSON 해석이 실패한다 (실행 검증 기록)
            StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
        };
        foreach (string arg in new[] { "-I", "-X", "utf8", script, "--await-start" }.Concat(args)) start.ArgumentList.Add(arg);
        foreach (var (key, value) in env) start.Environment[key] = value;   // 비밀값은 인자가 아니라 자식 환경으로만

        using var job = Job.Create();
        using var process = Process.Start(start)!;
        try { job.Assign(process); }
        catch
        {
            process.Kill();            // 시작 신호 전이라 Python은 아무것도 하지 않았다
            process.WaitForExit();
            throw;
        }
        process.StandardInput.NewLine = "\n";
        await process.StandardInput.WriteLineAsync("START");   // Job 연결 성공 후에만
        await process.StandardInput.FlushAsync();
        var stderr = process.StandardError.ReadToEndAsync();   // 파이프가 차지 않게 읽는다. 정제 전이라 파일에 쓰지 않는다.

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        // 동기 파이프 읽기는 취소 토큰으로 끊기지 않을 수 있다. 시간 초과·취소 때 Job 전체(Python과 CLI·업로드·gist 자식)를
        // 끝내면 파이프가 닫혀 EOF로 빠져나온다.
        using (limit.Token.Register(job.Terminate))
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonElement ev;
                try { ev = JsonDocument.Parse(line).RootElement.Clone(); }
                catch (JsonException) { ev = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["event"] = "text", ["line"] = line }); }
                if (ev.TryGetProperty("event", out var name) && name.GetString() == "mfa_required")
                {
                    // 시간 초과·취소면 코드 입력을 기다리지 않는다. 대화상자는 토큰으로 닫히고, Job은 이미 종료 중이라
                    // 아래 쓰기는 IOException으로 넘어가고 EOF 뒤 WaitEmpty로 자식 종료를 확인한다.
                    string? code;
                    try { code = await askMfa(limit.Token).WaitAsync(limit.Token); }
                    catch (OperationCanceledException) { code = null; }
                    try
                    {
                        await process.StandardInput.WriteLineAsync(code ?? "");
                        await process.StandardInput.FlushAsync();
                    }
                    catch (IOException) { }   // 코드를 기다리는 동안 시간 초과로 종료된 경우
                }
                onEvent(ev);
            }
            await process.WaitForExitAsync();
        }
        if (limit.IsCancellationRequested)
        {
            job.WaitEmpty(TimeSpan.FromSeconds(10));   // 자식 종료를 확인한 뒤 돌아가야 호출자가 잠금을 푼다
            return new(-1, !cancel.IsCancellationRequested, cancel.IsCancellationRequested, "");
        }
        return new(process.ExitCode, false, false, await stderr);
        // using job: 마지막 핸들이 닫히면(EXE가 finally 없이 죽어도) OS가 남은 프로세스를 종료한다
    }
}

/// KILL_ON_JOB_CLOSE Job. 핸들은 상속하지 않고(보안 속성 null) breakaway도 허용하지 않아 자식·손자가 모두 같은 Job에 속한다.
sealed class Job : IDisposable
{
    const int ExtendedLimitInformation = 9, BasicAccountingInformation = 1;
    const uint KillOnJobClose = 0x2000;
    readonly SafeFileHandle handle;

    Job(SafeFileHandle handle) => this.handle = handle;

    public static Job Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception();
        var info = new ExtendedLimit();
        info.Basic.LimitFlags = KillOnJobClose;
        if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimit>()))
        {
            var error = new Win32Exception();
            handle.Dispose();
            throw error;
        }
        return new Job(handle);
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception();
    }

    public void Terminate() => TerminateJobObject(handle, 1);

    public void WaitEmpty(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until && QueryInformationJobObject(handle, BasicAccountingInformation, out Accounting info, Marshal.SizeOf<Accounting>(), IntPtr.Zero) && info.ActiveProcesses > 0)
            Thread.Sleep(100);
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, int length);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting info, int length, IntPtr returned);
}
```

- [ ] **4단계: 통과 확인** — `dotnet test GarminBox.slnx` → 전체 통과
- [ ] **5단계: 커밋** — `feat: Job Object에 묶인 Python 실행과 시작 신호`

---

### 작업 10: 설치 — payload 무결성·압축 해제·EXE 복사·시작 메뉴

**파일:** 생성 `src/GarminBox/Installer.cs`, `src/GarminBox.Tests/InstallerTests.cs`

- [ ] **1단계: 실패하는 테스트**

```csharp
using System.IO.Compression;
using System.Security.Cryptography;

namespace GarminBox.Tests;

public class InstallerTests
{
    static byte[] Zip()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("app/run_batch.py").Open()))
            writer.Write("print(1)");
        return memory.ToArray();
    }

    [Fact]
    public void WrongHashChangesNothing()
    {
        using var dir = new TempDir();
        string runtime = Path.Combine(dir.Root, "runtime");
        Assert.Throws<InvalidDataException>(() => Installer.ExtractRuntime(new MemoryStream(Zip()), new string('0', 64), runtime));
        Assert.False(Directory.Exists(runtime));
    }

    [Fact]
    public void ExtractsThenMarksCurrent()
    {
        using var dir = new TempDir();
        string runtime = Path.Combine(dir.Root, "runtime");
        byte[] zip = Zip();
        string sha = Convert.ToHexStringLower(SHA256.HashData(zip));
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "stale.txt"), "old");   // 이전 버전

        Installer.ExtractRuntime(new MemoryStream(zip), sha, runtime);

        Assert.True(File.Exists(Path.Combine(runtime, "app", "run_batch.py")));
        Assert.False(File.Exists(Path.Combine(runtime, "stale.txt")));
        Assert.True(Installer.IsCurrent(runtime, sha));
    }

    [Fact]
    public void RuntimeWithoutMarkerIsNotComplete()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Root, "runtime", "python"));   // 강제 종료로 남은 부분 파일
        Assert.False(Installer.IsCurrent(Path.Combine(dir.Root, "runtime"), "abc"));
    }
}
```

- [ ] **2단계: 실패 확인** — 컴파일 오류

- [ ] **3단계: 구현** (`src/GarminBox/Installer.cs`)

```csharp
using System.IO.Compression;
using System.Security.Cryptography;

namespace GarminBox;

static class Installer
{
    const string Marker = "payload.sha256";

    /// 잠금을 잡은 상태에서 호출한다 (동기화 중인 코드를 덮어쓰지 않게). 런타임 표식이 맞으면 압축 해제를 건너뛴다.
    public static void Deploy()
    {
        var assembly = typeof(Installer).Assembly;
        using var hash = assembly.GetManifestResourceStream("payload.sha256")
            ?? throw new InvalidOperationException("이 빌드에는 Python 번들이 없습니다. build\\release.ps1로 만든 EXE를 쓰세요.");
        string expected = new StreamReader(hash).ReadToEnd().Trim();
        if (!IsCurrent(Paths.Runtime, expected))
        {
            using var zip = assembly.GetManifestResourceStream("payload.zip")!;
            ExtractRuntime(zip, expected, Paths.Runtime);
        }
        CopySelf();
        CreateShortcut();
    }

    public static bool IsCurrent(string runtime, string sha256)
    {
        string marker = Path.Combine(runtime, Marker);
        return File.Exists(marker) && File.ReadAllText(marker).Trim() == sha256;
    }

    public static void ExtractRuntime(Stream zip, string expectedSha256, string runtime)
    {
        string archive = runtime + ".zip.tmp", next = runtime + ".new", old = runtime + ".old";
        try
        {
            using (var file = File.Create(archive)) zip.CopyTo(file);
            string actual;
            using (var file = File.OpenRead(archive)) actual = Convert.ToHexStringLower(SHA256.HashData(file));
            if (actual != expectedSha256) throw new InvalidDataException("Python 번들 무결성 확인에 실패했습니다. 프로그램을 다시 내려받으세요.");
            if (Directory.Exists(next)) Directory.Delete(next, true);
            ZipFile.ExtractToDirectory(archive, next);
            if (Directory.Exists(old)) Directory.Delete(old, true);
            if (Directory.Exists(runtime)) Directory.Move(runtime, old);
            Directory.Move(next, runtime);
            File.WriteAllText(Path.Combine(runtime, Marker), expectedSha256);   // 표식은 마지막: 중간에 끊기면 다음 실행에서 다시 푼다
            if (Directory.Exists(old)) Directory.Delete(old, true);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    /// 다운로드 폴더의 EXE를 옮겨도 예약 작업이 깨지지 않게 고정 경로로 복사한다.
    static void CopySelf()
    {
        string self = Path.GetFullPath(Environment.ProcessPath!);
        if (string.Equals(self, Paths.Exe, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            File.Copy(self, Paths.Exe + ".new", overwrite: true);
            File.Move(Paths.Exe + ".new", Paths.Exe, overwrite: true);
        }
        catch (IOException e)
        {
            throw new IOException($"설치된 garmin-box가 열려 있어 바꾸지 못했습니다. 열린 창을 닫고 다시 실행하세요. ({e.Message})", e);
        }
    }

    static void CreateShortcut()
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(Paths.StartMenuLink);
        link.TargetPath = Paths.Exe;
        link.WorkingDirectory = Paths.InstallDir;
        link.Description = "garmin-box 설정";
        link.Save();
    }
}
```

- [ ] **4단계: 통과 확인** — `dotnet test GarminBox.slnx`
- [ ] **5단계: 커밋** — `feat: Python 번들 무결성 확인과 고정 경로 설치`

---

### 작업 11: 작업 스케줄러 등록·재확인·기존 작업 탐지

**파일:** 생성 `src/GarminBox/Scheduler.cs`, `src/GarminBox.Tests/SchedulerTests.cs`

- [ ] **1단계: 실패하는 테스트**

```csharp
namespace GarminBox.Tests;

public class SchedulerTests
{
    const string Exe = @"C:\Users\me\AppData\Local\garmin-box\GarminBox.exe";
    static readonly TimeOnly At = new(9, 10);
    static string Built() => Scheduler.BuildXml(Exe, At, @"PC\me", new DateOnly(2026, 10, 29));

    static string Task(string command, string arguments) =>
        $"""<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Actions><Exec><Command>{command}</Command><Arguments>{arguments}</Arguments></Exec></Actions></Task>""";

    [Fact]
    public void BuiltDefinitionPassesReadBackCheck() => Assert.Empty(Scheduler.Problems(Built(), Exe, At));

    [Fact]
    public void DefaultsWindowsMayOmitAreReadAsDefaults()
    {
        Assert.Empty(Scheduler.Problems(Built().Replace("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", ""), Exe, At));
        Assert.Contains("DisallowStartIfOnBatteries=true",
            Scheduler.Problems(Built().Replace("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", ""), Exe, At));
    }

    [Theory]
    [InlineData("<ExecutionTimeLimit>PT1H</ExecutionTimeLimit>", "<ExecutionTimeLimit>PT72H</ExecutionTimeLimit>")]
    [InlineData("<StartWhenAvailable>true</StartWhenAvailable>", "")]
    [InlineData("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", "")]
    [InlineData("<LogonType>InteractiveToken</LogonType>", "<LogonType>Password</LogonType>")]
    public void UnsafeDefinitionsAreRejected(string from, string to) =>
        Assert.NotEmpty(Scheduler.Problems(Built().Replace(from, to), Exe, At));

    [Theory]
    [InlineData("wscript.exe", "\"C:\\x\\garmin-box\\run_garmin_batch_hidden.vbs\"", true)]
    [InlineData("powershell", "-ExecutionPolicy Bypass -File C:\\x\\run_garmin_batch.ps1", true)]
    [InlineData("notepad.exe", "", false)]
    public void LegacyBatchTasksAreFound(string command, string arguments, bool legacy) =>
        Assert.Equal(legacy, Scheduler.IsLegacyBatch(Task(command, arguments)));

    [Fact]
    public void OnlyOurExecutableCountsAsOurs()
    {
        Assert.True(Scheduler.IsOurs(Task(Exe.ToUpperInvariant(), "--sync --scheduled"), Exe));
        Assert.False(Scheduler.IsOurs(Task(@"C:\other\tool.exe", ""), Exe));
    }
}
```

- [ ] **2단계: 실패 확인** — 컴파일 오류

- [ ] **3단계: 구현** (`src/GarminBox/Scheduler.cs`)

```csharp
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GarminBox;

sealed record ScheduleOutcome(bool Registered, string Message, DateTime? NextRun);

static class Scheduler
{
    public const string TaskName = "garmin-box";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// 현재 사용자·일반 권한·로그인 상태에서만 실행. 1시간 제한은 멈춘 실행이 다음 실행을 막지 않게 하는 마지막 안전장치다.
    public static string BuildXml(string exe, TimeOnly at, string userId, DateOnly startDay) => $"""
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>garmin-box 매일 동기화</Description></RegistrationInfo>
          <Triggers><CalendarTrigger><StartBoundary>{startDay:yyyy-MM-dd}T{at:HH:mm}:00</StartBoundary><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(userId)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--sync --scheduled</Arguments></Exec></Actions>
        </Task>
        """;

    /// 등록 후 다시 읽은 정의 검사. Windows는 기본값과 같은 항목을 생략할 수 있어 없으면 기본값으로 읽는다.
    public static List<string> Problems(string xml, string exe, TimeOnly at)
    {
        var task = XDocument.Parse(xml).Root!;
        var settings = task.Element(Ns + "Settings");
        var principal = task.Element(Ns + "Principals")?.Element(Ns + "Principal");
        var problems = new List<string>();
        void Expect(XElement? parent, string name, string fallback, string want)
        {
            string value = parent?.Element(Ns + name)?.Value.Trim() ?? fallback;
            if (!value.Equals(want, StringComparison.OrdinalIgnoreCase)) problems.Add($"{name}={value}");
        }
        Expect(settings, "DisallowStartIfOnBatteries", "true", "false");
        Expect(settings, "StopIfGoingOnBatteries", "true", "false");
        Expect(settings, "StartWhenAvailable", "false", "true");
        Expect(settings, "ExecutionTimeLimit", "PT72H", "PT1H");
        Expect(settings, "MultipleInstancesPolicy", "IgnoreNew", "IgnoreNew");
        Expect(settings, "Enabled", "true", "true");
        Expect(principal, "LogonType", "", "InteractiveToken");
        Expect(principal, "RunLevel", "LeastPrivilege", "LeastPrivilege");
        if (!IsOurs(xml, exe)) problems.Add("실행 파일");
        if (task.Descendants(Ns + "Arguments").FirstOrDefault()?.Value.Trim() != "--sync --scheduled") problems.Add("인수");
        if (task.Descendants(Ns + "StartBoundary").FirstOrDefault()?.Value.Contains($"T{at:HH:mm}") != true) problems.Add("실행 시각");
        if (task.Descendants(Ns + "DaysInterval").FirstOrDefault()?.Value.Trim() != "1") problems.Add("매일 반복");
        return problems;
    }

    static IEnumerable<XElement> Execs(string xml) => XDocument.Parse(xml).Descendants(Ns + "Exec");

    public static bool IsOurs(string xml, string exe) => Execs(xml).Any(exec => string.Equals(
        Environment.ExpandEnvironmentVariables(exec.Element(Ns + "Command")?.Value.Trim().Trim('"') ?? ""), exe, StringComparison.OrdinalIgnoreCase));

    /// 예전 README는 작업 이름을 정하지 않았으므로 실행 동작·인수의 런처 파일명으로 찾는다.
    public static bool IsLegacyBatch(string xml) => Execs(xml).Any(exec =>
        Regex.IsMatch(exec.Value, @"run_garmin_batch(_hidden\.vbs|\.ps1)", RegexOptions.IgnoreCase));

    public static ScheduleOutcome Register(string exe, TimeOnly at)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        dynamic root = service.GetFolder(@"\");
        // root가 dynamic이라 호출 결과도 dynamic이 된다. 정적 타입으로 먼저 받아야 LINQ 람다를 쓸 수 있다 (CS1977).
        List<(string Path, bool Enabled)> tasks = LegacyTasks(root);
        var legacy = tasks.Where(t => t.Enabled).Select(t => t.Path).ToList();
        if (legacy.Count > 0)   // 자동 삭제·이전하지 않고 안내만 한다
            return new(false, "예전 수동 예약 작업이 켜져 있어 새 예약을 켜지 않았습니다. 작업 스케줄러에서 다음 작업을 '사용 안 함'으로 바꾼 뒤 '예약 다시 등록'을 누르세요: "
                + string.Join(", ", legacy), null);
        dynamic? existing = null;
        try { existing = root.GetTask(TaskName); } catch (COMException) { }   // 없으면 0x80070002
        if (existing is not null && !IsOurs((string)existing.Xml, exe))
            return new(false, $"같은 이름({TaskName})의 다른 예약 작업이 있어 덮어쓰지 않았습니다.", null);
        string user = WindowsIdentity.GetCurrent().Name;
        root.RegisterTask(TaskName, BuildXml(exe, at, user, DateOnly.FromDateTime(DateTime.Today)), 6 /* CREATE_OR_UPDATE */, user, null, 3 /* INTERACTIVE_TOKEN */, null);
        dynamic task = root.GetTask(TaskName);
        var problems = Problems((string)task.Xml, exe, at);
        return problems.Count > 0
            ? new(false, "등록한 예약 작업의 설정이 예상과 다릅니다: " + string.Join(", ", problems), null)
            : new(true, "매일 자동 실행을 등록했습니다.", (DateTime)task.NextRunTime);
    }

    static List<(string Path, bool Enabled)> LegacyTasks(dynamic folder)
    {
        var found = new List<(string, bool)>();
        try
        {
            foreach (dynamic task in folder.GetTasks(1 /* 숨김 포함 */))
                if (IsLegacyBatch((string)task.Xml)) found.Add(((string)task.Path, (bool)task.Enabled));
            foreach (dynamic sub in folder.GetFolders(0)) found.AddRange(LegacyTasks(sub));
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException) { }   // 읽을 수 없는 시스템 폴더는 건너뛴다
        return found;
    }
}
```

- [ ] **4단계: 통과 확인** — `dotnet test GarminBox.slnx`
- [ ] **5단계: 커밋** — `feat: 작업 스케줄러 등록과 정의 재확인, 기존 수동 작업 탐지`

---

### 작업 12: 화면과 실행 진입점

**파일:** 생성 `src/GarminBox/MainForm.cs` · 수정 `src/GarminBox/Program.cs`

UI 배치·색상은 구현자 판단(설계). 아래 동작은 지킨다.

- [ ] **1단계: `Program.cs`**

```csharp
namespace GarminBox;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool scheduled = args.Contains("--scheduled");
        if (Paths.Preflight() is { } problem)
        {
            if (!scheduled) MessageBox.Show(problem, "garmin-box", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        if (args.Contains("--sync")) return Sync(scheduled);
        // 설정 창은 하나만: 창마다 확인한 토큰이 같은 확인용 파일을 덮어쓰지 않게 한다. 작업 잠금과 달리 창이 열린 동안 잡는다.
        using var window = AppLock.TryAcquire(@"Local\garmin-box-window");
        if (window is null)
        {
            MessageBox.Show("garmin-box 설정 창이 이미 열려 있습니다.", "garmin-box");
            return 1;
        }
        Application.Run(new MainForm());
        return 0;
    }

    /// 예약 작업·수동 런처 경로. MFA가 필요하면 기다리지 않고 재인증 필요로 끝난다.
    static int Sync(bool scheduled)
    {
        int code;
        using (var held = AppLock.TryAcquire())
        {
            if (held is null) return 1;   // 다른 작업 진행 중: 새로 시작하지 않는다
            try
            {
                var result = PythonRunner.RunAsync(Paths.Python, Paths.RunBatch, ["sync", "--no-prompt"], new Dictionary<string, string>(),
                    _ => { }, _ => Task.FromResult<string?>(null), TimeSpan.FromMinutes(55), CancellationToken.None).GetAwaiter().GetResult();
                if (result.TimedOut) ConfigFiles.WriteFailureStatus("sync", "55분 안에 끝나지 않아 중단했습니다.");
                code = result.ExitCode;
            }
            catch (Exception e)
            {
                ConfigFiles.WriteFailureStatus("sync", $"동기화를 시작하지 못했습니다: {e.Message}");
                code = 1;
            }
        }   // 메시지 창이 잠금을 잡고 있지 않도록 먼저 해제
        if (!scheduled) MessageBox.Show((string?)ConfigFiles.ReadStatus()?["message"] ?? "결과를 읽지 못했습니다.", "garmin-box");
        return code;
    }
}
```

- [ ] **2단계: `MainForm.cs` 동작 구현**

필수 동작:

1. **시작(`OnLoad`)**: 잠금을 잡고 `Installer.Deploy()`. 잠금이 바쁘면 "동기화가 진행 중입니다"를 표시하고 배치는 건너뛴다. 실패하면 원인을 표시하고 버튼을 막는다. 그 뒤 저장된 설정을 불러오고 `status.json`의 최근 결과를 보여 준다.
2. **입력란**: Garmin 이메일, Garmin 비밀번호·Supabase 연결 문자열·GitHub 토큰(`UseSystemPasswordChar`), Gist URL/ID, 갱신할 gist 파일(편집 가능 ComboBox, 기본 `garmin-stats`), 매일 실행 시각(`HH:mm`, 기본 09:10). 시각 옆에 "로그인되어 있을 때만 실행합니다. PC가 꺼져 있었다면 로그인 후 실행합니다. Windows 비밀번호는 받지 않습니다."
3. **저장된 비밀값**: 보여 주지 않고 `PlaceholderText = "저장됨 (바꿀 때만 입력)"`. 빈 칸이면 기존 값을 쓴다.
4. **입력 변경**: 모든 입력란의 `TextChanged`에서 `inputVersion++`, 확인 결과를 버리고 `설정 시작`을 끈다. 확인 결과를 버릴 때(입력 변경·결과 폐기·창 닫기)는 `Paths.CheckToken`도 지운다.
5. **연결 확인**: 이메일을 저장값과 다르게 바꾸면서 비밀번호가 비었으면 막는다. 실행 전 `.GarminDb`를 만들고 `SecretAcl.ProtectTree` (확인 중 받는 토큰도 보호). 입력한 값만 자식 환경으로 넘기고 `check`를 실행한다. 끝났을 때 `inputVersion`이 바뀌었으면 결과와 확인용 토큰을 버린다. 항목별 성공·실패와 원인(`message`)을 표시한다(`kind=account`: 로컬 DB가 있는 PC의 다른 계정 — `run_batch.py`가 로그인 전에 거부). GitHub 결과로 파일 목록을 채우고(없으면 `garmin-stats` 추가) "{login}의 공개/비공개 gist · 갱신할 파일: {파일}"과 gist 링크를 저장 전 보여 준다. 운영 토큰은 `SaveAll`이 설정을 저장한 뒤에만 확인용 토큰으로 바뀐다. 확인용 토큰 파일은 하나이므로 설정 창도 하나만 연다(`Program.Main`의 `Local\garmin-box-window`). 저장할 때는 `garmin.db_path`를 `SaveAll`에 넘겨 잠금 안에서 계정 제약을 다시 확인한다.
6. **설정 시작** 옆 설명: "설정 파일 저장 → Supabase 테이블 생성 → 운동 기록 업로드 → 선택한 gist 파일 갱신 → 매일 자동 실행 등록". 잠금 안에서 `SaveInputs` → `setup`(제한 6시간, `취소` 버튼). 실패하면 그 단계에서 멈추고 다시 시도할 수 있다(만든 테이블·업로드한 기록은 지우지 않음). 성공하면 `RegisterSchedule()`.
7. **RegisterSchedule**: 성공이면 "설정 완료 · 첫 동기화 N건 · 다음 실행 yyyy-MM-dd HH:mm (로그인되어 있을 때만)"과 gist 링크. 실패면 "동기화 성공 / 자동 실행 설정 실패: {원인}"과 `예약 다시 등록` 버튼(누르면 gist 갱신 없이 등록만 재시도).
8. **지금 동기화**: 잠금 안에서 `sync`(제한 55분, MFA는 화면에서 입력). 끝나면 최근 결과 갱신.
9. **MFA**: `mfa_required` 이벤트에서 코드 입력 대화상자를 띄우고 한 줄을 돌려준다. 취소하면 빈 줄. `AskMfa(CancellationToken)`는 토큰이 취소되면(시간 초과·`취소` 버튼) 대화상자를 닫고 `null`을 돌려준다(`token.Register`로 UI 스레드에서 `Close`).
10. **진행 표시**: `progress`·`warning`·`failed` 이벤트를 진행 상자와 상태 표시에 쓴다. 상태가 `reauth`면 "Garmin 재인증 필요: 비밀번호 칸을 비운 채 연결 확인을 눌러 다시 로그인하세요."
11. **잠금 규칙**: 잠금은 UI 스레드에서 잡고 `await` 뒤 같은 UI 스레드에서 해제한다(WinForms 컨텍스트로 복귀). 핸들러에서 `ConfigureAwait(false)`를 쓰지 않는다. 대화상자 대신 상태 표시를 써서 잠금을 잡은 채 사용자를 기다리지 않는다(MFA 입력만 예외).

핵심 코드:

```csharp
    /// 잠금을 잡고 run_batch.py를 실행한다. 모든 작업은 잠금 획득 뒤 설정 완료·런타임 표식을 다시 확인하므로
    /// abandoned 잠금은 경고만 남긴다.
    async Task<RunResult?> RunLocked(string mode, Dictionary<string, string> env, TimeSpan timeout, Action? prepare = null)
    {
        using var held = AppLock.TryAcquire();
        if (held is null) { ShowFailure("다른 작업(동기화 또는 설정)이 진행 중입니다. 끝난 뒤 다시 시도하세요."); return null; }
        if (held.WasAbandoned) Append("이전 작업이 비정상 종료됐습니다. 설정 상태를 다시 확인하고 진행합니다.");
        SetBusy(true);
        running = new CancellationTokenSource();
        try
        {
            prepare?.Invoke();
            var result = await PythonRunner.RunAsync(Paths.Python, Paths.RunBatch, [mode], env, OnEvent, AskMfa, timeout, running.Token);
            if (result.TimedOut || result.Cancelled)
            {
                string why = result.Cancelled ? "취소했습니다." : "제한 시간 안에 끝나지 않아 중단했습니다.";
                if (mode != "check") ConfigFiles.WriteFailureStatus(mode, why);
                ShowFailure(why);
            }
            return result;
        }
        catch (Exception e) { ShowFailure(e.Message); return null; }
        finally { running.Dispose(); running = null; SetBusy(false); }
    }

    void SaveInputs(JsonElement check)
    {
        string? existing = File.Exists(Paths.GarminConfig) ? File.ReadAllText(Paths.GarminConfig) : null;
        string garmin = ConfigFiles.MergeGarminConfig(existing, File.ReadAllText(Paths.GarminExample), email.Text.Trim(),
            password.Text.Length > 0 ? password.Text : null);
        var env = File.Exists(Paths.EnvFile) ? ConfigFiles.ParseEnv(File.ReadAllText(Paths.EnvFile)) : new Dictionary<string, string>();
        if (dsn.Text.Trim().Length > 0) env["SUPA_DSN"] = dsn.Text.Trim();
        if (token.Text.Trim().Length > 0) env["GITHUB_TOKEN"] = token.Text.Trim();
        env["GIST_ID"] = check.GetProperty("github").GetProperty("gist_id").GetString()!;
        env["GIST_FILENAME"] = gistFile.Text.Trim();
        ConfigFiles.SaveAll(ConfigFiles.LoadSettings() with { ScheduleTime = runAt.Value.ToString("HH:mm") }, garmin, ConfigFiles.FormatEnv(env),
            check.GetProperty("garmin").GetProperty("db_path").GetString()!);
    }
```

- [ ] **3단계: 빌드 확인** — `dotnet build GarminBox.slnx -c Release` → 경고·오류 없음. `dotnet test GarminBox.slnx` 통과.
- [ ] **4단계: 개발 실행으로 화면 확인** — `build\release.ps1` 전이라 payload가 없으므로 시작 시 "이 빌드에는 Python 번들이 없습니다"가 표시되는지만 확인한다. 실제 흐름은 작업 15에서 publish한 EXE로 확인한다.
- [ ] **5단계: 커밋** — `feat: Windows 설정 화면과 예약 동기화 진입점`

---

### 작업 13: 수동 런처·README·기존 테스트 갱신

**파일:** 수정 `run_garmin_batch.ps1`, `run_garmin_batch_hidden.vbs`, `README.md`, `README.en.md`, `tests/test_docs_and_launcher.py`

- [ ] **1단계: 테스트 먼저 갱신** (`tests/test_docs_and_launcher.py`)

```python
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
        self.assertNotIn("Start-Transcript", ps1)          # 정제 전 출력을 파일에 남기지 않는다
        self.assertNotIn("garmin_tokens.json", ps1)        # 토큰 삭제 재시도는 EXE 경로에서 제거

    def test_english_readme_accepts_uri_dsn(self):
        self.assertNotIn("not a URL", (ROOT / "README.en.md").read_text(encoding="utf-8"))
```

- [ ] **2단계: 실패 확인** — `& $PY -I -X utf8 -m unittest discover -s tests -t . -p test_docs_and_launcher.py -v` → 실패

- [ ] **3단계: 런처 교체**

`run_garmin_batch.ps1`:

```powershell
# 설치된 garmin-box EXE로 수동 동기화한다 (Windows). 설치 전이면 안내만 한다.
$exe = Join-Path $env:LOCALAPPDATA 'garmin-box\GarminBox.exe'
if (-not (Test-Path $exe)) {
    Write-Host 'garmin-box가 설치되지 않았습니다. 릴리스의 GarminBox.exe를 먼저 실행해 설정하세요.'
    exit 1
}
$p = Start-Process -FilePath $exe -ArgumentList '--sync' -Wait -PassThru
exit $p.ExitCode
```

`run_garmin_batch_hidden.vbs`:

```vb
' 설치된 garmin-box EXE의 --sync를 창 없이 실행하는 수동 런처. 결과는 EXE가 보여 준다.
Set shell = CreateObject("WScript.Shell")
exe = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\garmin-box\GarminBox.exe"
If CreateObject("Scripting.FileSystemObject").FileExists(exe) Then
  shell.Run """" & exe & """ --sync", 0, False
Else
  MsgBox "garmin-box가 설치되지 않았습니다. 릴리스의 GarminBox.exe를 먼저 실행해 설정하세요."
End If
```

- [ ] **4단계: README 재작성** — 새 PC의 EXE 실행을 앞에 둔다.
  - `## 준비물`: Garmin 계정, Supabase 연결 문자열(URI·`host=... password=...` 모두 가능, 발급 링크), gist 권한만 있는 GitHub 토큰과 gist(발급·생성 링크). Python·Git 설치 불필요.
  - `## 1. 설치하기`: 릴리스에서 `GarminBox.exe` 내려받기 → 실행(코드 서명이 없어 SmartScreen이 뜨면 "추가 정보 → 실행") → 입력 → 연결 확인 → gist 파일 선택 → 설정 시작. 한글 사용자 이름 PC는 현재 설치할 수 없다는 제약을 적는다.
  - `## 2. 매일 자동 실행`: 로그인되어 있을 때만 실행, 꺼져 있던 동안의 실행은 로그인 후, 절전·전원 정책은 바꾸지 않음.
  - `## 3. 다시 열기`: 시작 메뉴 `garmin-box`, 설정 변경·지금 동기화·최근 결과, Garmin 재인증 방법, 삭제한 활동은 통계에 남을 수 있음.
  - `## 고급 설정`: 수동 런처(`run_garmin_batch.ps1`/`.vbs`는 설치된 EXE를 부름), 예전 수동 예약 작업 해제 방법, 기존 `### Linux / OCI`·`### 개발` 절 유지(개발 명령은 `-m unittest`와 `dotnet test`로).
- [ ] **5단계: `README.en.md`** — 같은 구조로 맞추고 DSN 설명을 "URI(`postgresql://...`) 또는 key=value conninfo 모두 가능"으로 고친다.
- [ ] **6단계: 통과 확인** — Python 전체 테스트 `OK`
- [ ] **7단계: 커밋** — `docs: EXE 설치 우선 안내와 수동 런처를 설치된 EXE 호출로 변경`

---

### 작업 14: 릴리스 빌드와 번들 검증 도구

**파일:** 생성 `build/release.ps1` · 수정 `tools/verify_windows_bundle.py`

- [ ] **1단계: 번들 검증이 새 테스트도 돌리게 수정** (`tools/verify_windows_bundle.py`)

```python
for file in (args.repo / "test_update_gist.py", *sorted((args.repo / "tests").glob("test_*.py"))):
```

- [ ] **2단계: `build/release.ps1`**

```powershell
# 릴리스 빌드: 런타임 → payload ZIP·해시 → 다른 경로에서 번들 검증 → 전체 테스트 → self-contained 단일 EXE
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out'
& (Join-Path $PSScriptRoot 'build-runtime.ps1')

$runtime = Join-Path $out 'runtime'
$app = New-Item -ItemType Directory -Force (Join-Path $runtime 'app')
Copy-Item (Join-Path $root 'run_batch.py'), (Join-Path $root 'sync_supa.py'), (Join-Path $root 'update_gist.py'), (Join-Path $root 'schema.sql') $app
$payload = New-Item -ItemType Directory -Force (Join-Path $root 'src\GarminBox\payload')
$zip = Join-Path $payload 'payload.zip'
Remove-Item $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($runtime, $zip)
(Get-FileHash $zip -Algorithm SHA256).Hash.ToLower() | Set-Content -NoNewline -Encoding ascii (Join-Path $payload 'payload.sha256')

# 빌드 경로와 다른 곳에 풀어 경로 고정·네이티브 DLL 누락을 확인한다
$check = Join-Path $out 'relocated-check'
if (Test-Path $check) { Remove-Item -Recurse -Force $check }
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $check)
$py = Join-Path $check 'python\python.exe'
& $py -I -X utf8 (Join-Path $root 'tools\verify_windows_bundle.py') --repo $root --output (Join-Path $out 'bundle-result.json')
if ($LASTEXITCODE) { throw 'bundle verification failed' }

Push-Location $root
try {
    & $py -I -X utf8 -m unittest discover -s tests -t . -v
    if ($LASTEXITCODE) { throw 'python tests failed' }
    dotnet test GarminBox.slnx -c Release
    if ($LASTEXITCODE) { throw 'dotnet tests failed' }
} finally { Pop-Location }

dotnet publish (Join-Path $root 'src\GarminBox\GarminBox.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -o (Join-Path $out 'publish')
if ($LASTEXITCODE) { throw 'publish failed' }
Get-FileHash (Join-Path $out 'publish\GarminBox.exe') -Algorithm SHA256
```

- [ ] **3단계: 실행** — `powershell -ExecutionPolicy Bypass -File build\release.ps1`
  기대: `bundle-result.json`의 `status=passed`, Python·C# 테스트 통과, `build\out\publish\GarminBox.exe` 생성과 SHA-256 출력.
- [ ] **4단계: 커밋** — `chore: 릴리스 빌드 스크립트와 번들 검증 테스트 범위 확대`

---

### 작업 15: Windows 실행 검증과 기록

**파일:** 수정 `docs/superpowers/verification/2026-10-29-windows-verification.md` (제품 구현 검증 절 추가)

자동 테스트로 대신할 수 없는 항목이다. 실행하지 못한 항목은 **미검증**으로 남기고, 빌드 성공이나 테스트 대역 통과를 실제 설치 완료로 보고하지 않는다.

- [ ] **1단계: 일반 사용자 계정·화면**: publish한 EXE를 다운로드 폴더에서 실행 → `%LOCALAPPDATA%\garmin-box`에 EXE·`runtime` 배치, 시작 메뉴 바로가기 생성(바탕화면 없음). 가림 입력, 입력 변경 시 확인 결과 무효화, 오류·재시도 문구, 진행 표시, 한국어, 화면 배율 100%/150%.
- [ ] **2단계: 비 ASCII 경로 차단**: `$env:LOCALAPPDATA='C:\temp\한글'; .\GarminBox.exe` → 설계 문구와 실제 경로가 표시되고 아무 파일도 생기지 않는다.
- [ ] **3단계: 실제 계정 첫 동기화 (사용자 입력 필요)**: 연결 확인(MFA 포함) → 설정 시작 → 단계·건수 표시 → Supabase 행 수와 원격 건수 대조 → gist 갱신 → 예약 등록과 다음 실행 시각.
- [ ] **4단계: 토큰 폴백**: `garmin_tokens.json`을 깨진 내용으로 바꾼 뒤 `--sync` → 자격증명 폴백으로 성공하는지. 성공하면 Windows 경로의 토큰 삭제 재시도 제거가 맞다는 근거로 기록한다.
- [ ] **5단계: 예약 실행**: 작업 스케줄러에서 `garmin-box`를 즉시 실행 → 콘솔 창 없음, `status.json` 갱신. 정의의 배터리·`PT1H`·`StartWhenAvailable`·`IgnoreNew`·`InteractiveToken`을 다시 확인.
- [ ] **6단계: 강제 종료**: 지금 동기화 중 작업 관리자로 `GarminBox.exe` 종료 → `python.exe`(CLI 손자 포함)가 모두 사라지는지. 다시 열었을 때 abandoned 경고 후 정상 진행.
- [ ] **7단계: 중복 실행**: 지금 동기화 중 수동 런처·예약 실행 → 새 작업이 시작되지 않음. 설정 창이 열린 채 EXE를 다시 실행 → "설정 창이 이미 열려 있습니다" 후 종료. MFA 입력 창을 띄운 채 `취소` → 입력 창이 닫히고 바로 다음 작업을 할 수 있음.
- [ ] **8단계: 기존 수동 작업**: 예전 README 방식의 VBS 예약 작업을 만든 상태에서 설정 시작 → 새 예약을 켜지 않고 해제 안내, 해제 후 `예약 다시 등록` 성공.
- [ ] **9단계: 깨끗한 Windows**: Python/Git/gh/.NET이 없는 Windows 10/11 VM에서 1·3·5단계. VM이 없으면 미검증으로 기록.
- [ ] **10단계: 기록·커밋** — 항목별 결과, 버전·EXE SHA-256, 미검증 항목을 기록 · `docs: Windows 설정 EXE 실행 검증 기록`

---

## 설계 요구사항 대응

| 설계의 자동 검증 항목 | 작업 |
|---|---|
| 비 ASCII 설치 경로 사전 안내·중단 | 7 |
| 기존 JSON 설정 보존, 실행 시 DB 경로 계산 | 8, 5(`activities_db`) |
| `.env`의 `=`/Unicode/빈값 | 2, 8 |
| 일부 파일 저장 실패 시 실행 차단 | 8, 2(`not_configured`) |
| 토큰 교체 후 폴더 ACL 상속, 기존 명시적 권한 | 8 |
| 비밀값 제거 | 2 |
| Gist URL/파일 선택 | 6, 12 |
| 감싸진 MFA 예외의 플래그 판별 | 4 |
| `-I` 모드 하위 스크립트 실행 | 3 |
| 실제 0건, 필수 요약 실패/선택적 FIT 실패 구분 | 4, 5 |
| 25개 초과 누락의 전체 보충, 동일 건수 ID 누락 차단 | 5 |
| 기존 SQLite ID가 완전해도 CLI 비정상 종료·0 종료 로그인 실패 차단 (리뷰 반영) | 5 |
| 다른 계정 확인 거부 시 운영 토큰 보존, 저장 시에만 확인용 토큰 교체 (리뷰 반영) | 6, 8 |
| 비밀 임시 파일을 내용 전에 보호, 실패 시 제거, 백업 보호 (리뷰 반영) | 8 |
| 설정 창 하나만 열기, 저장 시점 계정 제약 재확인 (2차 리뷰 반영) | 6, 8, 12, 15 |
| `File.Replace` 전에 기존 대상·백업 보호 (2차 리뷰 반영) | 8 |
| MFA 입력 대기 중 시간 초과·취소 시 반환 (2차 리뷰 반영) | 9, 12, 15 |
| 로그인 뒤 목록 실패는 ID 대조로 판별 (2차 리뷰 검토, 변경 없음) | 5 |
| 원격 삭제·원격 0건 여분 기록 경고 후 진행 | 5 |
| SQL·변환 오류 시 롤백과 gist 차단 | 5 |
| EXE 경유 작업의 mutex, abandoned 복구 | 7, 12, 15 |
| `--await-start` EOF 차단·정상 신호 후 MFA·미지정 시 대기 없음 | 2, 9 |
| 기존 작업 탐지, 배터리·유한 실행 시간·놓친 실행 재확인 | 11 |
| Job 연결 실패 분기(코드 검토), EXE 강제 종료 시 자식·손자 종료(실행 검증) | 9, 15 |

## 이번 계획에서 하지 않는 것

- 직접 페이징(단일 요청이 잘리는 계정이 확인될 때), 한글 경로 지원(`activeCodePage` 매니페스트는 검증 후), garmin.cn 연결 확인, 로그 보관 개수 제한, wheel 해시 고정, 코드 서명, 자동 업데이트, 삭제 전파 — 설계의 범위 밖 또는 미검증 항목.
- Linux/OCI의 `run_garmin_batch.sh`는 그대로 둔다(토큰 삭제 재시도 포함). Windows 경로만 EXE로 바꾼다.
- 릴리스 업로드(`gh release create`)는 사용자가 결정한다.
