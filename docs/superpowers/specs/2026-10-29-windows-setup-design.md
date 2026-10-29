# garmin-box Windows 최초 설정 프로그램

작성일: 2026-10-29 (한국 시간)

## 목적과 합의한 범위

사용자는 새 Windows PC에서 설정 파일을 직접 편집하는 대신, `dms-deploy`처럼 EXE를 실행해 정보를 입력하고 설치를 끝내고 싶다. 사용자가 확인한 완료 범위는 **환경 준비 → 정보 입력 → 연결 확인 → 첫 동기화 → 매일 자동 실행 등록**이다. 재실행 시에는 설정 변경, 지금 동기화, 최근 결과 확인을 제공한다.

이 문서는 구현 전 검토용이다. 계정 생성, 실제 자격증명 입력, 외부 데이터 변경은 아직 수행하지 않았다.

## 접근 방식

| 방식 | 장점 | 부담 | 결정 |
|---|---|---|---|
| C# WinForms 설정 EXE + 기존 Python 동기화 | `dms-deploy`와 같은 Windows 사용 경험, 기존 수집·통계 로직 재사용, .NET 사전 설치 불필요 | 릴리스 때 Python과 의존성을 함께 검증·번들링 | 이 구성으로 진행 |
| Python GUI를 EXE로 패키징 | Python으로 한 언어에 모을 수 있음 | GarminDB의 패키지 데이터·동적 의존성까지 묶는 별도 패키징 검증 필요 | 이번 구성에서는 사용하지 않음 |
| 로컬 웹 설정 화면 | 브라우저로 입력 가능 | 로컬 서버의 시작·종료·접근 제어가 추가됨 | 이번 규모에서는 사용하지 않음 |

Windows 10/11 x64, 한국어 화면, 현재 Windows 사용자 한 명을 대상으로 한다. C# WinForms, `net10.0-windows`, `SelfContained=true`, `PublishSingleFile=true`인 단일 EXE를 배포한다. 사용자에게 Python, Git, GitHub CLI, .NET 설치를 요구하지 않는다.

## 사용자가 준비하는 정보

- Garmin Connect 이메일과 비밀번호. 인증이 요구될 때만 같은 프로그램 안에서 일회용 인증 코드를 입력한다.
- Supabase PostgreSQL 연결 문자열. URI와 `host=... password=...` 형식을 모두 허용한다. Supabase 계정/프로젝트 생성과 연결 문자열 발급은 Supabase에서 진행한다.
- gist 쓰기 권한이 있는 GitHub 토큰과 대상 Gist URL 또는 ID. 토큰 발급과 gist 생성은 GitHub에서 진행하며 안내 링크를 제공한다.
- 매일 실행할 시각. 기본 제안은 PC 현지 시각 09:10이며 사용자가 변경한다.

GitHub 인증은 gist 쓰기 권한만 있는 토큰의 가림 입력으로 통일한다. `gh auth token`으로 저장소 접근 권한까지 있는 토큰을 가져오지 않는다. 특정 계정, gist ID, Supabase 주소는 프로그램에 내장하지 않는다.

## 실행 흐름

### 1. 시작과 환경 준비

