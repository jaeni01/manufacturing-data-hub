@echo off
REM ── 개발 모드: 1883 plain, Debug 로그, InfluxDB 비활성 ──
set MFG_ENV=dev
start "" "%~dp0bin\Release\net8.0-windows\MfgInspectionSystem.exe"
