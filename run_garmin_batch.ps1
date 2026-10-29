# 설치된 garmin-box EXE로 수동 동기화한다 (Windows). 설치 전이면 안내만 한다.
$exe = Join-Path $env:LOCALAPPDATA 'garmin-box\GarminBox.exe'
if (-not (Test-Path $exe)) {
    Write-Host 'garmin-box가 설치되지 않았습니다. 릴리스의 GarminBox.exe를 먼저 실행해 설정하세요.'
    exit 1
}
$p = Start-Process -FilePath $exe -ArgumentList '--sync' -Wait -PassThru
exit $p.ExitCode