- 첫 실행은 정보 입력 화면으로, 설치된 PC에서는 기존 설정과 최근 동기화 결과 화면으로 진입한다.
- 설치 위치는 `%LOCALAPPDATA%\garmin-box`로 고정한다. 내려받은 EXE를 이 위치에 복사하고, Python 런타임·스크립트·스키마·런처를 고정 폴더에 배치한다. 다운로드 폴더의 EXE를 옮겨도 예약 작업이 깨지지 않도록 한다. 파일을 배치할 때도 공통 실행 잠금을 잡아 동기화 중인 코드를 덮어쓰지 않는다.
- 시작 메뉴 바로가기로 설치된 프로그램을 다시 열 수 있게 한다. 바탕화면 바로가기는 만들지 않는다.
- 항상 앱 전용 portable Python 하나를 사용한다. 릴리스 빌드 때 Windows x64 런타임과 고정한 GarminDB/psycopg 의존성을 설치한 번들을 만들고 단일 EXE에 포함한다. 대상 PC에서는 무결성 확인 후 압축만 푼다. 기존 Python 탐지, Store 별칭 처리, Python 설치 프로그램 실행, venv 생성, 대상 PC의 PyPI 접속은 필요 없다. 시스템 PATH·레지스트리·파일 연결·다른 Python 설치를 바꾸지 않는다.
- 번들은 `python-build-standalone`의 install-only 배포본 또는 python.org embeddable 배포본으로 만든다. embeddable에는 pip가 없으므로 대상 PC에서 pip를 추가해 설치하는 구조는 사용하지 않는다. 정확한 배포본·Python/의존성 버전·해시·동작 확인 결과는 실행 검증 뒤 별도 문서와 커밋으로 확정한다. 빌드 경로와 다른 경로로 옮겨 실행하여 경로 고정 문제와 네이티브 DLL 누락을 확인한다.
- [번들 실행 검증](../verification/2026-10-29-windows-verification.md)에서 CPython 3.13.18/GarminDB 3.9.0 후보는 ASCII 경로에서 통과했지만, 한글 경로의 기본 HTTPS 인증서 로드는 실패했다. 한글 사용자 프로필에서도 인증서 검증을 유지하는 해결책을 검증하기 전에는 지원 완료로 확정하지 않는다. 검증에 쓴 의존성 목록은 배포 승인 목록과 구분한다.
- 사용자 권한으로 실행한다. OS 정책이나 권한 때문에 진행할 수 없다면 해당 원인을 표시하고 멈춘다. 관리자 권한 요구나 시스템 정책 변경은 기본 흐름에 넣지 않는다.

### 2. 정보 입력과 연결 확인

- Garmin 계정, Supabase, GitHub/gist, 실행 시각을 한 화면에서 입력한다. 비밀번호·토큰·비밀번호가 포함된 DSN은 가림 입력한다.
- GitHub 토큰의 로그인 사용자와 gist 소유자를 확인하고, 대상 파일 이름을 기존 gist에서 선택한다. 새 파일 이름의 기본값은 `garmin-stats`다. gist 공개 여부와 실제로 갱신할 파일을 저장 전 보여 준다.
- `연결 확인`은 Garmin 인증과 프로필 조회, PostgreSQL 접속, GitHub 사용자·gist 조회를 실행한다. gist를 수정하거나 테이블을 만들지는 않는다. Garmin이 발급한 로그인 토큰 캐시는 저장할 수 있다.
- Garmin 인증은 번들 GarminDB와 같은 `garminconnect` 및 같은 토큰 저장 경로를 사용한다. 공통 Python 진입점의 MFA 콜백이 줄바꿈으로 끝나는 인증 요청 이벤트를 GUI에 보내고, stdin으로 인증 코드 한 줄을 받는다. 로그인 세션은 코드 입력 동안 유지한다. CLI의 줄바꿈 없는 `MFA code: ` 문자열을 줄 단위로 기다리는 방식은 사용하지 않는다.
- C#과 Python의 표준 입출력은 UTF-8로 고정한다. C#의 `StandardInputEncoding`, `StandardOutputEncoding`, `StandardErrorEncoding`을 BOM 없는 UTF-8로 명시하고 Python은 `-I -X utf8`로 실행한다. Windows 기본 인코딩에 맡기면 한글 진행 메시지가 깨질 뿐 아니라 JSON 해석까지 실패하는 사례를 실행으로 재현했다. 한글 진행·응답 메시지가 포함된 프로세스 간 왕복 검증을 포함한다.
- 예약 실행에서는 MFA 콜백이 입력을 기다리지 않고 `Garmin 재인증 필요` 상태와 전용 종료 코드를 반환한다. stdin의 EOF만으로 일반 실패와 재인증 필요를 구분했다고 보지 않는다. 사용자가 프로그램을 열고 재연결할 수 있게 최근 결과에 표시한다.
- 검증은 백그라운드에서 실행하며 실패한 연결 항목, 원인, 다시 시도할 방법을 화면에 표시한다. 검증 도중 입력을 바꾸면 기존 검증 결과는 무효화한다.

### 3. 설치·첫 동기화·예약 등록

