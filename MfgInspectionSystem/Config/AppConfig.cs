using Microsoft.Extensions.Configuration;

namespace MfgInspectionSystem.Config;

public class AppConfig
{
    public string Environment { get; set; } = "dev";
    public SerialConfig Serial { get; set; } = new();
    public MqttConfig Mqtt { get; set; } = new();
    public YoloConfig Yolo { get; set; } = new();
    public DatabaseConfig Database { get; set; } = new();
    public VisionConfig Vision { get; set; } = new();
    public SafetyConfig Safety { get; set; } = new();
    public SortingConfig Sorting { get; set; } = new();
    public InfluxDbConfig InfluxDb { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public MetricsConfig Metrics { get; set; } = new();

    public static AppConfig Load()
    {
        var env = System.Environment.GetEnvironmentVariable("MFG_ENV") ?? "dev";

        var cfg = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.secret.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "MFG_")
            .Build();

        var result = new AppConfig();
        cfg.Bind(result);
        result.Environment = env;
        return result;
    }

    /// <summary>
    /// MySqlConnector 형식 키 (Pomelo EF 가 내부적으로 MySqlConnector 사용).
    /// 풀링 없이 매 flush 마다 새 TCP 연결을 만들면 IsConnected 가 순간 false 가 되는
    /// "깜빡임" 현상이 발생한다. MinimumPoolSize=2 로 항상 연결 2개를 예열.
    /// AllowPublicKeyRetrieval=true — MySQL 8.x caching_sha2_password 인증 시
    /// public key 교환을 허용; 없으면 TLS 없는 환경에서 인증 거부될 수 있음.
    /// </summary>
    public string BuildConnectionString() =>
        $"server={Database.Host};port={Database.Port};" +
        $"database={Database.DatabaseName};uid={Database.User};" +
        $"pwd={Database.Password};charset=utf8mb4;SslMode=None;" +
        "Pooling=true;" +
        "MinimumPoolSize=2;" +
        "MaximumPoolSize=20;" +
        "ConnectionIdleTimeout=180;" +   // idle 연결 3분 후 반납 (pool stale 방지)
        "ConnectionLifeTime=600;" +       // 연결 최대 10분 — MySQL wait_timeout 전에 강제 교체
        "DefaultCommandTimeout=10;" +     // 쿼리 타임아웃 10s
        "ConnectionTimeout=5;" +          // 연결 시도 타임아웃 5s
        "AllowPublicKeyRetrieval=true;";
}

public class SerialConfig
{
    public string PortName { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public int AckTimeoutMs { get; set; } = 1000;
    public int MaxRetries { get; set; } = 3;
    public int WatchdogTimeoutMs { get; set; } = 5000;
}

public class MqttConfig
{
    public string BrokerHost { get; set; } = "127.0.0.1";
    public int BrokerPort { get; set; } = 1883;
    public string ClientId { get; set; } = "pc-line1-main";
    public string Username { get; set; } = "mfg_pc";
    public string Password { get; set; } = "";
    public bool TlsEnabled { get; set; } = false;
    public int ReconnectDelayMs { get; set; } = 3000;
    public MqttTopicsConfig Topics { get; set; } = new();
    public string? CaCertPath { get; set; }
    public string? ClientCertPath { get; set; }
    public string? ClientKeyPath { get; set; }
    public string? ClientCertPfxPath { get; set; }
    public string? ClientCertPfxPassword { get; set; }
    public bool AllowUntrustedCertificates { get; set; } = false;
}

public class MqttTopicsConfig
{
    public string EnvSubscribe { get; set; } = "mfg/line1/env/#";
    public string StatusRpiSubscribe { get; set; } = "mfg/line1/status/rpi/#";
    public string AlarmPublishBase { get; set; } = "mfg/line1/alarm";
    public string StatusPcPublishBase { get; set; } = "mfg/line1/status/pc";
    public string InspectionResultPublish { get; set; } = "mfg/line1/inspection/result";
    public string SortingResultPublish { get; set; } = "mfg/line1/sorting/result";
}

public class YoloConfig
{
    public string ServiceUrl { get; set; } = "http://localhost:5002";
    public int TimeoutMs { get; set; } = 3000;
    public double ConfidenceThreshold { get; set; } = 0.75;
    public double HoldThreshold { get; set; } = 0.70;
}

public class DatabaseConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string DatabaseName { get; set; } = "manufacturing";
    public string User { get; set; } = "mfg_control";
    public string Password { get; set; } = "";
}

