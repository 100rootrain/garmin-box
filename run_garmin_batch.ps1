# garmindb 다운로드 -> SUPA 동기화 -> gist 갱신 (Windows). 앞 단계 실패 시 즉시 중단.
# 시크릿은 저장소가 아니라 $HOME\.garmin-box.env 에서 읽는다 (공개 저장소이므로 커밋 금지).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

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

& $py $cli --activities --latest --download --import --analyze
if ($LASTEXITCODE -ne 0) { throw "garmindb failed ($LASTEXITCODE)" }

& $py (Join-Path $PSScriptRoot 'sync_supa.py')
if ($LASTEXITCODE -ne 0) { throw "sync_supa failed ($LASTEXITCODE)" }

& $py (Join-Path $PSScriptRoot 'update_gist.py')
if ($LASTEXITCODE -ne 0) { throw "update_gist failed ($LASTEXITCODE)" }

Write-Host "done: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