- `설정 시작` 버튼 옆에 설정 파일 저장, Supabase 테이블 생성, 운동 기록 업로드, 지정 gist 파일 갱신, 예약 등록을 수행한다는 설명을 둔다.
- 설정을 안전하게 저장한 다음 `schema.sql`의 `create table if not exists`를 실행한다. 기존 테이블의 자동 마이그레이션이나 스키마 전체 비교는 만들지 않는다. 필수 열과 거리·속도의 소수점 보존 조건은 확인한다. 예를 들어 `distance_km numeric(8,0)`은 오류 없이 거리를 반올림하므로 그대로 쓰지 않는다. 그 밖의 SQL 오류는 원인을 표시하고 중단하며 테이블을 변경하거나 삭제하지 않는다.
- 최초 다운로드와 매일 다운로드를 구분한다. 현재 `--latest`와 기본 25개만으로 최초 통계를 만들지 않는다. [실제 계정 검증](../verification/2026-10-29-windows-verification.md)에서 GarminDB와 같은 직접 API 경로의 `limit=2000` 요청으로 전체 1,138개의 고유 활동이 반환됐다. 최초에는 전체 건수를 조회해 GarminDB의 `download_all_activities=max(2000, 전체 건수)`로 설정하고 `--latest` 없이 수집한다. 응답/고유 활동 ID 수가 전체와 일치하는지 확인하며, 불일치하면 성공 처리하지 않는다. 직접 페이징은 기본 구현에 넣지 않고 실제 잘림이나 요청 제한이 확인된 경우에 추가한다. 1,000은 garminconnect의 일반 조회 함수에 있는 로컬 제한으로, 직접 API의 보편적 서버 상한이라고 보지 않는다. 2,000을 넘는 응답이나 다른 계정의 상한은 아직 미검증이다.
- 공통 Python 진입점이 인증 어댑터로 활동 목록을 직접 조회하고 예외를 전달받는다. 성공 응답의 빈 목록만 0건으로 처리하고 `None`, 인증 실패, 429, 통신 실패는 실패로 처리한다. GarminDB가 개별 활동 다운로드 예외를 로그만 남기고 삼키는 경로도 실패로 승격하여 불완전한 수집을 완료로 표시하지 않는다.
- 기존 `sync_supa.py`의 활동 ID 기준 upsert와 `update_gist.py`의 통계 생성 로직을 재사용한다. `sync_supa.py`의 현재 무조건적인 0행 실패 처리는 변경 대상이다. Garmin 조회가 성공한 빈 계정에는 빈 로컬 DB/테이블을 준비하고 0건 동기화를 허용한다. 로컬 0행만 보고 신규 빈 계정이라고 추정하지 않는다. 기존 Supabase에 활동이 남아 있으면 자동 삭제하거나 0건 통계라고 표시하지 않고 불일치를 안내한다.
- 첫 동기화는 오래 걸릴 수 있으므로 단계, 처리 건수, 대기 상태를 표시한다. 실패하면 그 단계에서 멈추며 다시 시도할 수 있게 한다. 이미 생성한 테이블·업로드한 기록은 지우지 않는다.
- 성공한 경우에만 작업 스케줄러에 매일 실행을 등록하고 등록된 정의를 다시 확인한다. 사용자에게 설정 완료, 첫 동기화 결과, 다음 실행 시각, gist 링크를 보여 준다.

## 파일과 기존 실행 방식의 관계

기존 수동 실행과 호환하기 위해 다음 파일과 경로를 사용한다.

| 내용 | 경로 |
|---|---|
| Garmin 설정 | `%USERPROFILE%\.GarminDb\GarminConnectConfig.json` |
| Garmin 인증 토큰 | 설치한 GarminDB가 사용하는 `%USERPROFILE%\.GarminDb` 아래 토큰 파일 |
| Supabase/GitHub 연결값 | `%USERPROFILE%\.garmin-box.env` |
| 기존 로컬 운동 DB | Garmin 설정의 `directories.base_dir`와 `relative_to_home`으로 계산한 `DBs\garmin_activities.db` (기본 `%USERPROFILE%\HealthData\DBs\garmin_activities.db`) |
| 앱·portable Python·고정 의존성·실행 코드 | `%LOCALAPPDATA%\garmin-box` 아래 |
| 실행 시각·마지막 결과 등 비밀값 없는 앱 상태 | `%LOCALAPPDATA%\garmin-box\settings.json`, `status.json` |
| 비밀값이 제거된 로그 | `%LOCALAPPDATA%\garmin-box\logs` |

