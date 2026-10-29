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


class StartupFailureTests(unittest.TestCase):
    def test_unreadable_env_still_reports_failure_and_status(self):
        """초기화 실패도 failed 이벤트와 status.json을 남겨, 예약 실행 실패가 이전 성공으로 보이지 않게 한다."""
        with tempfile.TemporaryDirectory() as tmp, redirect_stdout(io.StringIO()) as out:
            env = Path(tmp) / ".garmin-box.env"
            env.write_bytes("SUPA_DSN=한글".encode("cp949"))   # 예전 PS 5.1이 ANSI로 저장한 파일
            with mock.patch.object(run_batch, "ENV_FILE", env), mock.patch.dict(os.environ, {"LOCALAPPDATA": tmp}):
                code = run_batch.main(["sync"])
            status = json.loads((Path(tmp) / "garmin-box" / "status.json").read_text(encoding="utf-8"))
        self.assertEqual(code, run_batch.EXIT_FAIL)
        self.assertEqual(status["result"], "failed")
        self.assertIn('"failed"', out.getvalue())


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


class ConnectionWrappingGarmin(FakeGarmin):
    """garminconnect 0.3.11 Garmin.login처럼 MFA 콜백의 예외를 GarminConnectConnectionError로 감싼다."""

    def login(self, tokenstore=None):
        from garminconnect import GarminConnectConnectionError
        try:
            self.code = self.prompt_mfa()
        except Exception as error:
            raise GarminConnectConnectionError(f"Login failed: {error}") from error


class GarminLoginTests(unittest.TestCase):
    def config(self, tmp):
        return run_batch.EnteredLogin("me@example.com", "pw", Path(tmp) / "tokens.json")

    def test_empty_mfa_code_is_auth_not_network(self):
        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(sys, "stdin", io.StringIO("\n")), \
                redirect_stdout(io.StringIO()), self.assertRaises(Failure) as caught:
            run_batch.garmin_login(self.config(tmp), True, ConnectionWrappingGarmin)
        self.assertEqual(caught.exception.kind, "auth")   # '인터넷 연결 확인'이 아니라 '인증 코드 미입력'

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

    def run_cli(self, tmp, code, log_text=""):
        def run(args, env, log, timeout, on_line=None, cwd=None):
            Path(cwd, "garmindb.log").write_text(log_text, encoding="utf-8")
            return code
        return lambda latest: run_batch.run_garmindb(["garmindb_cli.py"], {}, print, 60, Path(tmp), run)

    def test_failed_cli_blocks_even_when_ids_are_complete(self):
        # 비정상 종료 / Download.login()이 GarminConnectAuthError를 삼킨 뒤 sys.exit()한 0 종료
        for code, log_text in ((42, ""), (0, "ERROR:root:login failed: 401\nERROR:garmindb_cli.py:Failed to login!\n")):
            with self.subTest(code=code), tempfile.TemporaryDirectory() as tmp, \
                    redirect_stdout(io.StringIO()), self.assertRaises(Failure):
                run_batch.collect({"1"}, False, self.run_cli(tmp, code, log_text), lambda: {"1"})

    def test_logged_in_cli_with_complete_ids_passes(self):
        with tempfile.TemporaryDirectory() as tmp, redirect_stdout(io.StringIO()):
            local = run_batch.collect({"1"}, False, self.run_cli(tmp, 0, "INFO:root:Getting activities\n"), lambda: {"1"})
        self.assertEqual(local, {"1"})

    def test_failed_summaries_reads_real_garmindb_log_records(self):
        """GarminDB 3.9.0 실제 로그 형식. 보조 함수의 DB 무관 오류(러닝 다이내믹스 없는 달리기)는 정상 import에서도 나온다."""
        log = (
            "ERROR:root:Exception in _process_running from 3 GarminJsonSummaryData: "
            "'NoneType' object has no attribute 'meters_or_feet'\n"
            "INFO:root:DB updated with 1 entries from C:\\x\\Activities/activity_3.json\n"
            "ERROR:root:Exception in _process_running from 1 GarminJsonSummaryData: (raised as a result of Query-invoked "
            "autoflush; consider using a session.no_autoflush block if this flush is occurring prematurely)\n"
            "(sqlite3.OperationalError) database is locked\n"
            "(Background on this error at: https://sqlalche.me/e/20/e3q8)\n"
            "ERROR:root:Failed to parse C:\\x\\Activities/activity_2.json: Traceback (most recent call last):\n"
            "json.decoder.JSONDecodeError: Expecting property name enclosed in double quotes\n"
            "ERROR:root:Failed to parse C:\\x\\Activities/activity_details_4.json: Traceback (most recent call last):\n"
        )
        self.assertEqual(run_batch.failed_summaries(log), {"1", "2"})

    def test_summary_import_failure_is_redownloaded_or_blocks(self):
        """SQLite에 유효한 이전 행이 있어도 요약 import가 실패하면 그 요약을 다시 받고, 계속 실패하면 업로드 전에 멈춘다."""
        for fixed_on_full_run in (True, False):
            with self.subTest(fixed_on_full_run=fixed_on_full_run), tempfile.TemporaryDirectory() as tmp, \
                    redirect_stdout(io.StringIO()):
                activities = Path(tmp)
                summary = activities / "activity_1.json"
                summary.write_text("{", encoding="utf-8")                     # 강제 종료로 잘린 요약
                (activities / "activity_details_1.json").write_text("{", encoding="utf-8")
                calls = []

                def run(args, env, log, timeout, on_line=None, cwd=None):
                    calls.append("--latest" in args)
                    if not summary.exists():                                  # Download: 파일이 없을 때만 다시 받는다
                        summary.write_text("{}" if fixed_on_full_run else "{", encoding="utf-8")
                    if summary.read_text(encoding="utf-8") == "{":           # JsonFileProcessor: 로그만 남기고 계속
                        Path(cwd, "garmindb.log").write_text(
                            f"ERROR:root:Failed to parse {activities}/activity_1.json: Traceback (most recent call last):\n"
                            f"ERROR:root:Failed to parse {activities}/activity_details_1.json: Traceback\n", encoding="utf-8")
                    return 0

                def run_cli(latest):
                    run_batch.run_garmindb(["garmindb_cli.py", *(["--latest"] if latest else [])], {}, print, 60, activities, run)

                read_local = lambda: {"1"}                                    # 이전 실행의 유효한 행
                with_summary = lambda ids: run_batch.with_summary(ids, activities)
                if fixed_on_full_run:
                    self.assertEqual(run_batch.collect({"1"}, False, run_cli, read_local, with_summary), {"1"})
                    self.assertEqual(summary.read_text(encoding="utf-8"), "{}")
                else:
                    with self.assertRaises(Failure) as caught:
                        run_batch.collect({"1"}, False, run_cli, read_local, with_summary)
                    self.assertEqual(caught.exception.kind, "incomplete")
                self.assertEqual(calls, [True, False])                          # 최근 수집 → 전체 수집으로 다시 받기
                self.assertTrue((activities / "activity_details_1.json").exists())   # 상세 실패는 선택 항목


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


