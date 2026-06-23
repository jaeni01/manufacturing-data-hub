using MfgInspectionSystem.Communication;
using MfgInspectionSystem.Communication.Messages;
using MfgInspectionSystem.Config;
using MfgInspectionSystem.Data;
using Serilog;

namespace MfgInspectionSystem.Core;

public class SafetyMonitor : IDisposable
{
    private readonly StateMachine _stateMachine;
    private readonly SerialManager _serial;
    private readonly MqttSubscriber _mqtt;
    private readonly DbWriter _db;
    private readonly SafetyConfig _cfg;

    private bool _emergencyActive;
    private bool _gasWarningActive;
    private bool _tempWarningActive;
    private bool _humidityWarningActive;
    private DateTime _alarmGraceUntil;   // 시작 직후 retained MQTT 메시지 오탐 억제용 (5초)
    private DateTime _lastArduinoHeartbeat = DateTime.UtcNow;
    private DateTime _lastRpiHeartbeat = DateTime.UtcNow;
    private bool _wasOperational;        // IDLE→RUNNING 전환 감지용 (stale 타이머 리셋)
    private System.Threading.Timer? _watchdogTimer;
    private bool _disposed;

    public event Action<string, string>? OnAlarm;         // (level, message)
    public event Action<string, string>? OnAlarmCleared;  // (metric, message)
    public event Action? OnEmergencyTriggered;

    public SafetyMonitor(StateMachine stateMachine, SerialManager serial,
        MqttSubscriber mqtt, DbWriter db, SafetyConfig cfg)
    {
        _stateMachine = stateMachine;
        _serial = serial;
        _mqtt = mqtt;
        _db = db;
        _cfg = cfg;
    }

    public void Start()
    {
        // MQTT broker retained 메시지는 구독 직후 즉시 수신됨 →
        // 실제 센서 없이도 이전 세션의 높은 값이 알람을 발생시키는 오탐 방지.
        // 5초 유예 동안 WARNING 알람은 억제, CRITICAL(TriggerEmergency)은 그대로 통과.
        _alarmGraceUntil = DateTime.UtcNow.AddSeconds(5);

        _serial.OnEstopTriggered += OnEstop;
        _serial.OnDisconnected += OnSerialDisconnected;
        _serial.OnHeartbeatReceived += OnArduinoHeartbeat;
        _mqtt.OnRpiOnlineChanged += OnRpiOnlineChanged;
        _mqtt.OnBrokerConnectionChanged += OnBrokerConnectionChanged;
        _mqtt.OnSensorData += OnSensorData;
        _mqtt.OnRpiHeartbeat += OnRpiHeartbeatReceived;

        _watchdogTimer = new System.Threading.Timer(WatchdogTick, null, 1000, 1000);
        Log.Information("SafetyMonitor started");
    }

    private void OnEstop(EstopEvent evt)
    {
        // RUNNING 중 hw_estop = Arduino가 S1 감지 후 컨베이어를 정지시킨 신호.
        // 이 경우 EMERGENCY가 아니라 검사 트리거로 처리 (MainForm.WireSerialEvents에서 수신).
        // RUNNING이 아닐 때(IDLE·PAUSED·EMERGENCY)는 진짜 비상 신호이므로 EMERGENCY 전환.
        if (_stateMachine.IsOperational) return;
        _ = TriggerEmergency("hw_estop", $"E-STOP: {evt.Reason}");
    }

    private void OnSerialDisconnected() =>
        _ = TriggerEmergency("arduino_disconnected", "Arduino Serial 연결 끊김");

    private void OnArduinoHeartbeat(HeartbeatEvent evt) =>
        _lastArduinoHeartbeat = DateTime.UtcNow;

    private void OnRpiHeartbeatReceived() =>
        _lastRpiHeartbeat = DateTime.UtcNow;

    private void OnRpiOnlineChanged(bool online)
    {
        if (!online)
            _ = TriggerEmergency("rpi_offline", "RPi offline (LWT)");
    }

    private void OnBrokerConnectionChanged(bool connected)
    {
        if (!connected)
            _ = TriggerEmergency("mqtt_broker_disconnected", "MQTT broker 연결 끊김");
    }

