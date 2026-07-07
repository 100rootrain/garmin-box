#!/usr/bin/env bash
# garmindb 다운로드 -> SUPA 동기화 -> gist 갱신. 앞 단계 실패 시 즉시 중단.
set -euo pipefail
cd "$(dirname "$0")"

set -a
source "$HOME/.garmin-box.env"
set +a

.venv/bin/python .venv/bin/garmindb_cli.py --activities --latest --download --import --analyze
.venv/bin/python sync_supa.py
.venv/bin/python update_gist.py
