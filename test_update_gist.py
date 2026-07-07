from datetime import timedelta

from update_gist import build_gist_content


def test_build_gist_content():
    stats = {
        "ytd": [
            ("running", 12, 98.4, timedelta(hours=12, minutes=34)),
            ("walking", 3, 11.2, timedelta(hours=2, minutes=5)),
        ],
        "alltime": 453.5,
    }
    out = build_gist_content(stats)
    lines = out.splitlines()
    assert lines[0].startswith("🏃 Running")
    assert "98.4" in lines[0] and "12h 34m" in lines[0]
    assert "🚶 Walking" in lines[1] and "2h 05m" in lines[1]
    assert lines[-1].startswith("📈 All-time") and "453.5" in lines[-1]


def test_build_gist_content_null_time_and_unknown_sport():
    stats = {"ytd": [("hiking", 1, 5.0, None)], "alltime": 5.0}
    out = build_gist_content(stats)
    assert "🏅 Hiking" in out and "0h 00m" in out
