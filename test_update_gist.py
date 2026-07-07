from update_gist import build_gist_content


def _stats(sports, cur_year=2026, prev_year=2025, cur_km=310.2, prev_km=520.9):
    return {"sports": sports, "cur_year": cur_year, "prev_year": prev_year,
            "cur_km": cur_km, "prev_km": prev_km}


def test_sport_bars():
    out = build_gist_content(_stats(
        {"running": (6, 41.8), "walking": (1, 3.6), "cycling": (0, 0.0)}))
    lines = out.splitlines()
    assert lines[0].startswith("🏃 Running") and "41.8 km" in lines[0]
    assert "92.1%" in lines[0] and "█" in lines[0]        # 41.8/45.4
    assert lines[1].startswith("🚶 Walking") and "7.9%" in lines[1]
    assert lines[2].startswith("🚴 Cycling") and "0.0%" in lines[2]
    assert "█" not in lines[2] and "░" in lines[2]        # 0km -> 빈 막대


def test_year_comparison():
    out = build_gist_content(_stats(
        {"running": (6, 41.8), "walking": (1, 3.6), "cycling": (0, 0.0)}))
    lines = out.splitlines()
    assert lines[-2].startswith("🎯 2026 vs 2025") and "59.6%" in lines[-2]  # 310.2/520.9
    assert lines[-1].strip() == "310.2 / 520.9 km"


def test_zero_prev_year_no_div():
    out = build_gist_content(_stats(
        {"running": (0, 0.0), "walking": (0, 0.0), "cycling": (0, 0.0)},
        cur_km=0.0, prev_km=0.0))
    assert "0.0%" in out                                  # 0으로 나눗셈 안 터짐
