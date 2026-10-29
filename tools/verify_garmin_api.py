"""Read-only Garmin request-limit probe; emits only counts and aggregate hashes."""
import argparse
import hashlib
import json
import logging
import os
import getpass
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

logging.disable(logging.CRITICAL)
sys.stdout.reconfigure(encoding="utf-8")


def emit(event, **data):
    payload = json.dumps({"event": event, **data}, ensure_ascii=False)
    print(payload, flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--ca-bundle", type=Path)
    args = parser.parse_args()
    from importlib.metadata import version
    import certifi
    ca_file = args.ca_bundle or Path(__file__).with_name("certifi-ca.pem")
    if ca_file.is_file():
        if ca_file.read_bytes() != Path(certifi.where()).read_bytes():
            raise RuntimeError("CA_BUNDLE_MISMATCH")
        os.environ["REQUESTS_CA_BUNDLE"] = str(ca_file)
    import garminconnect
    from garminconnect import Garmin

    report = {
        "tested_at_utc": datetime.now(timezone.utc).isoformat(),
        "garminconnect": version("garminconnect"),
        "status": "in_progress",
        "tls_ca_override": ca_file.is_file(),
        "requests": [],
    }
    stage = "login"

    def mfa():
        if sys.stdin.isatty():
            return getpass.getpass("Garmin MFA code: ").strip()
        emit("mfa_required")
        code = sys.stdin.readline().strip()
        if not code:
            raise RuntimeError("MFA_INPUT_REQUIRED")
        return code

    try:
        email = os.environ.pop("GARMIN_EMAIL", "")
        password = os.environ.pop("GARMIN_PASSWORD", "")
        if not email and sys.stdin.isatty():
            email = input("Garmin email: ").strip()
            password = getpass.getpass("Garmin password: ")
        os.environ.pop("GARMINTOKENS", None)
        if not email or not password:
            raise RuntimeError("CREDENTIALS_REQUIRED")
        emit("login_started")
        client = Garmin(email, password, prompt_mfa=mfa, retry_attempts=0)
        password = None
        client.login()
        emit("progress", message="로그인 성공. 활동 개수와 요청별 응답 개수를 확인합니다.")
        stage = "count"
        total = int(client.count_activities())
        report["total_activities"] = total
        report["sdk_max_activity_limit"] = garminconnect.MAX_ACTIVITY_LIMIT
        endpoint = client.garmin_connect_activities
        report["endpoint"] = endpoint

        def request(start, limit):
            nonlocal stage
            stage = "activities"
            time.sleep(2)
            emit("progress", message=f"읽기 전용 조회: start={start}, limit={limit}")
            rows = client.connectapi(endpoint, params={"start": str(start), "limit": str(limit)})
            if not isinstance(rows, list):
                raise TypeError("EXPECTED_ACTIVITY_LIST")
            ids = [str(row["activityId"]) for row in rows]
            if len(set(ids)) != len(ids):
                raise RuntimeError("DUPLICATE_ACTIVITY_IDS")
            report["requests"].append({
                "start": start,
                "limit": limit,
                "returned": len(rows),
                "unique_ids": len(set(ids)),
                "ids_sha256": hashlib.sha256("\n".join(ids).encode()).hexdigest(),
            })
            emit("progress", message=f"응답: {len(rows)}건 / 전체 {total}건")
            return ids

        largest = []
        large_limit = max(2000, min(total + 1, 10000))
        for limit in sorted({25, 1000, large_limit}):
            ids = request(0, limit)
            if len(ids) > len(largest):
                largest = ids
        report["single_request_covers_total"] = len(largest) == total
        report["observed_truncation"] = any(
            result["returned"] < min(total, result["limit"]) for result in report["requests"]
        )
        covered = set(largest)
        if len(covered) < total:
            page_size = min(len(largest) or 100, 1000)
            for _ in range(20):
                page = request(len(covered), page_size)
                previous = len(covered)
                covered.update(page)
                if len(covered) >= total or not page:
                    break
                if len(covered) == previous:
                    raise RuntimeError("OFFSET_DID_NOT_ADVANCE")
        elif total:
            tail = request(total - 1, 2)
            report["last_offset_matches"] = tail == largest[-1:]
        report["covered_unique_ids"] = len(covered)
        report["count_after"] = int(client.count_activities())
        report["consistent"] = total == report["count_after"] == len(covered)
        report["universal_server_limit_proven"] = False
        report["status"] = "passed" if report["consistent"] else "inconclusive"
        emit("complete", status=report["status"], total=total, covered=len(covered))
        return 0 if report["status"] == "passed" else 2
    except Exception as error:
        report.update(status="failed", failed_stage=stage, error_type=type(error).__name__)
        emit("failed", stage=stage, error_type=type(error).__name__)
        return 1
    finally:
        Path(args.output).write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
