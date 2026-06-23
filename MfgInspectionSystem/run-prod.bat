@echo off
REM ── 운영 모드: 8883 TLS, Warning 로그, InfluxDB 활성 ──
REM 사전 조건:
REM   1. bin\Release\net8.0-windows\certs\ 에 ca.crt, pc-client.crt, pc-client.key 배치
REM   2. appsettings.secret.json 의 InfluxDb.Token 에 실제 토큰 기입
set MFG_ENV=prod
start "" "%~dp0bin\Release\net8.0-windows\MfgInspectionSystem.exe"