    private void OnSensorData(SensorData data)
    {
        _lastRpiHeartbeat = DateTime.UtcNow;

        // 시작 직후 5초간 retained 메시지 오탐 억제 (CRITICAL은 유예 없이 통과)
        bool inGrace = DateTime.UtcNow < _alarmGraceUntil;

        switch (data.Metric)
        {
            case "temperature":
                if (data.Value >= _cfg.TempCritical)
                    _ = TriggerEmergency("temp_critical", $"온도 임계 초과: {data.Value:F1}°C");
                else if (data.Value >= _cfg.TempWarning)
                {
                    // 상승 엣지에서만 알람 발생 — 같은 상태가 유지되는 동안 반복하지 않음
                    if (!inGrace && !_tempWarningActive)
                    {
                        _tempWarningActive = true;
                        RaiseAlarm("WARNING", $"온도 경고: {data.Value:F1}°C");
                    }
                }
                else if (_tempWarningActive)
                {
                    _tempWarningActive = false;
                    RaiseAlarmCleared("temperature", $"온도 정상 복귀: {data.Value:F1}°C");
                }
                break;

            case "humidity":
                if (data.Value >= _cfg.HumidityCritical)
                    _ = TriggerEmergency("humidity_critical", $"습도 임계 초과: {data.Value:F1}%");
                else if (data.Value >= _cfg.HumidityWarning)
                {
                    if (!inGrace && !_humidityWarningActive)
                    {
                        _humidityWarningActive = true;
                        RaiseAlarm("WARNING", $"습도 경고: {data.Value:F1}%");
                    }
                }
                else if (_humidityWarningActive)
                {
                    _humidityWarningActive = false;
                    RaiseAlarmCleared("humidity", $"습도 정상 복귀: {data.Value:F1}%");
                }
                break;

            case "gas":
                if (data.Value >= _cfg.GasCritical)
                    _ = TriggerEmergency("gas_critical", $"가스 임계 초과: {data.Value:F0} ppm");
                else if (data.Value >= _cfg.GasWarning)
                {
                    if (!inGrace && !_gasWarningActive)
                    {
                        _gasWarningActive = true;
                        RaiseAlarm("WARNING", $"가스 경고: {data.Value:F0} ppm");
                    }
                }
                else if (_gasWarningActive)
                {
                    _gasWarningActive = false;
                    RaiseAlarmCleared("gas", $"가스 정상 복귀: {data.Value:F0} ppm");
                }
                break;
        }
    }

    private void WatchdogTick(object? state)
    {
        bool isOp = _stateMachine.IsOperational;

        if (!isOp)
        {_wasOperational = false; return;}

        // IDLE/PAUSED → RUNNING 첫 틱: IDLE 동안 쌓인 stale 타이머를 리셋.
        // 리셋 없이 검사하면 "IDLE 중에 흐른 시간"이 타임아웃으로 잡혀 즉시 EMERGENCY.
        if (!_wasOperational)
        {
            _wasOperational = true;
            _lastArduinoHeartbeat = DateTime.UtcNow;
            _lastRpiHeartbeat     = DateTime.UtcNow;
            return;
        }

        var now = DateTime.UtcNow;

        double arduinoElapsed = (now - _lastArduinoHeartbeat).TotalMilliseconds;
        if (arduinoElapsed > _cfg.ArduinoHeartbeatTimeoutMs)
            _ = TriggerEmergency("arduino_timeout",
                $"Arduino heartbeat timeout ({arduinoElapsed:F0}ms)");

        double rpiElapsed = (now - _lastRpiHeartbeat).TotalMilliseconds;
        if (_lastRpiHeartbeat > DateTime.MinValue && rpiElapsed > _cfg.RpiHeartbeatTimeoutMs)
            _ = TriggerEmergency("rpi_timeout",
                $"RPi heartbeat timeout ({rpiElapsed:F0}ms)");
    }

    private async Task TriggerEmergency(string type, string reason)
    {
        if (_emergencyActive) return;
        _emergencyActive = true;

        Log.Error("EMERGENCY: {Type} — {Reason}", type, reason);

        _stateMachine.TransitionTo(SystemState.EMERGENCY, reason);

        try
        {
            await _serial.SetMotor(0);
            await _serial.TriggerEstop();
            await _serial.SetLed("red", true);
            await _serial.SetLed("green", false);
            await _serial.SetLed("yellow", false);
            await _serial.SetBuzzer(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "EMERGENCY serial commands failed");
        }

        try
        {
            await _mqtt.PublishAlarmAsync(type, "emergency", new { reason, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }
        catch { }

        _db.WriteEventLog("emergency_triggered", "CRITICAL", "safety_monitor", $"{type}: {reason}");
        _db.WriteAlarmLog(type, "CRITICAL", reason);

        RaiseAlarm("CRITICAL", $"[{type}] {reason}");
        OnEmergencyTriggered?.Invoke();
    }

    private void RaiseAlarm(string level, string message)
    {
        Log.Warning("Alarm [{Level}]: {Msg}", level, message);
        OnAlarm?.Invoke(level, message);
    }

    private void RaiseAlarmCleared(string metric, string message)
    {
        Log.Information("Alarm cleared [{Metric}]: {Msg}", metric, message);
        OnAlarmCleared?.Invoke(metric, message);
    }

    /// <summary>
    /// 시연용 센서 데이터 직접 주입.
    /// MQTT 없이도 SafetyMonitor의 알람 로직을 트리거할 수 있다.
    /// </summary>
    public void InjectSensorData(SensorData data) => OnSensorData(data);

    public bool TryReset()
    {
        if (_stateMachine.CurrentState != SystemState.EMERGENCY) return false;
        _emergencyActive = false;
        _lastArduinoHeartbeat = DateTime.UtcNow;
        _lastRpiHeartbeat = DateTime.UtcNow;
        return _stateMachine.TransitionTo(SystemState.IDLE, "manual_reset");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdogTimer?.Dispose();
    }
}