Garmin 설정은 설치한 GarminDB의 예제 구조를 기준으로 만들고 사용자 입력과 앱에 필요한 기본값만 반영한다. 이미 있는 파일에서는 알 수 없는 설정과 사용자 지정 데이터 경로를 보존한다. `.env` 값은 첫 `=`만 구분자로 사용하며 연결 문자열 안의 `=`를 보존한다. 줄바꿈·NUL을 포함한 입력은 거부한다. 손상된 기존 설정을 기본값으로 덮어쓰지 않는다.

Windows 전체 동기화의 DB 경로 기준은 Garmin 설정이다. GarminDB의 경로 계산을 재사용해 절대 DB 경로를 구하고, 설정 저장 때 `.garmin-box.env`의 `GARMIN_SQLITE`에도 같은 값을 쓴다. 기존 두 값이 다르면 동기화 전에 불일치를 알리고 같은 경로로 맞춘다. Linux/OCI에서 다운로드 없이 동기화만 실행할 때는 기존 `GARMIN_SQLITE` 지정 방식을 유지한다.

처음 저장하거나 수정할 때 공통 실행 잠금을 잡고 앱 상태를 `설정 미완료`로 표시한다. 파일별로 임시 파일에 완성한 뒤 원자적으로 교체하고 백업을 한 개 보관한다. 파일 간 자동 롤백은 만들지 않는다. 어느 한 파일이라도 저장에 실패하면 오류를 표시하고 다시 저장하게 하며, 두 파일 저장과 검증이 끝날 때까지 수동·예약 동기화를 막는다. 백업에도 원본과 같은 비밀값 보호를 적용한다.

실행과 실패 판정은 `run_batch.py` 하나로 모은다. 이 진입점이 설정 상태/경로 확인, 잠금, Garmin 다운로드·가져오기, Supabase upsert, gist 갱신, 상태 기록과 로그의 비밀값 제거를 담당한다. 성공·일반 실패·재인증 필요를 서로 다른 종료 코드로 돌려준다. EXE와 `run_garmin_batch.ps1`, `.sh`는 이 진입점을 호출하고 별도의 로그인 실패 판정이나 토큰 삭제 로직을 갖지 않는다. 기존 숨김 VBS와 Linux/OCI의 다운로드 없는 수동 동기화도 계속 제공한다.

고정할 GarminDB/garminconnect 버전에서 만료·거부된 캐시 토큰의 자격증명 폴백을 실행 검증한다. 폴백이 동작하면 현재 배치의 토큰 삭제 후 재시도는 제거한다. `Start-Transcript`도 제거하여 정제 전 출력을 파일에 남기지 않는다. README는 새 PC의 EXE 실행을 우선 안내하고 수동 방식과 기존 예약 작업 해제는 고급 안내로 둔다. 영어 README의 DSN 형식 설명도 실제 psycopg 동작과 일치시킨다.

## 자동 실행과 재실행

