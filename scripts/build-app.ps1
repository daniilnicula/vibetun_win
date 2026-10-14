# Публикация приложения VibeTun (single-file, win-x64).
#
# Требуется .NET 8 SDK. NuGet-пакеты (Grpc.Net.Client, Google.Protobuf, Grpc.Tools)
# качаются при restore — сборка без сети не пройдёт.
#
# Использование (из корня репозитория):
#   powershell -ExecutionPolicy Bypass -File scripts\build-app.ps1
# Результат: dist\win-x64\app\VibeTun.exe

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\VibeTun\VibeTun.csproj"
$out = Join-Path $root "dist\win-x64\app"

Write-Host "Публикация VibeTun → $out"
dotnet publish $proj -c Release -r win-x64 -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

Write-Host "Готово: $out\VibeTun.exe"
Write-Host "Ядро: положите sing-box.exe рядом с VibeTun.exe или в %LocalAppData%\VibeTun\core\ (scripts\build-core.ps1 -Install)"
