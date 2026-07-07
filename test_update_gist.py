from update_gist import build_gist_content


def test_build_gist_content():
    stats = {"running": (6, 41.8), "walking": (1, 3.6), "cycling": (0, 0.0)}
    out = build_gist_content(stats)
    lines = out.splitlines()
    assert lines[0].startswith("🏃 Running") and "41.8 km" in lines[0]
    assert "92.1%" in lines[0] and "█" in lines[0]        # 41.8/45.4
    assert lines[1].startswith("🚶 Walking") and "7.9%" in lines[1]
    assert lines[2].startswith("🚴 Cycling") and "0.0%" in lines[2]
    assert "█" not in lines[2] and "░" in lines[2]        # 0km -> 빈 막대
    assert lines[-1].startswith("📅") and "45.4" in lines[-1] and "7회" in lines[-1]


def test_build_gist_content_all_zero():
    stats = {"running": (0, 0.0), "walking": (0, 0.0), "cycling": (0, 0.0)}
    out = build_gist_content(stats)
    assert "0.0%" in out.splitlines()[0]                  # 0으로 나눗셈 안 터짐
    assert "█" not in out and "░" in out
    assert "총 0.0 km · 0회" in out
