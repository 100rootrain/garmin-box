"""PythonRunner 검사용: run_batch.py와 같은 시작 신호·이벤트·MFA 규약을 흉내 낸다."""
import json
import subprocess
import sys

if sys.stdin.readline() != "START\n":
    sys.exit(3)


def emit(event, **data):
    print(json.dumps({"event": event, **data}, ensure_ascii=False), flush=True)


if sys.argv[-1] == "echo":
    emit("progress", message="한글 진행 메시지 ✓")
    emit("mfa_required")
    emit("done", code=sys.stdin.readline().strip())
elif sys.argv[-1] == "hang":
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"])
    emit("child", pid=child.pid)
    child.wait()
