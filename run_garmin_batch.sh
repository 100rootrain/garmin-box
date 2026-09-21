#!/usr/bin/env bash
# garmindb 다운로드 -> SUPA 동기화 -> gist 갱신. 앞 단계 실패 시 즉시 중단.
set -euo pipefail
cd "$(dirname "$0")"

set -a
source "$HOME/.garmin-box.env"
set +a

# garmindb_cli 는 로그인 실패 시에도 exit 0 → 출력으로 판정. refresh token 만료(약 1달) 시
# 캐시 토큰 로그인만 반복 실패하므로 토큰 파일을 지우고 한 번만 재시도한다.
run_garmindb() {
    local out rc=0
    out=$(.venv/bin/python .venv/bin/garmindb_cli.py --activities --latest --download --import --analyze 2>&1) || rc=$?
    printf '%s\n' "$out"
    [ "$rc" -eq 0 ] && ! grep -q 'Failed to login' <<<"$out"
}
if ! run_garmindb; then
    rm -f "$HOME/.GarminDb/garmin_tokens.json"
    echo "garmindb login failed; removed stale token, retrying once"
    run_garmindb
fi
.venv/bin/python sync_supa.py
.venv/bin/python update_gist.py
