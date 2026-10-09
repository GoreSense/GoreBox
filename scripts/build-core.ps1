# Сборка ядра GoreBox (nekobox_core) → dist\win-x64\sing-box.exe
#
# Зеркало libs/build_go.sh из MatsuriDayo/nekoray: CGO off, теги сборки nekobox,
# версия прокидывается в banner (libneko/neko_common.Version_neko).
# Требуется Go 1.22+. Исходники vendored в core/ (см. core/README.md).
#
# Использование (из корня репозитория):
#   powershell -ExecutionPolicy Bypass -File scripts\build-core.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\build-core.ps1 -Install
# -Install дополнительно кладёт бинарник в %LocalAppData%\GoreBox\core\sing-box.exe,
# откуда приложение подхватывает его при первом запуске.

param(
    [string]$Version = "GoreBox-1.2.0",
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "core\nekoray\go\cmd\nekobox_core"
$dest = Join-Path $root "dist\win-x64"
$out = Join-Path $dest "sing-box.exe"

New-Item -ItemType Directory -Force -Path $dest | Out-Null

Write-Host "Сборка nekobox_core ($Version) из $src"
Push-Location $src
try {
    $env:CGO_ENABLED = "0"
    go build -v -o $out -trimpath `
        -ldflags "-w -s -X github.com/matsuridayo/libneko/neko_common.Version_neko=$Version" `
        -tags "with_clash_api,with_gvisor,with_quic,with_wireguard,with_utls,with_ech"
    if ($LASTEXITCODE -ne 0) { throw "go build завершился с кодом $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Write-Host "Готово: $out"

if ($Install) {
    $coreDir = Join-Path $env:LOCALAPPDATA "GoreBox\core"
    New-Item -ItemType Directory -Force -Path $coreDir | Out-Null
    Copy-Item -Force $out (Join-Path $coreDir "sing-box.exe")
    Write-Host "Установлено: $coreDir\sing-box.exe"
}
