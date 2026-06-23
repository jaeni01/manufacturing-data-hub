-- =============================================================
--  MfgInspectionSystem - MySQL 8.0+ reference schema
--  Column names match the EF Core [Column("...")] mappings.
--  'timestamp' is a MySQL reserved word — always backtick it.
-- =============================================================

CREATE DATABASE IF NOT EXISTS manufacturing
    CHARACTER SET utf8mb4
    COLLATE utf8mb4_unicode_ci;

USE manufacturing;

-- CREATE USER IF NOT EXISTS 'csharp_user'@'%' IDENTIFIED BY 'changeme';
-- GRANT ALL PRIVILEGES ON manufacturing.* TO 'csharp_user'@'%';
-- FLUSH PRIVILEGES;

CREATE TABLE IF NOT EXISTS inspection_results (
    id                   BIGINT       NOT NULL AUTO_INCREMENT,
    `timestamp`          DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    product_id           VARCHAR(64),
    correlation_id       VARCHAR(36),
    product_type         VARCHAR(64),
    yolo_class           VARCHAR(64),
    yolo_confidence      DOUBLE,
    yolo_all_detections  LONGTEXT,
    opencv_pin_count     INT,
    opencv_blur_score    DOUBLE,
    opencv_roi_centered  TINYINT(1),
    result               VARCHAR(16),
    defect_detail        VARCHAR(255),
    cam1_image_path      VARCHAR(512),
    model_version        VARCHAR(64),
    inference_time_ms    INT,
    environment_temp     DOUBLE,
    environment_humidity DOUBLE,
    PRIMARY KEY (id),
    INDEX idx_ir_product   (product_id),
    INDEX idx_ir_corr      (correlation_id),
    INDEX idx_ir_inspected (`timestamp`),
    INDEX idx_ir_result    (result)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS sorting_results (
    id                  BIGINT      NOT NULL AUTO_INCREMENT,
    product_id          VARCHAR(64),
    verdict             VARCHAR(16),
    sorted_at           DATETIME(3),
    verified            TINYINT(1)  NOT NULL DEFAULT 0,
    verification_sensor VARCHAR(8),
    expected_sensor     VARCHAR(8),
    PRIMARY KEY (id),
    INDEX idx_sr_product (product_id),
    INDEX idx_sr_sorted  (sorted_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS sensor_readings (
    id          BIGINT      NOT NULL AUTO_INCREMENT,
    `timestamp` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    source      VARCHAR(64),
    quality     VARCHAR(16),
    seq         BIGINT      NOT NULL DEFAULT 0,
    mqtt_topic  VARCHAR(255),
    transport   VARCHAR(16),
    temperature DOUBLE,
    humidity    DOUBLE,
    gas_value   INT,
    gas_status  VARCHAR(32),
    PRIMARY KEY (id),
    INDEX idx_sn_ts     (`timestamp`),
    INDEX idx_sn_source (source)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS event_log (
    id             BIGINT      NOT NULL AUTO_INCREMENT,
    `timestamp`    DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    event_type     VARCHAR(64),
    severity       VARCHAR(16),
    source         VARCHAR(64),
    message        TEXT,
    actor          VARCHAR(64),
    details        TEXT,
    reason         TEXT,
    correlation_id VARCHAR(36),
    prev_hash      CHAR(64),
    record_hash    CHAR(64),
    PRIMARY KEY (id),
    INDEX idx_el_ts       (`timestamp`),
    INDEX idx_el_severity (severity),
    INDEX idx_el_type     (event_type),
    INDEX idx_el_corr     (correlation_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS alarm_log (
    id           BIGINT      NOT NULL AUTO_INCREMENT,
    `timestamp`  DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    alarm_type   VARCHAR(64),
    severity     VARCHAR(16),
    message      TEXT,
    acknowledged TINYINT(1)  NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    INDEX idx_al_ts       (`timestamp`),
    INDEX idx_al_severity (severity),
    INDEX idx_al_type     (alarm_type)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
