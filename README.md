# garmin-box

[![README: English](https://img.shields.io/badge/README-English-blue)](README.en.md)

Garmin Connect 활동 통계를 GitHub 프로필 gist에 표시한다 (strava-box 대체 —
Strava 2026-06 정책 변경으로 무료 API 차단됨).

```
스케줄러 (하루 1회)
 └─ run_garmin_batch (.ps1 Windows / .sh Linux)
     ├─ garmindb_cli : Garmin Connect -> SQLite
     ├─ sync_supa.py : SQLite -> Supabase garmin_activity upsert
     └─ update_gist.py : YTD/all-time 통계 -> gist 갱신
```

> **주의:** Garmin 로그인은 클라우드/데이터센터 IP(OCI 등)에서 429로 막힌다.
> `--download` 단계는 반드시 일반 가정/사무실 IP PC에서 실행할 것. 그래서 기본
> 운영은 Windows PC + 작업 스케줄러(`run_garmin_batch.ps1`)를 권장한다.

## 설치 (Windows PC — 권장)

    git clone https://github.com/100rootrain/garmin-box.git
    cd garmin-box
    py -m venv .venv
    .venv\Scripts\pip install -r requirements.txt

1. GarminDB 설정: `%USERPROFILE%\.GarminDb\GarminConnectConfig.json`
   (예제: `.venv\Lib\site-packages\garmindb\GarminConnectConfig.json.example`)
2. 시크릿: `%USERPROFILE%\.garmin-box.env` 를 만들고 아래 형식으로 실값 입력
   (`.env.example` 참고, `SUPA_DSN` 은 URL 형식이 아니라 key=value conninfo 형식):

       SUPA_DSN=host=... port=5432 dbname=postgres user=... password=...
       GITHUB_TOKEN=gist 스코프 토큰
       GIST_ID=gist id

3. Supabase에 `schema.sql` 실행 (최초 1회)
4. 수동 실행: `powershell -ExecutionPolicy Bypass -File .\run_garmin_batch.ps1`
5. 작업 스케줄러 등록 (매일 09:10 예시). **창이 안 뜨게** `run_garmin_batch_hidden.vbs`
   런처로 호출한다(직접 .ps1을 걸면 실행 때마다 PowerShell 창이 뜬다):

       schtasks /Create /TN garmin-box /SC DAILY /ST 09:10 /TR "wscript.exe \"C:\경로\garmin-box\run_garmin_batch_hidden.vbs\""

   출력은 `batch.log`에 남으므로 창을 숨겨도 실패 원인을 추적할 수 있다.

## 설치 (Linux 서버 / OCI — 클라우드 IP는 Garmin 429 주의)

> **이 서버는 OCI(Oracle Cloud Infrastructure)다.** OCI 등 클라우드/데이터센터 IP는
> Garmin Connect 로그인·`--download`에서 **429로 차단**된다. 따라서 OCI에서는
> `run_garmin_batch.sh` 전체(다운로드 포함)를 돌리면 다운로드 단계에서 실패한다.
> OCI는 **Garmin 접속이 필요 없는 뒷단(sync_supa → update_gist)만** 돌리거나,
> gist 갱신 전용 스케줄러로만 쓸 것. Garmin 다운로드는 가정/사무실 IP의 Windows PC 담당.

    git clone https://github.com/100rootrain/garmin-box.git && cd garmin-box
    python3 -m venv .venv
    .venv/bin/pip install -r requirements.txt

1. GarminDB 설정: `~/.GarminDb/GarminConnectConfig.json`
   (예제: `.venv/lib/python*/site-packages/garmindb/GarminConnectConfig.json.example`)
2. 시크릿: `cp .env.example ~/.garmin-box.env && chmod 600 ~/.garmin-box.env` 후 실값 입력
3. Supabase에 `schema.sql` 실행 (최초 1회)
4. 수동 실행 (뒷단만): `.venv/bin/python sync_supa.py && .venv/bin/python update_gist.py`
   (Garmin 다운로드까지 포함한 `./run_garmin_batch.sh` 는 OCI IP에서 429로 실패)
5. cron 등록 (gist 갱신 전용, 매일 03:10):
   `10 3 * * * cd /home/USER/garmin-box && .venv/bin/python update_gist.py >> update_gist.log 2>&1`

## 개발

    .venv/bin/python -m pytest -v
    .venv/bin/python update_gist.py --dry-run

시크릿은 어떤 파일로도 커밋하지 않는다. `.env`, 로그, 토큰 파일은 .gitignore 참조.
