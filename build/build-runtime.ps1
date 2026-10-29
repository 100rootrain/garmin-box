# 배포용 Python 런타임을 만든다: 고정 배포본 내려받기 → SHA-256 확인 → 잠금 목록 설치. 출력: build\out\runtime\python
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'out'
$name = 'cpython-3.13.18+20261026-x86_64-pc-windows-msvc-install_only_stripped.tar.gz'
$url = "https://github.com/astral-sh/python-build-standalone/releases/download/20261026/$($name.Replace('+', '%2B'))"
$sha = '7369f7a35f261222a6ac8b3a87c1c88eeebee8af945d85346202591a64038dd3'

New-Item -ItemType Directory -Force $out | Out-Null
$archive = Join-Path $out $name
if (-not (Test-Path $archive)) { Invoke-WebRequest $url -OutFile $archive }
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $sha) { throw "runtime SHA-256 mismatch: $archive" }

$runtime = Join-Path $out 'runtime'
if (Test-Path $runtime) { Remove-Item -Recurse -Force $runtime }
New-Item -ItemType Directory $runtime | Out-Null
tar -xzf $archive -C $runtime
$py = Join-Path $runtime 'python\python.exe'
# shortcut: 버전만 고정하고 wheel 해시는 고정하지 않음. 공급망 요구가 생기면 --require-hashes 잠금으로 바꾼다.
& $py -I -m pip install --no-deps --no-warn-script-location -r (Join-Path $PSScriptRoot 'windows-requirements.lock')
if ($LASTEXITCODE) { throw 'pip install failed' }
& $py -I -m pip --isolated check
if ($LASTEXITCODE) { throw 'pip check failed' }
