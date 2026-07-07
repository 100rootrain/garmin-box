#!/usr/bin/env python3
"""GarminDB sqlite -> Supabase garmin_activity 전체 upsert."""
import os
import sqlite3
import sys
from pathlib import Path

import psycopg

SQLITE_PATH = Path(os.environ.get(
    "GARMIN_SQLITE", str(Path.home() / "HealthData/DBs/garmin_activities.db")))

SELECT_SQL = """
    select activity_id, start_time, sport, name,
           distance, elapsed_time, moving_time, avg_speed
    from activities
    where start_time is not null
"""

UPSERT_SQL = """
    insert into garmin_activity
        (activity_id, start_time, sport, name, distance_km,
         elapsed_time, moving_time, avg_speed, updated_at)
    values (%s, %s, %s, %s, %s, %s, %s, %s, now())
    on conflict (activity_id) do update set
        start_time   = excluded.start_time,
        sport        = excluded.sport,
        name         = excluded.name,
        distance_km  = excluded.distance_km,
        elapsed_time = excluded.elapsed_time,
        moving_time  = excluded.moving_time,
        avg_speed    = excluded.avg_speed,
        updated_at   = now()
"""


def main() -> None:
    if not SQLITE_PATH.exists():
        sys.exit(f"sqlite not found: {SQLITE_PATH}")
    with sqlite3.connect(SQLITE_PATH) as lite:
        rows = lite.execute(SELECT_SQL).fetchall()
    if not rows:
        sys.exit("no activity rows in sqlite")

    with psycopg.connect(os.environ["SUPA_DSN"]) as pg, pg.cursor() as cur:
        # ponytail: 전체 upsert 매회 실행, 건수가 수천 건 넘어가면 증분으로 전환
        cur.executemany(UPSERT_SQL, rows)
        cur.execute("select count(*) from garmin_activity")
        (pg_count,) = cur.fetchone()

    assert pg_count >= len(rows), f"supa {pg_count} < sqlite {len(rows)}"
    print(f"synced {len(rows)} sqlite rows, supa total {pg_count}")


if __name__ == "__main__":
    main()
