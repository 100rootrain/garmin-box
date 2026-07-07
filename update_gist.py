#!/usr/bin/env python3
"""Supabase 통계 조회 -> GitHub gist 갱신 (YTD 종목별 + all-time)."""
import json
import os
import sys
import urllib.request

import psycopg

SPORT_EMOJI = {"running": "🏃", "walking": "🚶", "cycling": "🚴"}

YTD_SQL = """
    select sport, count(*), round(sum(distance_km), 1),
           sum(moving_time::interval)
    from garmin_activity
    where sport is not null
      and date_part('year', start_time) = date_part('year', now())
    group by sport
    order by 3 desc
"""

ALLTIME_SQL = "select coalesce(round(sum(distance_km), 1), 0) from garmin_activity"


def fetch_stats(dsn: str) -> dict:
    with psycopg.connect(dsn) as pg, pg.cursor() as cur:
        ytd = cur.execute(YTD_SQL).fetchall()
        (alltime,) = cur.execute(ALLTIME_SQL).fetchone()
    return {"ytd": ytd, "alltime": alltime}


def _fmt_hm(delta) -> str:
    total_min = int(delta.total_seconds() // 60) if delta else 0
    return f"{total_min // 60}h {total_min % 60:02d}m"


def build_gist_content(stats: dict) -> str:
    lines = []
    for sport, cnt, km, t in stats["ytd"]:
        emoji = SPORT_EMOJI.get(sport, "🏅")
        lines.append(
            f"{emoji} {sport.capitalize():<9} YTD {cnt:>3} acts {km:>7} km {_fmt_hm(t):>8}")
    lines.append(f"📈 All-time {'':<13} {stats['alltime']:>7} km")
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
        print(content)
        return
    update_gist(content)
    print("gist updated")


if __name__ == "__main__":
    main()
