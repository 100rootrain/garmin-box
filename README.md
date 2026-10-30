# garmin-box

[![README: English](https://img.shields.io/badge/README-English-blue)](README.en.md)

Garmin Connect의 운동 기록을 Supabase에 저장하고, 연간·누적 통계를 GitHub 프로필 gist에 표시합니다. Windows에서는 `GarminBox.exe` 하나로 설치, 연결 확인, 첫 동기화, 매일 자동 실행 등록까지 끝냅니다.

> Garmin 다운로드는 일반 가정·사무실 인터넷에서 실행하세요. OCI 같은 클라우드 IP에서는 Garmin이 429로 차단할 수 있습니다.

## 준비물

- Windows 10/11 64비트 PC와 인터넷 연결. Python·Git은 설치하지 않아도 됩니다.
- Garmin Connect 이메일과 비밀번호. 2단계 인증을 쓰면 연결 확인 때 프로그램 안에서 인증 코드를 입력합니다.
- Supabase 데이터베이스 연결 문자열. URI(`postgresql://...`)와 `host=... password=...` 형식 모두 쓸 수 있습니다. [연결 문자열 찾기](https://supabase.com/docs/guides/database/connecting-to-postgres)
- gist 권한만 있는 GitHub 토큰([만들기](https://github.com/settings/tokens/new?scopes=gist&description=garmin-box))과 통계를 표시할 gist([새 gist 만들기](https://gist.github.com/)). gist는 URL 또는 ID로 입력합니다.

## 1. 설치하기

1. [릴리스](https://github.com/100rootrain/garmin-box/releases/latest)에서 `GarminBox.exe`를 내려받아 실행합니다. 코드 서명이 없어 SmartScreen 경고가 뜨면 **추가 정보 → 실행**을 누릅니다.
2. 프로그램이 `%LOCALAPPDATA%\garmin-box`에 자신과 Python 실행 환경을 설치하고 시작 메뉴에 `garmin-box`를 만듭니다. 관리자 권한은 필요 없습니다.
3. Garmin 이메일·비밀번호, Supabase 연결 문자열, GitHub 토큰, Gist URL 또는 ID, 매일 실행 시각을 입력합니다.
4. **연결 확인**을 누릅니다. Garmin 로그인, Supabase 접속, GitHub 토큰과 gist 소유자를 확인합니다. 이 단계에서는 테이블이나 gist를 바꾸지 않습니다.
5. 갱신할 gist 파일을 고릅니다(기본 `garmin-stats`). 화면에 gist 공개 여부와 갱신할 파일이 표시됩니다.
6. **설정 시작**을 누릅니다. 설정 저장 → Supabase 테이블 생성 → 운동 기록 업로드 → gist 갱신 → 매일 자동 실행 등록 순서로 진행합니다. 기록이 많으면 첫 동기화가 오래 걸립니다. 실패하면 그 단계에서 멈추므로, 원인을 고친 뒤 다시 누르면 됩니다.

> 현재는 사용자 이름에 한글 등 영문이 아닌 문자가 있는 Windows 계정(예: `C:\Users\홍길동`)에서는 설치할 수 없습니다. 프로그램이 시작할 때 이를 알리고 아무것도 설치하지 않습니다.

입력한 비밀번호와 토큰은 `%USERPROFILE%\.GarminDb\GarminConnectConfig.json`과 `%USERPROFILE%\.garmin-box.env`에 평문으로 저장됩니다. 현재 사용자·SYSTEM·Administrators만 읽을 수 있게 권한을 제한하지만 암호화하지는 않습니다.

## 2. 매일 자동 실행

- 작업 스케줄러에 `garmin-box` 작업이 입력한 시각으로 등록됩니다.
- Windows에 로그인되어 있을 때만 실행합니다. Windows 비밀번호는 받지 않습니다.
- PC가 꺼져 있었다면 다음 로그인 후 실행합니다. 배터리로 동작 중이어도 실행하며, 절전·전원 설정은 바꾸지 않습니다.
- 최근 기록을 받은 뒤 빠진 기록이 있으면 전체 수집으로 보충합니다. Garmin의 기록이 모두 모였을 때만 Supabase와 gist를 갱신합니다.

## 3. 다시 열기

시작 메뉴에서 `garmin-box`를 엽니다.

- 저장한 설정과 최근 동기화 결과가 표시됩니다. 저장된 비밀번호·토큰은 보이지 않으며, 칸을 비워 두면 기존 값을 그대로 씁니다.
- 설정을 바꾸려면 값을 고친 뒤 **연결 확인 → 설정 시작**을 누릅니다.
- **지금 동기화**로 바로 동기화할 수 있습니다.
- 최근 결과가 `Garmin 재인증 필요`이면 비밀번호 칸을 비운 채 **연결 확인**을 눌러 다시 로그인합니다. 인증 코드를 요구하면 입력합니다.
- 한 Windows 사용자에서는 한 Garmin 계정만 쓸 수 있습니다. 운동 기록 DB가 있는 PC에서는 다른 Garmin 이메일로 바꿀 수 없습니다.
- Garmin에서 삭제한 활동은 Supabase와 gist 통계에 남을 수 있습니다. 이 경우 동기화 결과에 경고가 표시됩니다.

## 고급 설정

### 수동 실행

`run_garmin_batch.ps1`과 `run_garmin_batch_hidden.vbs`는 설치된 `%LOCALAPPDATA%\garmin-box\GarminBox.exe --sync`를 실행합니다. 프로그램을 설치하기 전에는 안내만 표시합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\run_garmin_batch.ps1
```

### 예전 수동 예약 작업 해제

예전 안내대로 `run_garmin_batch_hidden.vbs`나 `run_garmin_batch.ps1`을 작업 스케줄러에 직접 등록했다면, 설정 시작 때 프로그램이 그 작업을 찾아 알려 주고 새 예약을 켜지 않습니다. 작업 스케줄러에서 알려 준 작업을 **사용 안 함**으로 바꾼 뒤 프로그램의 **예약 다시 등록**을 누르세요. 예전 작업은 자동으로 지우지 않습니다.

### Linux / OCI

OCI 같은 클라우드 서버에서는 Garmin 다운로드를 실행하지 마세요. 클라우드 IP가 429로 차단될 수 있습니다. Windows PC에서 다운로드한 뒤, `garmin_activities.db`를 서버의 `$HOME/HealthData/DBs/garmin_activities.db`로 복사합니다. 다른 위치를 쓰면 서버의 `$HOME/.garmin-box.env`에 `GARMIN_SQLITE=/실제/경로/garmin_activities.db`를 추가하세요. 서버의 같은 파일에는 `SUPA_DSN`, `GITHUB_TOKEN`, `GIST_ID`도 설정합니다.

```bash
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
set -a
source "$HOME/.garmin-box.env"
set +a
.venv/bin/python sync_supa.py
.venv/bin/python update_gist.py
```

### 개발

Windows에서는 배포와 같은 Python 런타임으로 테스트합니다. C# 테스트에는 .NET 10 SDK가 필요합니다.

```powershell
powershell -ExecutionPolicy Bypass -File build\build-runtime.ps1   # build\out\runtime 생성
$PY = (Resolve-Path build\out\runtime\python\python.exe)
& $PY -I -X utf8 -m unittest discover -s tests -t . -v
dotnet test GarminBox.slnx
powershell -ExecutionPolicy Bypass -File build\release.ps1         # 번들 검증 후 build\out\publish\GarminBox.exe
```

통계 내용만 확인하려면 `.venv/bin/python update_gist.py --dry-run`을 실행합니다.

시크릿, 로그, 토큰 파일은 커밋하지 마세요. 영어 안내는 [README.en.md](README.en.md)를 참고하세요.

## 라이선스

이 저장소의 코드는 [MIT 라이선스](LICENSE)를 따릅니다.

Windows 릴리스의 `GarminBox.exe`에는 Python 실행 환경과 의존 패키지가 들어 있고, 각 패키지는 자기 라이선스를 따릅니다.

- [GarminDB](https://github.com/tcgoetz/GarminDB)와 같은 작성자의 `fitfile`·`idbutils`·`tcxfile`은 GPL-2.0입니다. `run_batch.py`는 GarminDB를 같은 Python 프로세스에서 직접 import해 쓰므로, 이들을 함께 배포하는 릴리스 EXE는 전체로서 GPL-2.0 조건을 따릅니다. 이 저장소의 코드 파일은 따로 떼어 쓰면 MIT 그대로입니다.
- `psycopg`·`psycopg-binary`는 LGPL-3.0, `certifi`와 `tqdm` 일부는 MPL-2.0이며 수정하지 않았습니다. 나머지는 MIT·BSD·Apache-2.0·PSF 계열입니다. 패키지 버전은 [`build/windows-requirements.lock`](build/windows-requirements.lock)에 고정돼 있습니다.
- 소스를 얻는 곳:
  - garmin-box(C# 실행 파일 포함): 이 저장소에서 릴리스와 같은 이름의 태그(예: [`v0.1.0`](https://github.com/100rootrain/garmin-box/tree/v0.1.0)).
  - Python 패키지: 각 패키지의 라이선스 전문과 `.py` 소스가 설치 후 `%LOCALAPPDATA%\garmin-box\runtime\python\Lib\site-packages`에 그대로 있습니다.
  - `psycopg-binary`의 네이티브 모듈(`_psycopg`·`pq`의 `.pyd`): 번들에는 Cython이 생성한 `.c`만 있습니다. 원래 Cython 소스(`psycopg_c/`)와 Windows wheel 빌드 스크립트(`tools/ci/`, `.github/workflows/packages-bin.yml`)는 psycopg 저장소의 같은 버전 태그(현재 [`3.3.6`](https://github.com/psycopg/psycopg/tree/3.3.6))에 있습니다.