public class VisionConfig
{
    public string Cam1Url { get; set; } = "http://192.168.1.50:8080/?action=snapshot";
    public string Cam2Url { get; set; } = "http://192.168.1.50:8081/?action=snapshot";
    public string Cam1StreamUrl { get; set; } = "http://192.168.1.50:8080/stream";
    public string Cam2StreamUrl { get; set; } = "http://192.168.1.50:8081/stream";
    public int PinCountThreshold { get; set; } = 3;

    // v1 single threshold — kept as fallback when v2 keys are absent
    public double BlurLaplacianThreshold { get; set; } = 100.0;

    // v2 (D spec): two-stage blur thresholds
    public double BlurThresholdHold { get; set; } = 80.0;   // below this → HOLD (severe blur)
    public double BlurThresholdPass { get; set; } = 100.0;  // below this → HOLD (borderline blur)

    // true when v2 keys are explicitly configured (both positive and Hold <= Pass)
    public bool HasV2BlurThresholds =>
        BlurThresholdHold > 0 && BlurThresholdPass >= BlurThresholdHold;

    public int CameraTriggerDelayMs { get; set; } = 80;

    /// <summary>이미지 저장 루트 — Flask의 static 폴더 절대경로. 상대경로면 앱 실행 디렉터리 기준.</summary>
    public string ImageBasePath { get; set; } = "inspection_images";

    /// <summary>
    /// YOLO 추론 전 CLAHE 전처리 활성화.
    /// 어둡거나 노이즈 심한 이미지에서 bbox/신뢰도 개선 목적.
    /// 효과 없으면 appsettings.json에서 false로 변경 (재빌드 불필요).
    /// </summary>
    public bool EnableYoloPreprocessing { get; set; } = false;
}

public class SafetyConfig
{
    public double TempWarning { get; set; } = 35.0;
    public double TempCritical { get; set; } = 40.0;
    public double HumidityWarning { get; set; } = 70.0;
    public double HumidityCritical { get; set; } = 80.0;
    public int GasWarning { get; set; } = 300;
    public int GasCritical { get; set; } = 500;
    public int BrokerDisconnectTimeoutMs { get; set; } = 5000;
    public int ArduinoHeartbeatTimeoutMs { get; set; } = 5000;
    public int RpiHeartbeatTimeoutMs { get; set; } = 5000;
}

public class SortingConfig
{
    public int ServoReturnDelayMs { get; set; } = 500;
    public int VerificationTimeoutMs { get; set; } = 3000;
    public string PassSensor { get; set; } = "";   // PASS는 검증 센서 없음 → 즉시 verified=true
    public string DefectSensor { get; set; } = "S2"; // S2: DEFECT 박스 확인 센서
    public string HoldSensor { get; set; } = "S3";   // S3: HOLD 박스 확인 센서
    /// <summary>Servo command descriptor for PASS verdict (informational; "none" = no action).</summary>
    public string PassServoCmd { get; set; } = "none";
    /// <summary>Servo command descriptor for DEFECT verdict.</summary>
    public string DefectServoCmd { get; set; } = "servoA:90";
    /// <summary>Servo command descriptor for HOLD verdict.</summary>
    public string HoldServoCmd { get; set; } = "servoB:90";
    /// <summary>
    /// 서보 발사 후 컨베이어 진동으로 인한 S1 오감지 억제 시간(ms).
    /// OnS3TriggeredAsync 반환 직후 S1 lockout에 적용됨.
    /// </summary>
    public int SensorLockoutAfterServoMs { get; set; } = 3500;
}

public class InfluxDbConfig
{
    public bool Enabled { get; set; } = false;
    public string Url { get; set; } = "http://localhost:8086";
    /// <summary>InfluxDB v2 API token — store in appsettings.secret.json, never in appsettings.json.</summary>
    public string Token { get; set; } = "";
    public string Org { get; set; } = "mfg";
    public string Bucket { get; set; } = "line1-telemetry";
}

public class LoggingConfig
{
    public string MinimumLevel { get; set; } = "Information";
    public string FilePath { get; set; } = "logs/mfg-.log";
}

public class MetricsConfig
{
    public bool Enabled { get; set; } = true;
    public int PrometheusPort { get; set; } = 9091;
}
