-- ============================================================
-- MfgInspectionSystem — DB 스키마 재생성 스크립트
-- 실행 후 C# 앱을 재시작하면 EnsureCreated()가 현재 엔티티
-- 기준으로 테이블을 자동 재생성합니다.
--
-- 대상 DB: manufacturing
-- 주의: 기존 데이터는 모두 삭제됩니다 (테스트 데이터 한정 실행)
-- ============================================================

USE manufacturing;

SET FOREIGN_KEY_CHECKS = 0;

DROP TABLE IF EXISTS inspection_results;
DROP TABLE IF EXISTS sorting_results;
DROP TABLE IF EXISTS sensor_readings;
DROP TABLE IF EXISTS event_log;
DROP TABLE IF EXISTS alarm_log;

SET FOREIGN_KEY_CHECKS = 1;

-- 실행 완료 확인
SELECT 'Schema drop complete. Restart the C# app to recreate tables.' AS status;
