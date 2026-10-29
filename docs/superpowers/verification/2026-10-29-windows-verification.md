# Windows Python 번들과 Garmin API 실행 검증

검증일: 2026-10-29. 대상 설계: [Windows 최초 설정 프로그램](../specs/2026-10-29-windows-setup-design.md).

## 현재 결과

- Python 번들: Windows 10 x64에서 빌드 폴더와 다른 경로로 압축 해제한 뒤 실제 실행했다. ASCII 경로는 통과했다. 한글 경로는 기본 인증서 경로로 HTTPS를 실행하면 실패하며, 동일한 인증서를 ASCII 경로에 지정한 경우에만 아래 검사를 통과했다. 한글 경로를 그대로 지원하는 배포본으로 확정할 수 없다.
- Garmin API: 실제 계정에서 `limit=2000` 요청이 수락됐고 전체 1,138개의 고유 활동을 한 번에 받았다. 조회 전후 전체 건수와 마지막 offset도 일치했다. 이 경로의 서버가 1,000건에서 응답을 자른다는 가정은 해당 계정에서 성립하지 않았다. 서버의 보편적인 최대값까지 입증한 것은 아니다.

## 검증 환경과 고정 입력

| 항목 | 값 |
|---|---|
| 실행 OS | Windows 10 x64, 빌드 19045 |
| Python | CPython 3.13.18, 일반 GIL 빌드 |
| 런타임 배포 | python-build-standalone `20261026`, Windows x86_64 install-only stripped |
| 원본 런타임 SHA-256 | `7369f7a35f261222a6ac8b3a87c1c88eeebee8af945d85346202591a64038dd3` |
| 검증 번들 ZIP 크기 | 30,191,374 bytes |
| 검증 번들 ZIP SHA-256 | `46524c8f04fdaa45efcf4c2d6feefb11ebab8ab120897cf8ad171529d3634f76` |
| 직접 의존성 | GarminDB 3.9.0, garminconnect 0.3.11, psycopg/psycopg-binary 3.3.6 |
| 주요 네이티브 의존성 | SQLAlchemy 2.0.52, greenlet 3.5.6, curl_cffi 0.17.0, cffi 2.1.1 |
| libpq | `180004` |

