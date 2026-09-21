# garmindb 다운로드 -> SUPA 동기화 -> gist 갱신 (Windows). 앞 단계 실패 시 즉시 중단.
# 시크릿은 저장소가 아니라 $HOME\.garmin-box.env 에서 읽는다 (공개 저장소이므로 커밋 금지).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# 실행 전체를 batch.log로 캡처 → 스케줄러에서 조용히 실패해도(예: gist 403) 흔적이 남는다.
Start-Transcript -Path (Join-Path $PSScriptRoot 'batch.log') -Append | Out-Null
try {

# --- 시크릿 로드: KEY=VALUE, 첫 '=' 만 구분자 (SUPA_DSN 값 안의 '=' 보존) ---
$envFile = Join-Path $HOME '.garmin-box.env'
if (-not (Test-Path $envFile)) { throw "env file not found: $envFile" }
foreach ($line in Get-Content $envFile) {
    $t = $line.Trim()
    if (-not $t -or $t.StartsWith('#')) { continue }
    $i = $t.IndexOf('=')
    if ($i -lt 1) { continue }
    Set-Item -Path ("Env:" + $t.Substring(0, $i).Trim()) -Value $t.Substring($i + 1)
}

$py = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
$cli = Join-Path $PSScriptRoot '.venv\Scripts\garmindb_cli.py'

# garmindb_cli 는 로그인 실패 시에도 exit 0 → 종료코드 대신 출력으로 판정한다.
# 캐시 토큰(~/.GarminDb/garmin_tokens.json)의 refresh token 이 만료(약 1달)되면 garminconnect 가
# 자격증명 폴백을 타지 않고 계속 실패하므로, 토큰 파일을 지우고 한 번만 재시도한다.
function Invoke-GarminDb {
    $ErrorActionPreference = 'Continue'  # PS 5.1: Stop 상태에서 2>&1 은 stderr 줄을 예외로 승격시킨다
    & $py $cli --activities --latest --download --import --analyze 2>&1 | Tee-Object -Variable out | Out-Host
    return ($LASTEXITCODE -eq 0 -and -not ($out -match 'Failed to login'))
}
if (-not (Invoke-GarminDb)) {
    Remove-Item (Join-Path $HOME '.GarminDb\garmin_tokens.json') -ErrorAction SilentlyContinue
    Write-Host 'garmindb login failed; removed stale token, retrying once'
    if (-not (Invoke-GarminDb)) { throw 'garmindb failed' }
}

& $py (Join-Path $PSScriptRoot 'sync_supa.py')
if ($LASTEXITCODE -ne 0) { throw "sync_supa failed ($LASTEXITCODE)" }

& $py (Join-Path $PSScriptRoot 'update_gist.py')
if ($LASTEXITCODE -ne 0) { throw "update_gist failed ($LASTEXITCODE)" }

Write-Host "done: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
}
finally {
    Stop-Transcript | Out-Null
}
