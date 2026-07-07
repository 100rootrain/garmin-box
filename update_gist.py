#!/usr/bin/env python3
"""Supabase 통계 조회 -> GitHub gist 갱신.

- 상단: 최근 30일 러닝/걷기/자전거 거리 막대
- 하단: 올해 러닝 누적 vs 작년 총합 도달률
"""
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

YEAR_SQL = """
    select
        cast(date_part('year', now()) as int),
        cast(date_part('year', now()) as int) - 1,
        coalesce(sum(distance_km) filter (
            where date_part('year', start_time) = date_part('year', now())), 0),
        coalesce(sum(distance_km) filter (
            where date_part('year', start_time) = date_part('year', now()) - 1), 0)
    from garmin_activity
    where sport = 'running'
"""


def fetch_stats(dsn: str) -> dict:
    with psycopg.connect(dsn) as pg, pg.cursor() as cur:
        rows = {}
        for sport, cnt, km in cur.execute(STATS_SQL).fetchall():
            rows[sport] = (int(cnt), float(km))
        sports = {key: rows.get(key, (0, 0.0)) for key, _, _ in SPORTS}
        cy, py, ck, pk = cur.execute(YEAR_SQL).fetchone()
    return {
        "sports": sports,
        "cur_year": int(cy), "prev_year": int(py),
        "cur_km": float(ck), "prev_km": float(pk),
    }


def _bar(pct: float) -> str:
    filled = max(0, min(BAR_LEN, round(pct / 100 * BAR_LEN)))
    return "█" * filled + "░" * (BAR_LEN - filled)


def build_gist_content(stats: dict) -> str:
    sports = stats["sports"]
    total_km = sum(km for _, km in sports.values())
    lines = []
    for key, emoji, label in SPORTS:
        _, km = sports[key]
        pct = (km / total_km * 100) if total_km else 0.0
        lines.append(f"{emoji} {label:<9} {km:>5.1f} km  {_bar(pct)} {pct:>5.1f}%")

    cur_km, prev_km = stats["cur_km"], stats["prev_km"]
    ypct = (cur_km / prev_km * 100) if prev_km else 0.0
    label = f"{stats['cur_year']} vs {stats['prev_year']}"
    # 라벨 폭 20 = 종목줄의 "{label:<9} {km:>5.1f} km  " 폭과 동일 → 막대 시작 정렬
    lines.append(f"🎯 {label:<20}{_bar(ypct)} {ypct:>5.1f}%")
    lines.append(f"   {cur_km:.1f} / {prev_km:.1f} km")
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