- 작업 이름은 `garmin-box`이며, 현재 사용자·일반 권한·로그인된 상태에서만 실행한다. Windows 계정 비밀번호를 받지 않는다. 이 조건을 실행 시간 입력란과 완료 화면에 표시한다.
- PC가 꺼져 있거나 로그아웃 상태인 동안은 실행할 수 없다. 로그인 후 놓친 실행을 수행하도록 설정한다. 절전 해제나 Windows 전원 정책은 변경하지 않는다.
- 작업 등록·재확인은 Windows Task Scheduler COM API(`Schedule.Service`)와 XML을 사용한다. `DisallowStartIfOnBatteries=false`, `StopIfGoingOnBatteries=false`, `ExecutionTimeLimit=PT0S`, `MultipleInstancesPolicy=IgnoreNew`를 명시한다. 작업 전체는 장시간 최초 수집을 강제 종료하지 않고, 네트워크/하위 프로세스별 제한 시간은 공통 진입점에서 관리한다. 계정은 SYSTEM이 아닌 현재 사용자의 `InteractiveToken`이며 트리거는 매일 지정한 시각이다.
- 예약 작업은 설치된 고정 경로의 `GarminBox.exe --sync --scheduled`를 실행하며 콘솔 창을 띄우지 않는다. 앱 화면의 `지금 동기화`도 같은 실행 경로를 쓴다.
- 앱과 예약 작업은 공통 잠금을 사용하고 작업 스케줄러도 중복 실행을 막는다. 기존 작업이 진행 중이면 새 작업을 시작하지 않고 진행 상태를 표시한다.
- 이미 같은 이름의 예약 작업이 있으면 이 앱이 등록한 작업인지 경로·등록 정보를 확인한 뒤에만 갱신한다. 예전 README가 작업 이름을 지정하지 않았으므로, 다른 이름의 작업도 실행 동작과 인수의 `run_garmin_batch_hidden.vbs`/`run_garmin_batch.ps1`을 기준으로 찾아 안내한다. 기존 수동 작업은 자동 삭제·이전하지 않고 사용자에게 해제를 안내하며, 활성 작업이 남아 있으면 새 예약을 중복 활성화하지 않는다. 관계없는 작업은 덮어쓰지 않는다.
- 예약 등록 실패는 `동기화 성공 / 자동 실행 설정 실패`로 구분한다. 성공한 gist 갱신을 다시 수행하지 않고 예약 등록부터 재시도할 수 있게 한다.
- 재실행하면 기존 설정을 불러온다. 저장된 비밀값은 기본적으로 가리고, 빈 입력칸으로 저장했다는 이유로 기존 비밀값을 지우지 않는다. 연결값 변경은 다시 검증한 뒤 저장한다.
- 한 Windows 사용자에서는 한 Garmin 계정을 지원한다. 이미 로컬 운동 DB가 있으면 다른 Garmin 이메일로의 변경은 막아 두 계정의 기록이 섞이지 않게 한다. 동일 계정의 비밀번호·토큰·DB·실행 시각 변경은 지원한다.

## 비밀값과 실패 처리

- 기존 Python 배치와 호환하기 위해 자격증명 파일은 평문 형식으로 저장한다. 기본은 프로필의 상속 ACL을 사용하되, 원본·백업·토큰 파일에 현재 사용자/SYSTEM/Administrators 외 읽기·쓰기 허용이 있는지 확인한다. 과도한 권한이 있으면 해당 파일에 한해 제한하며 실패하면 완료 처리하지 않는다. 사용자 프로필 전체의 ACL은 변경하지 않는다. 파일 자체를 암호화했다고 표현하지 않는다.
- 비밀값은 프로그램 인자나 로그에 넣지 않는다. 기존 스크립트가 읽는 `SUPA_DSN`, `GITHUB_TOKEN` 등은 자식 프로세스의 환경에만 넣고 시스템·사용자 영구 환경 변수에 저장하지 않는다. 최초 연결 확인용 Garmin 이메일/비밀번호도 자식 환경으로 전달할 수 있다. 별도 비밀값 전송 규약은 만들지 않고 stdin은 MFA 코드 응답에 사용한다.
- 외부 라이브러리 출력, 예외, DSN, 인증 URL 쿼리, 토큰은 파일에 기록하기 **전에** 비밀값을 제거한다. 원문을 임시 로그나 PowerShell transcript에 먼저 기록한 뒤 가리는 구조는 사용하지 않는다.
- 네트워크 요청과 하위 프로세스에는 제한 시간을 두고, 연결 차단·잘못된 비밀번호·권한 부족·Garmin 재인증·속도 제한을 구분한다. Garmin 속도 제한에 빠르게 반복 로그인하지 않는다.
- 최초 연결·진행 상태와 실패 후 재시도는 UI가 멈추지 않게 한다. 설치 중 강제 종료 후 재실행하더라도 부분 파일만으로 완료라고 판정하지 않는다.

## 검증과 완료 조건

