"""Exercise the relocated Windows runtime without Garmin credentials or a remote DB."""
import argparse
import importlib
import json
import os
from pathlib import Path
import platform
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from importlib.metadata import version
from contextlib import closing

parser = argparse.ArgumentParser()
parser.add_argument("--repo", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--ca-bundle", type=Path)
args = parser.parse_args()
if args.ca_bundle:
    import certifi
    assert args.ca_bundle.read_bytes() == Path(certifi.where()).read_bytes()
    os.environ["REQUESTS_CA_BUNDLE"] = str(args.ca_bundle)
report = {"python": platform.python_version(), "architecture": platform.machine(), "os": platform.platform(), "checks": []}
report["ca_override"] = bool(args.ca_bundle)
for name in ("garmindb", "garminconnect", "psycopg", "psycopg_binary", "sqlalchemy", "greenlet", "curl_cffi", "_cffi_backend", "fitfile", "tcxfile", "idbutils", "tornado"):
    importlib.import_module(name)
report["checks"].append("all_application_and_native_imports")
report["versions"] = {name: version(name) for name in ("garmindb", "garminconnect", "psycopg", "psycopg-binary", "sqlalchemy", "curl-cffi")}
import psycopg
from psycopg.conninfo import conninfo_to_dict
assert psycopg.pq.version() > 0
assert conninfo_to_dict("host=localhost dbname=postgres password='a=b'")["password"] == "a=b"
report["libpq"] = psycopg.pq.version()
report["checks"].append("libpq_load_and_conninfo")
from curl_cffi import requests
response = requests.get("https://pypi.org/pypi/garmindb/3.9.0/json", timeout=30)
assert response.status_code == 200 and response.json()["info"]["version"] == "3.9.0"
report["checks"].append("curl_cffi_https_certificate_and_json")
import garmindb
from garmindb.garmin_connect_config_manager import GarminConnectConfigManager
from garmindb.garmindb import ActivitiesDb
with tempfile.TemporaryDirectory(prefix="garmin-bundle-db-") as temporary:
    config_root = Path(temporary) / "config"
    config_root.mkdir()
    example = Path(garmindb.__file__).parent / "GarminConnectConfig.json.example"
    config = json.loads(example.read_text(encoding="utf-8"))
    config["directories"].update(relative_to_home=False, base_dir=str(Path(temporary) / "health data"))
    (config_root / "GarminConnectConfig.json").write_text(json.dumps(config), encoding="utf-8")
    manager = GarminConnectConfigManager(str(config_root))
    database = ActivitiesDb(manager.get_db_params())
    database.engine.dispose()
    db_path = Path(manager.get_db_dir()) / "garmin_activities.db"
    assert db_path.is_file()
    with closing(sqlite3.connect(db_path)) as db:
        db.execute("insert into activities (activity_id, start_time, sport, name, distance, elapsed_time, moving_time, hrz_1_time, hrz_2_time, hrz_3_time, hrz_4_time, hrz_5_time) values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", ("1", "2026-10-29 09:00:00", "running", "synthetic bundle check", 1.25, "00:10:00", "00:09:00", *(["00:00:00"] * 5)))
        db.commit()
        assert db.execute("select distance from activities where activity_id=1").fetchone()[0] == 1.25
report["checks"].append("garmindb_config_sqlalchemy_sqlite_roundtrip")
cli = Path(sys.executable).parent / "Scripts" / "garmindb_cli.py"
result = subprocess.run([sys.executable, "-I", "-X", "utf8", str(cli), "--help"], capture_output=True, text=True, encoding="utf-8", timeout=60)
assert result.returncode == 0 and "--activities" in result.stdout
report["checks"].append("garmindb_cli_help")
sys.path.insert(0, str(args.repo))
suite = unittest.TestSuite()
for file in (args.repo / "test_update_gist.py", args.repo / "tests" / "test_docs_and_launcher.py"):
    spec = importlib.util.spec_from_file_location(file.stem, file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    suite.addTests(unittest.defaultTestLoader.loadTestsFromModule(module))
    for name, function in vars(module).items():
        if name.startswith("test_") and callable(function):
            suite.addTest(unittest.FunctionTestCase(function))
result = unittest.TextTestRunner(verbosity=2).run(suite)
assert result.wasSuccessful() and result.testsRun == 4
report["existing_tests_passed"] = result.testsRun
report["checks"].append("existing_repository_tests")
result = subprocess.run([sys.executable, "-I", "-m", "pip", "--isolated", "check"], capture_output=True, text=True, timeout=60)
assert result.returncode == 0
report["checks"].append("pip_check")
report["status"] = "passed"
args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report))
