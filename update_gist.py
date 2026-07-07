#!/usr/bin/env python3
"""Supabase 통계 조회 -> GitHub gist 갱신 (최근 30일, 러닝/걷기/자전거 거리 막대)."""
import json
import os
import sys
import urllib.request

import psycopg

# 표시 순서 고정: (DB sport 값, 이모지, 라벨)
SPORTS = [
    ("running", "🏃", "Running"),
    ("walking", "🚶", "Walking"),
    ("cycling", "🚴", "Cycling"),
]
BAR_LEN = 25

STATS_SQL = """
    select sport, count(*), coalesce(round(sum(distance_km), 1), 0)
    from garmin_activity
    where sport in ('running', 'walking', 'cycling')
      and start_time >= now() - interval '30 days'
    group by sport
"""


def fetch_stats(dsn: str) -> dict:
    rows = {}
    with psycopg.connect(dsn) as pg, pg.cursor() as cur:
        for sport, cnt, km in cur.execute(STATS_SQL).fetchall():
            rows[sport] = (int(cnt), float(km))
    return {key: rows.get(key, (0, 0.0)) for key, _, _ in SPORTS}


def build_gist_content(stats: dict) -> str:
    total_km = sum(km for _, km in stats.values())
    total_cnt = sum(cnt for cnt, _ in stats.values())
    lines = []
    for key, emoji, label in SPORTS:
        cnt, km = stats[key]
        pct = (km / total_km * 100) if total_km else 0.0
        filled = round(pct / 100 * BAR_LEN)
        bar = "█" * filled + "░" * (BAR_LEN - filled)
        lines.append(f"{emoji} {label:<9} {km:>5.1f} km  {bar} {pct:>5.1f}%")
    lines.append("")
    lines.append(f"📅 최근 30일 · 총 {total_km:.1f} km · {total_cnt}회")
    return "\n".join(lines)


def update_gist(content: str) -> None:
    filename = os.environ.get("GIST_FILENAME", "garmin-stats")
    body = json.dumps({"files": {filename: {"content": content}}}).encode()
    req = urllib.request.Request(
        f"https://api.github.com/gists/{os.environ['GIST_ID']}",
        data=body, method="PATCH",
        headers={
            "Authorization": f"Bearer {os.environ['GITHUB_TOKEN']}",
            "Accept": "application/vnd.github+json",
        })
    with urllib.request.urlopen(req) as resp:
        assert resp.status == 200, f"gist update failed: {resp.status}"


def main() -> None:
    content = build_gist_content(fetch_stats(os.environ["SUPA_DSN"]))
    if "--dry-run" in sys.argv:
        sys.stdout.reconfigure(encoding="utf-8")  # Windows 콘솔(cp949) 이모지 출력 대응
        print(content)
        return
    update_gist(content)
    print("gist updated")


if __name__ == "__main__":
    main()