- 의미 있는 자동 검증: 기존 JSON 설정과 사용자 지정 DB 경로 보존, `.env`의 `=`/Unicode/빈값 처리, 잘못된 입력 거부, 일부 파일 저장 실패 시 실행 차단, 과도한 파일 ACL 탐지, 비밀값 제거, Gist URL/파일 선택, MFA 요청·응답·예약 실행 중 전용 상태 반환, 실제 0건과 조회/개별 다운로드 실패 구분, 실패 후 후속 단계 차단, 동일 실행 잠금, 이름이 다른 기존 작업 탐지, 배터리 조건을 포함한 예약 정의 재확인. 페이지 수집 검증은 API 실측 결과에 따라 추가한다.
- 외부 서비스는 테스트 대역으로 검증한다. 테스트에서 실제 Supabase에 쓰거나 gist를 갱신하지 않는다.
- Release 빌드와 self-contained publish를 실행한다. 일반 Windows 사용자 계정에서 정보 입력 화면, 가림 입력, 오류·재시도, 진행 표시, 한국어·화면 배율을 확인한다.
- Python/Git/gh/.NET이 사전 설치되지 않은 깨끗한 Windows 환경에서 환경 준비와 예약 등록을 검증한다. 가능하지 않으면 미검증으로 명확히 남긴다.
- 실제 Garmin/Supabase/GitHub 연결과 실제 첫 동기화는 사용자 입력이 있어야 검증할 수 있다. EXE 빌드 성공이나 테스트 대역 통과를 실제 계정 설치 완료라고 보고하지 않는다.
- 사용자가 EXE 하나를 받아 실행하고 필요한 정보를 입력한 뒤, 두 설정 파일 편집 없이 첫 동기화와 매일 실행 등록을 마치면 제품 목표를 충족한다.

## 이번 범위 밖과 구현자가 정할 부분

이번 범위 밖: 계정/프로젝트 자동 생성, GitHub OAuth 앱 등록, Windows 로그아웃 중 실행, 다중 Garmin 계정, 자동 업데이트 서비스, 데이터 초기화/삭제, Windows 서비스, 웹 대시보드, 코드 서명 인증서 구매.

구현자 판단: 내부 클래스·파일 분할, UI 배치와 색상, 세부 제한 시간과 재시도 횟수. Python 번들의 검증 버전·경로별 결과와 실제 계정의 단일 활동 목록 요청 결과는 [실행 검증 기록](../verification/2026-10-29-windows-verification.md)을 따른다. 한글 프로필의 인증서 문제, 더 큰 계정의 API 상한처럼 실행으로 확인하지 못한 항목은 미검증으로 남기며 소스 확인이나 테스트 대역을 실제 검증으로 대체하지 않는다. GarminDB CLI를 감싸거나 인증 어댑터를 재사용하되, 운동 기록 스키마와 통계 의미는 바꾸지 않는다.

## 근거

- 이 저장소: `README.md`, `README.en.md`, `requirements.txt`, `run_garmin_batch.ps1`, `sync_supa.py`, `update_gist.py`, `schema.sql`.
- [GarminDB v3.9.0 설정 예제](https://github.com/tcgoetz/GarminDB/blob/v3.9.0/garmindb/GarminConnectConfig.json.example), [인증 어댑터](https://github.com/tcgoetz/GarminDB/blob/v3.9.0/garmindb/garmin_connect_auth_adapter.py), [다운로드 처리](https://github.com/tcgoetz/GarminDB/blob/v3.9.0/garmindb/download.py), [의존성](https://github.com/tcgoetz/GarminDB/blob/v3.9.0/requirements.txt): 활동 목록 예외를 로그만 남기고 처리하는 경로와 MFA 콜백을 소스로 확인했다. 배포에 사용할 버전은 실행 검증으로 확정한다.
- [python-garminconnect 인증 안내](https://github.com/cyberjunky/python-garminconnect#-authentication): MFA와 토큰 재사용 방식.
- [Python embeddable 배포](https://docs.python.org/3.13/using/windows.html#the-embeddable-package), [python-build-standalone install-only 배포](https://gregoryszorc.com/docs/python-build-standalone/main/distributions.html#install-only-archive): 앱 전용 런타임과 의존성 번들링.
- [Windows 작업 실행 계정](https://learn.microsoft.com/en-us/windows/win32/taskschd/principal-logontype): 로그인된 사용자로 실행하는 `TASK_LOGON_INTERACTIVE_TOKEN` 조건.
- [작업 스케줄러 배터리 조건](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-disallowstartifonbatteries-settingstype-element): 배터리에서 시작을 금지하는 기본값을 명시적으로 해제한다.
- [PostgreSQL numeric](https://www.postgresql.org/docs/current/datatype-numeric.html): 선언한 소수점 자릿수로 자동 반올림될 수 있어 SQL 오류만으로 스키마 호환성을 보장하지 않는다.
- [GitHub Gist API](https://docs.github.com/en/rest/gists/gists): gist 인증과 파일 조회·갱신.