class LocalIdsTests(unittest.TestCase):
    """요약 JSON 조건은 원격 ID의 수집 완료 판정에만 쓰고, 여분·건수는 sync_supa가 올리는 SQLite 전체 행으로 센다."""

    def setUp(self):
        import sqlite3
        from contextlib import closing
        run_batch.WARNINGS.clear()
        self.activities = Path(self.enterContext(tempfile.TemporaryDirectory(ignore_cleanup_errors=True)))
        self.db = self.activities / "garmin_activities.db"
        with closing(sqlite3.connect(self.db)) as lite:
            lite.execute("create table activities (activity_id, start_time, name)")
            lite.executemany("insert into activities values (?, '2026-10-29 09:00:00', 'old_name')", [("1",), ("2",)])
            lite.commit()
        (self.activities / "activity_1.json").write_text("{}", encoding="utf-8")   # 2는 요약 JSON 없음

    def collect(self, remote):
        with redirect_stdout(io.StringIO()):
            # 목록 조회가 실패해 CLI가 아무것도 받지 않았다 (종료 코드 0, 로그인은 성공)
            return run_batch.collect(remote, False, lambda latest: None, lambda: run_batch.local_ids(self.db),
                                     lambda ids: run_batch.with_summary(ids, self.activities))

    def test_row_without_summary_json_is_missing_when_list_fails(self):
        """SQLite ID는 완전하지만 요약 JSON이 없으면 정상 조회는 다시 받아 갱신한다 → 목록 실패 실행을 통과시키지 않는다."""
        self.assertEqual(run_batch.with_summary(run_batch.local_ids(self.db), self.activities), {"1"})
        with self.assertRaises(Failure) as caught:
            self.collect({"1", "2"})
        self.assertEqual(caught.exception.kind, "incomplete")

    def test_old_row_without_json_still_counts_as_uploaded_extra(self):
        """원격 0 / SQLite 2건(하나는 JSON 없음): sync_supa는 둘 다 올리므로 0건이 아니라 2건과 여분 경고."""
        self.assertEqual(self.collect(set()), {"1", "2"})
        self.assertEqual(len(run_batch.WARNINGS), 1)


class SupabaseOnlyTests(unittest.TestCase):
    def setUp(self):
        run_batch.WARNINGS.clear()

    def kept(self, supabase_ids, local):
        with mock.patch("psycopg.connect") as connect, redirect_stdout(io.StringIO()):
            connect.return_value.__enter__.return_value.execute.return_value = [(i,) for i in supabase_ids]
            return run_batch.supabase_only("host=x", local)

    def test_empty_remote_and_local_with_old_supabase_rows_warns(self):
        self.assertEqual(self.kept([1, 2, 3, 4, 5], set()), {"1", "2", "3", "4", "5"})   # 0건 성공이 아니라 5건 보존
        self.assertEqual(len(run_batch.WARNINGS), 1)

    def test_supabase_matching_local_is_silent(self):
        self.assertEqual(self.kept([1, 2], {"1", "2"}), set())
        self.assertEqual(run_batch.WARNINGS, [])


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
