# garmin-box

[![README: English](https://img.shields.io/badge/README-English-blue)](README.en.md)

Garmin Connect의 운동 기록을 Supabase에 저장하고, 연간·누적 통계를 GitHub 프로필 gist에 표시합니다. Garmin, GitHub, Supabase 계정과 연결값을 이미 준비한 Windows 사용자를 위한 안내입니다.

> Garmin 다운로드는 일반 가정·사무실 인터넷에서 실행하세요. OCI 같은 클라우드 IP에서는 Garmin이 429로 차단할 수 있습니다.

## 준비물

- Windows PC와 인터넷 연결
- Garmin Connect 로그인 정보
- GitHub gist 권한 토큰과 gist ID
- Supabase 데이터베이스 연결 문자열
- [Python](https://www.python.org/downloads/) 3 설치 (`py --version`으로 확인)

## 1. 설치하기

PowerShell을 열고 아래를 한 줄씩 실행합니다.

```powershell
git clone https://github.com/100rootrain/garmin-box.git
cd garmin-box
py -m venv .venv
.venv\Scripts\pip install -r requirements.txt
```

Git이 없다면 [Git for Windows](https://git-scm.com/download/win)를 먼저 설치합니다.

## 2. Garmin 계정 연결하기

1. `.venv\Lib\site-packages\garmindb\GarminConnectConfig.json.example` 파일을 엽니다.
2. 내용을 `%USERPROFILE%\.GarminDb\GarminConnectConfig.json`으로 저장합니다. 폴더가 없으면 만듭니다.
3. 파일에 Garmin Connect 로그인 정보를 입력하고 저장합니다.

## 3. Supabase와 GitHub 연결하기

1. `%USERPROFILE%\.garmin-box.env` 파일을 새로 만듭니다.
2. 아래 내용을 붙여 넣고, 준비한 값으로 바꿉니다. `SUPA_DSN`은 Supabase 연결 문자열을 그대로 사용합니다.

```text
SUPA_DSN=postgresql://postgres:PASSWORD@db.PROJECT.supabase.co:5432/postgres
GITHUB_TOKEN=github_gist_token
GIST_ID=your_gist_id
```

3. Supabase SQL Editor에서 저장소의 `schema.sql` 내용을 실행합니다. 이 작업은 처음 한 번만 합니다.

> `.garmin-box.env`에는 비밀번호와 토큰이 있으므로 GitHub에 올리지 마세요.

## 4. 처음 실행하기

PowerShell에서 `garmin-box` 폴더로 이동한 뒤 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\run_garmin_batch.ps1
```

성공하면 `batch.log` 마지막에 `done:`이 표시됩니다. 운동 기록은 Supabase에, 통계는 지정한 gist에 반영됩니다.

문제가 나면 같은 폴더의 `batch.log` 마지막 부분을 확인하세요. Garmin 로그인 오류는 `%USERPROFILE%\.GarminDb\GarminConnectConfig.json`, 연결·토큰 오류는 `%USERPROFILE%\.garmin-box.env` 값을 다시 확인하면 됩니다.

## 5. 매일 자동으로 실행하기

1. Windows에서 **작업 스케줄러**를 엽니다.
2. **작업 만들기**를 선택하고, 트리거에서 매일 실행할 시간을 정합니다.
3. 동작에서 **프로그램 시작**을 선택합니다.
4. 프로그램/스크립트에 `wscript.exe`를 입력합니다.
5. 인수 추가에 아래처럼 저장소의 실제 경로를 입력합니다.

```text
"C:\경로\garmin-box\run_garmin_batch_hidden.vbs"
```

이 런처는 PowerShell 창을 띄우지 않고 실행하며, 결과는 `batch.log`에 남습니다.

## 고급 설정

### Linux / OCI

OCI 같은 클라우드 서버에서는 Garmin 다운로드를 실행하지 마세요. 클라우드 IP가 429로 차단될 수 있습니다. Windows PC에서 다운로드한 뒤, 서버에서는 필요할 때 `sync_supa.py`와 `update_gist.py`만 실행하거나 gist 갱신만 예약합니다.

```bash
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
.venv/bin/python sync_supa.py
.venv/bin/python update_gist.py
```

### 개발

```bash
.venv/bin/python -m pytest -v
.venv/bin/python update_gist.py --dry-run
```

시크릿, 로그, 토큰 파일은 커밋하지 마세요. 자세한 영어 안내는 [README.en.md](README.en.md)를 참고하세요.
