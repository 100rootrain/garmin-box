# garmin-box

Garmin Connect 활동 통계를 GitHub 프로필 gist에 표시한다 (strava-box 대체 —
Strava 2026-06 정책 변경으로 무료 API 차단됨).

```
cron (하루 1회)
 └─ run_garmin_batch.sh
     ├─ garmindb_cli : Garmin Connect -> SQLite
     ├─ sync_supa.py : SQLite -> Supabase garmin_activity upsert
     └─ update_gist.py : YTD/all-time 통계 -> gist 갱신
```

## 설치 (Linux 서버)

    git clone https://github.com/100rootrain/garmin-box.git && cd garmin-box
    python3 -m venv .venv
    .venv/bin/pip install -r requirements.txt

1. GarminDB 설정: `~/.GarminDb/GarminConnectConfig.json`
   (예제: `.venv/lib/python*/site-packages/garmindb/GarminConnectConfig.json.example`)
2. 시크릿: `cp .env.example ~/.garmin-box.env && chmod 600 ~/.garmin-box.env` 후 실값 입력
3. Supabase에 `schema.sql` 실행 (최초 1회)
4. 수동 실행: `./run_garmin_batch.sh`
5. cron 등록: `10 3 * * * /home/USER/garmin-box/run_garmin_batch.sh >> /home/USER/garmin-box/garmin-batch.log 2>&1`

## 개발

    .venv/bin/python -m pytest -v
    .venv/bin/python update_gist.py --dry-run

시크릿은 어떤 파일로도 커밋하지 않는다. `.env`, 로그, 토큰 파일은 .gitignore 참조.
