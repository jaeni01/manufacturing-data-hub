USE manufacturing;

CREATE TABLE IF NOT EXISTS `inspection_results` (
    `id`                  bigint NOT NULL AUTO_INCREMENT,
    `timestamp`           datetime(6) NOT NULL,
    `product_id`          longtext CHARACTER SET utf8mb4 NULL,
    `correlation_id`      longtext CHARACTER SET utf8mb4 NULL,
    `product_type`        longtext CHARACTER SET utf8mb4 NULL,
    `yolo_class`          longtext CHARACTER SET utf8mb4 NULL,
    `yolo_confidence`     double NULL,
    `yolo_all_detections` longtext CHARACTER SET utf8mb4 NULL,
    `opencv_pin_count`    int NULL,
    `opencv_blur_score`   double NULL,
    `opencv_roi_centered` tinyint(1) NULL,
    `result`              longtext CHARACTER SET utf8mb4 NULL,
    `defect_detail`       longtext CHARACTER SET utf8mb4 NULL,
    `cam1_image_path`     longtext CHARACTER SET utf8mb4 NULL,
    `model_version`       longtext CHARACTER SET utf8mb4 NULL,
    `inference_time_ms`   int NULL,
    `environment_temp`    double NULL,
    `environment_humidity` double NULL,
    PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `sorting_results` (
    `id`                  bigint NOT NULL AUTO_INCREMENT,
    `product_id`          longtext CHARACTER SET utf8mb4 NULL,
    `verdict`             longtext CHARACTER SET utf8mb4 NULL,
    `sorted_at`           datetime(6) NULL,
    `verified`            tinyint(1) NOT NULL DEFAULT 0,
    `verification_sensor` longtext CHARACTER SET utf8mb4 NULL,
    `expected_sensor`     longtext CHARACTER SET utf8mb4 NULL,
    PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `sensor_readings` (
    `id`          bigint NOT NULL AUTO_INCREMENT,
    `timestamp`   datetime(6) NOT NULL,
    `source`      longtext CHARACTER SET utf8mb4 NULL,
    `quality`     longtext CHARACTER SET utf8mb4 NULL,
    `seq`         bigint NOT NULL DEFAULT 0,
    `mqtt_topic`  longtext CHARACTER SET utf8mb4 NULL,
    `transport`   longtext CHARACTER SET utf8mb4 NULL,
    `temperature` double NULL,
    `humidity`    double NULL,
    `gas_value`   int NULL,
    `gas_status`  longtext CHARACTER SET utf8mb4 NULL,
    PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `event_log` (
    `id`           bigint NOT NULL AUTO_INCREMENT,
    `timestamp`    datetime(6) NOT NULL,
    `event_type`   longtext CHARACTER SET utf8mb4 NULL,
    `severity`     longtext CHARACTER SET utf8mb4 NULL,
    `source`       longtext CHARACTER SET utf8mb4 NULL,
    `message`      longtext CHARACTER SET utf8mb4 NULL,
    `actor`        longtext CHARACTER SET utf8mb4 NULL,
    `reason`       longtext CHARACTER SET utf8mb4 NULL,
    `correlation_id` longtext CHARACTER SET utf8mb4 NULL,
    `prev_hash`    longtext CHARACTER SET utf8mb4 NULL,
    `record_hash`  longtext CHARACTER SET utf8mb4 NULL,
    `details`      longtext CHARACTER SET utf8mb4 NULL,
    PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `alarm_log` (
    `id`           bigint NOT NULL AUTO_INCREMENT,
    `timestamp`    datetime(6) NOT NULL,
    `alarm_type`   longtext CHARACTER SET utf8mb4 NULL,
    `severity`     longtext CHARACTER SET utf8mb4 NULL,
    `message`      longtext CHARACTER SET utf8mb4 NULL,
    `acknowledged` tinyint(1) NOT NULL DEFAULT 0,
    PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

SELECT 'Tables created successfully.' AS status;
