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
import urllib.request
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
    state_dir.mkdir(parents=True, exist_ok=True)  # Log를 만들기 전에 실패한 경우에도 상태를 쓴다
    path = state_dir / "status.json"
    temp = path.with_name("status.json.tmp")
    temp.write_text(json.dumps(status, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temp, path)


def run_child(args, env, log, timeout, on_line=None, cwd=None):
    """번들 Python으로 절대 경로 스크립트를 `-I -X utf8` 하위 프로세스로 실행한다 (`-I`라 import로는 못 부른다).

    stdin은 닫아 예기치 않은 기본 MFA 프롬프트에서도 멈추지 않고, cwd는 임시 폴더라 원문 garmindb.log가 남지 않는다
    (run_garmindb는 로그를 읽으려고 자기 임시 폴더를 넘긴다).
    """
    with tempfile.TemporaryDirectory(prefix="garmin-box-", ignore_cleanup_errors=True) as temp, \
            subprocess.Popen([sys.executable, "-I", "-X", "utf8", *map(str, args)], cwd=cwd or temp, env=env,
                             stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                             text=True, encoding="utf-8", errors="replace") as proc:
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
    chain, seen = [], error
    while seen is not None:
        chain.append(seen)
        seen = seen.__cause__ or seen.__context__
    # MFA 콜백이 던진 Failure가 우선이다: garminconnect 0.3.11은 콜백 예외를 GarminConnectConnectionError로 감싼다
    if failure := next((e for e in chain if isinstance(e, Failure)), None):
        return failure
    for seen in chain:
        for cls, kind, message in kinds:
            if isinstance(seen, cls):
                return Failure(kind, message)
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


def check_schema(dsn, create):
    """create면 create table if not exists만 실행한 뒤 필수 열·소수 자릿수를 검사한다. 기존 테이블은 바꾸거나 지우지 않는다."""
    import psycopg
    with psycopg.connect(dsn, connect_timeout=30) as pg:
        if create:
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


def with_summary(ids, activities_dir):
    """요약 JSON(activity_<id>.json)도 있는 ID. 원격 ID의 수집 완료 판정에만 쓴다(업로드·건수는 SQLite 전체 행).

    GarminDB는 SQLite가 아니라 이 파일이 있는지로 다시 받을지 정한다. 둘 다 있으면 목록 조회가 실패한 실행도
    정상 실행과 같은 결과다. 파일이 없으면(원본 정리·DB만 이전·import 실패로 지움) 정상 조회가 다시 받으므로 누락이다.
    """
    return {i for i in ids if (activities_dir / f"activity_{i}.json").is_file()}


def ensure_download_all(config_file, count):
    """전체 수집이 원격 전체를 받도록 download_all_activities를 count 이상으로 둔다 (다른 설정은 보존)."""
    data = json.loads(config_file.read_text(encoding="utf-8"))
    if (data.get("data", {}).get("download_all_activities") or 0) >= count:
        return
    data.setdefault("data", {})["download_all_activities"] = count
    temp = config_file.with_name(config_file.name + ".tmp")
    temp.write_text(json.dumps(data, indent=4, ensure_ascii=False), encoding="utf-8")
    os.replace(temp, config_file)


def collect(remote, full_first, run_cli, read_local, collected=lambda ids: ids):
    """CLI 다운로드·import 뒤 원격 ID가 모두 수집돼야(collected: SQLite 행 + 요약 JSON) 완료다 (건수가 같아도 ID가 빠지면 실패).

    목록·요약 조회 실패는 GarminDB가 로그만 남기므로 ID 누락으로 판별한다. 원본 FIT·상세 JSON만 없는 활동은
    요약이 있어 통과한다(정상 실행에서도 GarminDB가 다시 받지 않는 선택 항목). 돌려주는 값과 여분 경고는
    sync_supa.py가 실제로 올리는 SQLite 전체 행(read_local) 기준이다.
    """
    missing = None
    if not full_first:
        run_cli(latest=True)
        missing = remote - collected(read_local())
        if missing:
            warn(f"최근 기록 밖에서 빠진 {len(missing)}건을 전체 수집으로 보충합니다.")
    if full_first or missing:
        run_cli(latest=False)
        if still := remote - collected(read_local()):
            raise Failure("incomplete", f"Garmin 기록 {len(still)}건을 가져오지 못해 Supabase·gist를 갱신하지 않았습니다. 다시 동기화하세요.")
    local = read_local()
    if extra := local - remote:
        warn(f"Garmin에 없는 기록 {len(extra)}건이 로컬·Supabase·gist 통계에 남아 있습니다. 삭제는 반영하지 않습니다.")
    return local


LOG_RECORD = re.compile(r"^(?=(?:CRITICAL|ERROR|WARNING|INFO|DEBUG):)", re.M)   # basicConfig 기본 형식 LEVEL:name:message
SUMMARY_FAILED = (
    re.compile(r"Failed to parse .*?[\\/]activity_(\d+)\.json:"),   # 파싱 오류·커밋 실패 (_process_files)
    # 보조 함수(_process_running 등) 안의 autoflush가 activities 쓰기에 실패한 경우. 같은 형식의 DB 무관 오류
    # ('NoneType' … meters_or_feet: 러닝 다이내믹스 없는 달리기)는 정상 import에서도 나오므로 sqlite 오류만 본다.
    re.compile(r"Exception in _process_\w+ from (\d+) GarminJsonSummaryData:[\s\S]*\(sqlite3\."),
)


def failed_summaries(text):
    """garmindb.log에서 요약(activity_<id>.json) import가 실패한 활동 ID. 상세 JSON·FIT 실패는 선택 항목이라 뺀다."""
    return {m.group(1) for record in LOG_RECORD.split(text) for pattern in SUMMARY_FAILED if (m := pattern.search(record))}


def run_garmindb(args, env, log, timeout, activities_dir, run=run_child, on_line=None):
    """garmindb_cli를 한 번 실행한다. 0이 아닌 종료와 0으로 끝난 로그인 실패는 실패다.

    GarminDB 3.9.0은 Download.login()이 GarminConnectAuthError를 삼키면 "Failed to login!"을 남기고 sys.exit()(종료 코드 0)
    한다. 다른 로그인 예외는 traceback과 함께 0이 아닌 종료다. 이전 런처도 같은 문구로 판정했다.

    요약 JSON import 실패는 cwd의 garmindb.log에만 남는다(failed_summaries). 그 요약 파일을 지워 collect가 누락으로 보고
    다시 받게 한다(잘린 파일은 있다고 건너뛰어 재시도로도 복구되지 않는다). 다시 받아도 실패하면 incomplete로 업로드 전에
    멈춘다. 원문 로그는 메모리에서만 읽고 남기지 않는다.
    """
    with tempfile.TemporaryDirectory(prefix="garmin-box-", ignore_cleanup_errors=True) as cwd:
        code = run(args, env, log, timeout, on_line, cwd=cwd)
        garmindb_log = Path(cwd) / "garmindb.log"
        text = garmindb_log.read_text(encoding="utf-8", errors="replace") if garmindb_log.is_file() else ""
    if failed := failed_summaries(text):
        for activity_id in failed:
            (activities_dir / f"activity_{activity_id}.json").unlink(missing_ok=True)
        emit("progress", stage="import", message=f"가져오지 못한 활동 요약 {len(failed)}건은 다시 받습니다.")
    if code != 0:
        raise Failure("garmin", f"GarminDB 수집이 실패했습니다(종료 코드 {code}). 로그를 확인하세요.")
    if "Failed to login" in text:
        raise Failure("garmin", "GarminDB가 Garmin에 로그인하지 못해 수집하지 않았습니다. 잠시 뒤 다시 동기화하세요.")


def supabase_only(dsn, local):
    """업로드 전에 Supabase에만 있는 기록(이전 PC·다른 계정 업로드, Garmin에서 삭제)을 찾아 경고한다. 지우지 않는다."""
    import psycopg
    with psycopg.connect(dsn, connect_timeout=30) as pg:
        kept = {str(row[0]) for row in pg.execute("select activity_id from garmin_activity")} - local
    if kept:
        warn(f"Garmin에 없는 기록 {len(kept)}건이 Supabase·gist 통계에 남아 있습니다. 삭제는 반영하지 않습니다.")
    return kept


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
    """setup: 테이블 생성 + 전체 수집, sync: 최근 수집(누락 시 전체 보충). 둘 다 테이블 검사와 업로드 전 SQLite 대조."""
    for key, label in (("SUPA_DSN", "Supabase 연결 문자열"), ("GITHUB_TOKEN", "GitHub 토큰"), ("GIST_ID", "Gist ID")):
        need(values, key, label)
    # setup이 거부한 테이블을 지금 동기화·예약 실행이 반올림해 올리지 않게 모든 업로드 경로에서 검사한다
    emit("progress", stage="schema", message="Supabase 테이블 확인")
    check_schema(values["SUPA_DSN"], create=mode == "setup")
    config = saved_garmin_config()
    emit("progress", stage="login", message="Garmin 로그인")
    adapter = garmin_login(config, prompt_allowed, factory)
    db_path = activities_db(config)
    total, remote = remote_activities(adapter)
    emit("progress", stage="list", message=f"Garmin 활동 {total}건 확인")
    # Windows 전체 동기화의 DB 경로는 Garmin 설정에서 계산한 값이 우선한다 (.env에 중복 저장하지 않는다)
    env = child_env(values, GARMIN_SQLITE=str(db_path))
    cli = Path(sys.executable).parent / "Scripts" / "garmindb_cli.py"
    activities_dir = Path(config.get_activities_dir())

    def run_cli(latest):
        stage = "latest" if latest else "all"
        if not latest:
            ensure_download_all(GARMIN_CONFIG, max(LIST_LIMIT_FLOOR, total))
        run_garmindb([cli, "--activities", "--download", "--import", "--analyze", *(["--latest"] if latest else [])],
                     env, log, CLI_TIMEOUT[stage], activities_dir,
                     on_line=lambda line: emit("progress", stage=stage, message=line))

    local = collect(remote, mode == "setup", run_cli, lambda: local_ids(db_path), lambda ids: with_summary(ids, activities_dir))
    try:
        changed = adapter.client.count_activities() != total
    except Exception:
        changed = True
    if changed:
        warn("수집하는 동안 Garmin 기록 수가 바뀌었거나 확인하지 못했습니다. 잠시 뒤 '지금 동기화'를 다시 실행하세요.")
    kept = supabase_only(values["SUPA_DSN"], local)
    if not local:
        env["GARMIN_BOX_ALLOW_EMPTY"] = "1"  # collect가 원격 0건과 로컬 0행을 확인한 경우에만 여기 온다
    upload(env, log)
    # gist는 Supabase 전체로 통계를 만든다: 남은 기록도 세어 0건 성공으로 보이지 않게 한다
    return {"activities": len(local) + len(kept), "gist_url": f"https://gist.github.com/{values['GIST_ID']}"}


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
    log = lambda line: None  # .env 읽기·로그 폴더 준비가 실패해도 아래에서 failed 이벤트와 상태를 남긴다
    try:
        values = {**read_env_file(ENV_FILE), **{k: os.environ[k] for k in ENV_KEYS if os.environ.get(k)}}
        REDACT = make_redactor(secrets_of(values))
        log = Log(state_dir / "logs", REDACT)
        log(f"start {args.mode}")
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
