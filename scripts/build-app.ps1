# Публикация приложения GoreBox (single-file, win-x64).
#
# Требуется .NET 8 SDK. NuGet-пакеты (Grpc.Net.Client, Google.Protobuf, Grpc.Tools)
# качаются при restore — сборка без сети не пройдёт.
#
# Использование (из корня репозитория):
#   powershell -ExecutionPolicy Bypass -File scripts\build-app.ps1
# Результат: dist\win-x64\app\GoreBox.exe

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\GoreBox\GoreBox.csproj"
$out = Join-Path $root "dist\win-x64\app"

Write-Host "Публикация GoreBox → $out"
dotnet publish $proj -c Release -r win-x64 -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

Write-Host "Готово: $out\GoreBox.exe"
Write-Host "Ядро: положите sing-box.exe рядом с GoreBox.exe или в %LocalAppData%\GoreBox\core\ (scripts\build-core.ps1 -Install)"