전체 패키지 목록은 [windows-runtime-requirements.txt](windows-runtime-requirements.txt)에 기록했다. 런타임은 [해당 GitHub 릴리스](https://github.com/astral-sh/python-build-standalone/releases/tag/20261026)의 `cpython-3.13.18+20261026-x86_64-pc-windows-msvc-install_only_stripped.tar.gz`를 사용했고 다운로드 후 GitHub asset digest와 SHA-256이 같은지 확인했다. 빌드용 pip으로 PyPI에서 의존성을 설치한 다음 ZIP을 만들었다. 대상 경로에서 pip 설치를 다시 실행하지 않았다.

## 번들 실행 항목

빌드 폴더의 Python과 별도로 ZIP을 새 ASCII 경로와 공백·한글이 포함된 경로에 각각 풀었다. `PATH`는 Windows System32만 남기고, `PYTHONHOME`과 `PYTHONPATH`에는 존재하지 않는 경로를 지정한 뒤 `-I -X utf8`로 실행했다.

| 검사 | ASCII 경로 | 한글 경로 + ASCII CA 지정 |
|---|---|---|
| GarminDB·garminconnect·FIT/TCX 관련 모듈 import | 통과 | 통과 |
| psycopg/libpq, cffi, curl_cffi, greenlet 등 네이티브 모듈 로드 | 통과 | 통과 |
| libpq 연결 문자열 파싱 (`=` 포함 비밀번호) | 통과 | 통과 |
| curl_cffi HTTPS 인증서 검증 및 PyPI JSON 조회 | 통과 | 통과 |
| GarminDB 설정 예제 로드와 사용자 지정 DB 경로 계산 | 통과 | 통과 |
| 실제 ActivitiesDb 생성, SQLAlchemy/SQLite에 합성 활동 저장·조회 | 통과 | 통과 |
| 번들의 `garmindb_cli.py --help` 실행 | 통과 | 통과 |
| 저장소의 기존 테스트 4개 | 통과 | 통과 |
| `pip check` | 통과 | 통과 |

검사 코드는 [verify_windows_bundle.py](../../../tools/verify_windows_bundle.py)에 있다. 기존 테스트는 pytest 스타일 함수 3개와 unittest 메서드 1개를 그대로 실행했으며, 테스트 결과가 0건이면 성공으로 처리하지 않았다. 제품 코드나 실제 사용자 DB는 변경하지 않았다.

## 한글 경로 HTTPS 실패

동일한 ZIP을 한글 경로에 풀어 기본 설정으로 HTTPS 요청을 실행하면 `curl_cffi.requests.exceptions.SSLError`, curl 오류 `77`이 발생했다. certifi의 `cacert.pem` 파일이 존재하고 Python으로 읽을 수 있지만 libcurl이 해당 경로에서 trust anchor를 읽지 못했다.

같은 파일 내용을 ASCII 경로로 복사해 `verify=<CA 경로>` 또는 자식 프로세스의 `REQUESTS_CA_BUNDLE`로 지정하면 HTTP 200을 받았다. 인증서 검증은 계속 활성화했고, 원본과 복사본의 바이트가 같은지도 검사했다. `CurlOpt.CAINFO_BLOB`은 이 버전에서 `NotImplementedError`이므로 해결책으로 확정하지 않는다.

이 우회는 현재 검증용 임시 폴더가 ASCII 경로인 환경에서만 확인했다. 사용자 프로필·임시 폴더 전체가 한글인 PC에서도 쓸 수 있는 제품 해결책은 아직 확정하지 않았다. 설치 경로를 고정했다는 이유만으로 이 문제가 사라진다고 가정하지 않는다.

제품 설계의 현재 동작은 비 ASCII 설치·CA 경로를 첫 사전점검에서 감지하고, 이유와 실제 경로를 표시한 뒤 자격증명 입력·로그인·파일 배치 전에 중단하는 것이다. 검증 도구의 명시적인 `--ca-bundle` 실험과 제품의 자동 우회를 구분한다. `activeCodePage=UTF-8` 매니페스트는 [Windows 공식 문서](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/use-utf8-code-page)에 있는 해결 후보지만, 이 Python 번들의 리소스·동작을 보존하는지와 CA 우회 없는 한글 경로 실행은 아직 검증하지 않았다. 8.3 경로나 공용 CA 폴더를 제품의 자동 우회로 채택하지 않는다.

## Garmin API 검증 방법과 한계

garminconnect 0.3.11의 `get_activities()`는 `MAX_ACTIVITY_LIMIT=1000`을 로컬에서 검사한다. 이 함수를 호출해 1,000건보다 큰 값을 거부한 결과로 Garmin 서버 상한을 결론 내리면 안 된다. GarminDB는 이 함수를 거치지 않고 `connectapi()`로 활동 목록을 요청한다.

[verify_garmin_api.py](../../../tools/verify_garmin_api.py)는 같은 직접 요청 경로에서 다음을 읽기 전용으로 확인한다.

1. 계정의 전체 활동 건수 조회.
2. `start=0`에서 `limit=25`, `1000`, `max(2000, min(전체 건수 + 1, 10000))` 요청의 실제 응답 건수·고유 ID 수 비교.
3. 단일 요청이 전체를 반환하지 않으면 offset을 늘려 누락분 조회. 각 요청 사이에 2초 대기하며 429나 오류가 나면 중단.
4. 마지막 offset 또는 전체 고유 ID 수를 확인하고 종료 시점의 활동 건수와 다시 대조.

결과에는 이메일·비밀번호·토큰·운동 이름·개별 활동 ID를 남기지 않는다. 요청/응답 건수와 ID 목록의 집계 해시만 기록한다. 검증은 Garmin 활동을 수정하거나 Supabase/gist를 갱신하지 않는다. 이 계정으로 입증할 수 있는 범위와 서버의 보편적인 최대값을 구분한다. 계정의 기록 수가 충분하지 않다면 서버 최대값은 미확정이다.

### 실제 계정 관측 결과

2026-10-29 14:21 KST에 시작한 검증은 한글 경로의 Python 번들에 동일한 certifi 인증서를 ASCII 경로로 지정한 상태에서 실행했다. 사용자가 검증용 창에 직접 로그인했고, 다음 결과가 저장됐다.

| start | limit | 실제 응답 | 고유 활동 ID 수 |
|---:|---:|---:|---:|
| 0 | 25 | 25 | 25 |
| 0 | 1000 | 1000 | 1000 |
| 0 | 2000 | 1138 | 1138 |
| 1137 | 2 | 1 | 1 |

조회 전 전체 건수, 단일 요청의 고유 ID 수, 종료 시점 전체 건수가 모두 1,138로 일치했다. 마지막 offset의 1건도 전체 응답의 마지막 ID와 일치했다. `status=passed`, `single_request_covers_total=true`, `observed_truncation=false`, `last_offset_matches=true`, `consistent=true`였다.

따라서 이번 계정의 최초 활동 목록은 GarminDB 설정의 `download_all_activities`를 2,000 이상으로 잡은 단일 요청으로 확보할 수 있다. 최초에는 전체 건수를 조회하여 이 값이 전체보다 작지 않게 하고 실제 응답/고유 ID 수를 대조한다. 직접 페이징을 기본 구현으로 추가하지 않는다. 추후 단일 요청이 전체를 반환하지 않는 계정이 확인되면 해당 제약을 검증한 뒤 페이징을 추가하며, 그전에는 불완전한 목록을 성공으로 처리하지 않는다.

`limit=2000`의 수락과 1,138건의 반환은 확인했지만, 2,000건이 넘는 계정의 단일 응답 크기나 Garmin 서버의 절대 최대값은 알 수 없다. 범용 SDK의 로컬 제한 1,000과 직접 API의 관측 결과를 구분해야 한다. 이번 검사는 활동 목록 조회까지이며 1,138개 FIT 파일 다운로드·가져오기 전체 실행을 뜻하지 않는다.

실제 제품의 수집 완료 판정은 별도 설계대로 CLI import 후의 SQLite 업로드 대상 행에 적용한다. 원격 전체 건수와 메모리의 원격 ID 집합을 확인한 뒤, 해당 ID가 SQLite에 모두 있는지 대조하고 최신 25개 처리 후 누락된 기록은 전체 수집으로 한 번 보충한다. SQLite의 여분 ID는 경고 후 기존 upsert·통계 갱신을 진행하며 원격 삭제를 전파하지 않는다. 원격 0건이어도 기존 기록이 남으면 경고하고 0건 통계로 표시하지 않는다. SQL·값 변환 오류는 기존 upsert 트랜잭션의 롤백과 실패 종료로 처리하고 gist 단계를 막는다. 필수 요약이 유효할 때 선택적인 FIT가 없는 활동은 경고로 처리한다. 이 알고리즘의 제품 구현이나 전체 import까지 위 API 목록 실측으로 통과했다고 보지 않는다.

## GUI/Python 한글 프로토콜 회귀 검증

첫 실제 계정 시도는 로그인과 `limit=25` 조회 뒤 GUI의 `JsonReaderException`으로 중단됐다. 이 결과로 전체 수집이나 서버 상한이 검증됐다고 보지 않는다.

자격증명 없이 같은 UTF-8 한글 JSON 메시지 3개를 Python 자식 프로세스가 보내고, GUI와 같은 C# 프로세스 시작·읽기 경로로 수신하도록 재현했다. Windows 기본 디코딩에서는 첫 두 메시지가 깨지고 세 번째 메시지에서 `JsonReaderException`이 발생했다. 수정 전 검사의 종료 코드는 1이었다.

C#의 `StandardInputEncoding`, `StandardOutputEncoding`, `StandardErrorEncoding`을 `new UTF8Encoding(false)`로 지정하고 Python 실행에 `-X utf8`을 추가한 뒤 같은 검사를 실행했다. 수신 인코딩은 65001이었고 세 메시지 모두 원문과 같았으며 JSON 해석도 통과했다. 수정 후 종료 코드는 0이었다. 이 검사는 Garmin에 로그인하거나 사용자 정보를 읽지 않는다.

## 격리 모드와 테스트 수 검증

같은 임시 폴더에 둔 두 Python 파일로 `-I -X utf8` 경계를 확인했다. 한 파일에서 다른 파일을 이름으로 import하면 `ModuleNotFoundError`로 종료했고, 같은 인터프리터가 다른 파일의 절대 경로를 하위 프로세스로 실행하면 종료 코드 0과 기대한 출력을 받았다. 이에 따라 제품 진입점의 업로드·gist 단계는 절대 경로의 하위 스크립트로 정했다. 검증 도구에서 테스트 모듈을 읽기 위해 저장소 경로를 명시적으로 추가하는 것과 구분한다.

번들 검증 도구의 테스트 수 제한은 임시 저장소 사본에서 확인했다. 기존 테스트 파일에 유효한 테스트 하나를 추가하면 5개가 모두 통과한 뒤에도 기존 `testsRun == 4` 때문에 도구가 종료 코드 1로 실패했다. 조건을 `testsRun > 0`으로 바꾼 뒤 같은 5개와 번들 검사는 종료 코드 0으로 통과했다. 빈 테스트 파일 두 개로 수집한 0개는 종료 코드 1로 실패하여 빈 수집을 성공 처리하지 않았다. 본 저장소의 기존 테스트 4개와 번들 검사도 다시 통과했다.

## Job 연결 전 부모 종료 재현

5차 리뷰의 `Process.Start` 유지 제안은 수용하되, Python 로그인에 걸리는 시간만으로 Start~Assign 사이의 안전을 보장하지 않는다. Windows 10 x64, .NET 10과 CPython 3.13.18의 임시 재현 프로그램에서 C#이 Python을 시작한 후 Job에 연결하기 전 상태로 대기하게 만들고, C#만 강제 종료했다. 외부 서비스나 실제 계정은 사용하지 않았다.

- 시작 확인이 없는 Python은 부모가 종료된 뒤에도 임시 파일에 작업 시작 표시를 쓰고 종료 코드 0으로 끝났다.
- stdin 첫 줄의 `START\n`을 기다리는 Python은 부모 종료 시 EOF를 받아 작업 시작 표시 없이 종료 코드 3으로 끝났다.
- 정상 경로에서는 C#의 시작 신호 다음 줄에 테스트 인증 코드를 보내고 한글 JSON 응답을 받았다. 입력 순서와 UTF-8 응답이 일치했고 종료 코드는 0이었다.

이 결과는 `Process`의 기존 입출력을 유지하면서 Job 연결 성공 후에만 작업을 허용하는 설계의 근거다. 이 검사는 Start~Assign 사이를 의도적으로 늘린 재현이며, 실제 Job 연결이나 Job에 속한 자식·손자 프로세스의 종료까지 검증한 것은 아니다.

## 재실행

준비한 ZIP을 풀고 아래처럼 실행한다. 검증 경로와 출력 경로는 실제 위치로 바꾼다. 한글 경로에서 CA 우회를 시험할 때만 `--ca-bundle`에 원본 certifi 인증서와 내용이 같은 ASCII 경로 파일을 지정한다.

```powershell
& 'C:\garmin-check\python\python.exe' -I -X utf8 .\tools\verify_windows_bundle.py --repo . --output .\bundle-result.json
& 'C:\garmin-check\python\python.exe' -I -X utf8 .\tools\verify_garmin_api.py --output .\garmin-api-result.json
```

API 검사는 대화형 터미널에서는 로그인 정보를 가림 입력한다. 현재 세션에서는 임시 WinForms 창에서 입력받아 자식 프로세스의 환경으로 전달한다. 로그인 정보와 토큰은 저장하지 않으며, 인증이 끝나지 않은 상태를 API 검증 성공으로 처리하지 않는다.

## 남은 확인

- 2,000건이 넘는 계정에서의 단일 응답 크기와 서버의 보편적 상한. 현재 계정으로 확인한 범위를 넘는 수치는 단정하지 않는다.
- 한글 사용자 프로필에서도 인증서 검증을 유지하는 배포 경로/런타임 해결책.
- Windows 11 및 Python/.NET 사전 설치가 없는 깨끗한 VM 검증.
- 실제 FIT 다운로드·가져오기 전체 경로, 원격 PostgreSQL 연결, Supabase upsert, gist 갱신, 실제 예약 실행. 이 문서의 합성 SQLite 검사나 모듈 import로 해당 항목까지 통과했다고 보지 않는다.
- `Process.Start` 직후의 Job 연결과 시작 신호, Job에 속한 자식·손자 프로세스 정리, EXE 강제 종료 후 재실행과 abandoned mutex 복구의 제품 구현·실행 검증. 위 재현은 Job 연결 전 EOF와 기존 입출력만 확인했으며 제품 전체 실행을 뜻하지 않는다.
