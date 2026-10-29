# 릴리스 빌드: 런타임 → payload ZIP·해시 → 다른 경로에서 번들 검증 → 전체 테스트 → self-contained 단일 EXE
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out'
& (Join-Path $PSScriptRoot 'build-runtime.ps1')

$runtime = Join-Path $out 'runtime'
$app = New-Item -ItemType Directory -Force (Join-Path $runtime 'app')
Copy-Item (Join-Path $root 'run_batch.py'), (Join-Path $root 'sync_supa.py'), (Join-Path $root 'update_gist.py'), (Join-Path $root 'schema.sql') $app
$payload = New-Item -ItemType Directory -Force (Join-Path $root 'src\GarminBox\payload')
$zip = Join-Path $payload 'payload.zip'
Remove-Item $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($runtime, $zip)
(Get-FileHash $zip -Algorithm SHA256).Hash.ToLower() | Set-Content -NoNewline -Encoding ascii (Join-Path $payload 'payload.sha256')

# 빌드 경로와 다른 곳에 풀어 경로 고정·네이티브 DLL 누락을 확인한다
$check = Join-Path $out 'relocated-check'
if (Test-Path $check) { Remove-Item -Recurse -Force $check }
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $check)
$py = Join-Path $check 'python\python.exe'
& $py -I -X utf8 (Join-Path $root 'tools\verify_windows_bundle.py') --repo $root --output (Join-Path $out 'bundle-result.json')
if ($LASTEXITCODE) { throw 'bundle verification failed' }

Push-Location $root
try {
    & $py -I -X utf8 -m unittest discover -s tests -t . -v
    if ($LASTEXITCODE) { throw 'python tests failed' }
    dotnet test GarminBox.slnx -c Release
    if ($LASTEXITCODE) { throw 'dotnet tests failed' }
} finally { Pop-Location }

dotnet publish (Join-Path $root 'src\GarminBox\GarminBox.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -o (Join-Path $out 'publish')
if ($LASTEXITCODE) { throw 'publish failed' }
Get-FileHash (Join-Path $out 'publish\GarminBox.exe') -Algorithm SHA256
