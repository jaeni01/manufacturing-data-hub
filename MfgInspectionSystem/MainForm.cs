using MfgInspectionSystem.Communication;
using MfgInspectionSystem.Communication.Messages;
using MfgInspectionSystem.Config;
using MfgInspectionSystem.Core;
using MfgInspectionSystem.Data;
using MfgInspectionSystem.Models;
using MfgInspectionSystem.UI;
using MfgInspectionSystem.UI.Controls;
using MfgInspectionSystem.Utils;
using MfgInspectionSystem.Vision;
using Newtonsoft.Json.Linq;
using Serilog;

namespace MfgInspectionSystem;

public partial class MainForm : Form
{
    // ── Config ──
    private readonly AppConfig _config;

    // ── Communication layer ──
    private SerialManager? _serial;
    private MqttSubscriber? _mqtt;
    private YoloClient? _yolo;

    // ── Data layer ──
    private DbWriter? _db;
    private InfluxDbWriter? _influx;

    // ── Core layer ──
    private StateMachine? _stateMachine;
    private ProductDecisionQueue? _queue;
    private InspectionPipeline? _inspection;
    private SortingController? _sorting;
    private SafetyMonitor? _safety;
    private OpenCvPostProcessor? _opencv;

    // ── Cached sensor values (for display) ──
    private double _lastTemp, _lastHumidity, _lastGas;

    // ── Last verdict (for ProcessFlowView) ──
    private Verdict? _lastVerdict;

    // ── S1 재트리거 방지: 검사+분류 완료 후 일정 시간 S1 이벤트 무시 ──
    private DateTime _s1CooldownUntil = DateTime.MinValue;

    // ── 시연용 가스 알람 시퀀스 ──
    private CancellationTokenSource? _demoCts;

    // ── Periodic health-check timer (YOLO recovery + DB recovery) ──
    private System.Windows.Forms.Timer? _healthCheckTimer;

    // ── CAM1 bbox overlay: shows last inspected frame for N seconds then restores live ──
    private System.Windows.Forms.Timer? _cam1BboxTimer;


    public MainForm(AppConfig config)
    {
        _config = config;
        InitializeComponent();
    }

    // ════════════════════════════════════════════════════════════
    //  FORM LOAD
    // ════════════════════════════════════════════════════════════
    private async void MainForm_Load(object sender, EventArgs e)
    {
        Text = "MfgInspectionSystem — PC 통합 허브 v8.0";
        AppendEventLog("시스템 초기화 중...");

        _ = InitializeCam1WebViewAsync();   // WebView2 is handle-ready at Load time
        _ = InitializeCam2WebViewAsync();

        try
        {
            await InitializeAllComponentsAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Initialization failed");
            AppendEventLog($"[FATAL] 초기화 실패: {ex.Message}");
        }
    }

    private async Task InitializeCam1WebViewAsync()
    {
        try
        {
            await webCam1Live.EnsureCoreWebView2Async();
            webCam1Live.CoreWebView2.Settings.IsScriptEnabled               = true;
            webCam1Live.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webCam1Live.CoreWebView2.NavigateToString(MakeCamHtml(_config.Vision.Cam1StreamUrl));
            Log.Information("CAM1 live view initialized: {Url}", _config.Vision.Cam1StreamUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CAM1 WebView init failed — check Cam1StreamUrl in appsettings");
            AppendEventLog($"[Vision] CAM1 라이브 스트림 초기화 실패: {ex.Message}");
        }
    }

    private async Task InitializeCam2WebViewAsync()
    {
        try
        {
            await webCam2Live.EnsureCoreWebView2Async();
            webCam2Live.CoreWebView2.Settings.IsScriptEnabled               = true;
            webCam2Live.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webCam2Live.CoreWebView2.NavigateToString(MakeCamHtml(_config.Vision.Cam2StreamUrl));
            Log.Information("CAM2 live view initialized: {Url}", _config.Vision.Cam2StreamUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CAM2 WebView init failed — check Cam2StreamUrl in appsettings");
            AppendEventLog($"[Vision] CAM2 라이브 스트림 초기화 실패: {ex.Message}");
        }
    }

    private static string MakeCamHtml(string streamUrl) =>
        $$"""
        <html><head><style>
        html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden;background:#000}
        img{width:100%;height:100%;object-fit:contain}
        </style></head>
        <body><img src="{{streamUrl}}" /></body></html>
        """;

    private async Task InitializeAllComponentsAsync()
    {
        // ── 1. Core structures ──
        _stateMachine = new StateMachine();
        _queue = new ProductDecisionQueue();
        _opencv = new OpenCvPostProcessor();

        // ── 2. Data layer ──
        _influx = new InfluxDbWriter(_config.InfluxDb);
        _db = new DbWriter(_config.BuildConnectionString());
        _db.OnDbError += msg => SafeUiAction(() =>
        {
            AppendEventLog($"[DB] {msg}");
            UpdateStatusIndicator(lblDbStatus, false, "DB");
            nodeDb?.SetState(StatusDot.DotState.Critical, "오류");
        });
        _db.Start();

        bool dbOk = await Task.Run(() => _db.TestConnection());
        UpdateStatusIndicator(lblDbStatus, dbOk, "DB");
        nodeDb?.SetState(dbOk ? StatusDot.DotState.Ok : StatusDot.DotState.Critical, dbOk ? "정상" : "오류");
        AppendEventLog(dbOk ? "MySQL 연결 성공" : "MySQL 연결 실패 (계속 진행)");

        // ── 3. Serial ──
        _serial = new SerialManager(_config.Serial);
        WireSerialEvents();

        bool serialOk = await Task.Run(() => _serial.Connect());
        UpdateStatusIndicator(lblSerialStatus, serialOk, "Serial");
        nodeSerial?.SetState(serialOk ? StatusDot.DotState.Ok : StatusDot.DotState.Critical, serialOk ? "정상" : "오류");
        AppendEventLog(serialOk ? $"Arduino 연결 성공 ({_config.Serial.PortName})" : $"Arduino 연결 실패 ({_config.Serial.PortName})");

        // ── 4. MQTT ──
        _mqtt = new MqttSubscriber(_config.Mqtt);
        WireMqttEvents();

        try
        {
            await _mqtt.StartAsync();
            AppendEventLog("MQTT 연결 시도 중...");
        }
        catch (Exception ex)
        {
            AppendEventLog($"MQTT 연결 실패: {ex.Message}");
        }

        // ── 5. YOLO ──
        _yolo = new YoloClient(_config.Yolo);
        _yolo.OnHealthChanged += ok => SafeUiAction(() =>
        {
            UpdateStatusIndicator(lblYoloStatus, ok, "YOLO");
            nodeYolo?.SetState(ok ? StatusDot.DotState.Ok : StatusDot.DotState.Critical, ok ? "정상" : "오류");
            AppendEventLog(ok ? $"[YOLO] 서비스 복구됨 (model={_yolo.ModelVersion})" : "[YOLO] 서비스 응답 없음");
            UpdateProcessFlowUI(_stateMachine?.CurrentState ?? SystemState.IDLE);
        });
        bool yoloOk = await _yolo.CheckHealthAsync();
        UpdateStatusIndicator(lblYoloStatus, yoloOk, "YOLO");
        nodeYolo?.SetState(yoloOk ? StatusDot.DotState.Ok : StatusDot.DotState.Critical, yoloOk ? "정상" : "오류");
        AppendEventLog(yoloOk ? $"YOLO service 정상 (model={_yolo.ModelVersion})" : "YOLO service 연결 실패 (계속 진행)");

        // ── 6. Business logic ──
        _inspection = new InspectionPipeline(_yolo, _opencv!, _queue, _db, _config, _mqtt);
        _inspection.OnLog += msg => SafeUiAction(() => AppendEventLog(msg));
        _inspection.OnInspectionCompleted += d =>
        {
            SafeUiAction(() =>
            {
                UpdateLastInspection(d);
                ShowCam1BboxOverlay(d);     // 검사 완료 시 CAM1에 bbox 오버레이 표시
                _lastVerdict = d.Verdict;
                UpdateProcessFlowUI(_stateMachine?.CurrentState ?? SystemState.IDLE);
            });
            _ = Task.Run(() => _influx?.WriteInspectionMetric(d));
        };

        _sorting = new SortingController(_serial, _queue, _db, _config.Sorting, _mqtt);
        _sorting.OnLog += msg => SafeUiAction(() => AppendEventLog(msg));
        _sorting.OnVerificationResult += (d, ok) => SafeUiAction(() =>
        {
            UpdateStatsUI();
            _influx?.WriteQueueDepth(_queue?.CurrentDepth ?? 0);
        });

        _safety = new SafetyMonitor(_stateMachine, _serial, _mqtt, _db, _config.Safety);
        _safety.OnAlarm += (level, msg) => SafeUiAction(() =>
        {
            AppendEventLog($"[ALARM-{level}] {msg}");
            if (level == "CRITICAL") FlashEmergencyUI();
        });
        _safety.OnAlarmCleared += (metric, msg) => SafeUiAction(() =>
            AppendEventLog($"[알람 해제] {msg}"));
        _safety.OnEmergencyTriggered += () => SafeUiAction(HandleEmergencyUI);
        _safety.Start();

        // ── 7. State machine wiring ──
        _stateMachine.OnStateChanged += (from, to, reason) =>
            SafeUiAction(() => UpdateStateUI(to, reason));

        // ── 8. Queue wiring ──
        _queue.OnEnqueued += d => SafeUiAction(UpdateStatsUI);
        _queue.OnDequeued += d => SafeUiAction(UpdateStatsUI);

        // ── 9. Periodic health-check (30s) — YOLO + DB 복구 자동 감지 ──
        _healthCheckTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _healthCheckTimer.Tick += async (_, _) =>
        {
            // YOLO — CheckHealthAsync fires OnHealthChanged on transition (already wired above)
            if (_yolo != null)
                await _yolo.CheckHealthAsync();

            // DB — TestConnection; update node if status changed
            if (_db != null)
            {
                bool dbOk = await Task.Run(() => _db.TestConnection());
                SafeUiAction(() =>
                {
                    UpdateStatusIndicator(lblDbStatus, dbOk, "DB");
                    nodeDb?.SetState(dbOk ? StatusDot.DotState.Ok : StatusDot.DotState.Critical,
                        dbOk ? "정상" : "오류");
                });
            }
        };
        _healthCheckTimer.Start();

        // ── CAM1 bbox overlay auto-restore (4 s after each inspection) ──
        _cam1BboxTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        _cam1BboxTimer.Tick += (_, _) =>
        {
            _cam1BboxTimer.Stop();
            try { webCam1Live.CoreWebView2?.NavigateToString(MakeCamHtml(_config.Vision.Cam1StreamUrl)); }
            catch { /* WebView2가 아직 초기화 안 된 경우 무시 */ }
        };

        // ── 10. Initial UI state ──
        UpdateStateUI(_stateMachine.CurrentState, "init");
        UpdateStatsUI();
        AppendEventLog("초기화 완료. IDLE 상태.");
        Log.Information("All components initialized");
    }

    // ════════════════════════════════════════════════════════════
    //  EVENT WIRING
    // ════════════════════════════════════════════════════════════
    private void WireSerialEvents()
    {
        _serial!.OnIrEvent += async (irEvt) =>
        {
            SafeUiAction(() => AppendSerialLog($"IR: {irEvt.Sensor} {irEvt.State}"));

            switch (irEvt.Sensor)
            {
                case "S1":
                    // S1: 컨베이어 정지 신호 → 검사 + 분류 트리거 (RUNNING 상태에서만)
                    if (!irEvt.IsBlocked || _stateMachine?.IsOperational != true) return;
                    if (DateTime.UtcNow < _s1CooldownUntil) return; // 쿨다운 중 — 동일 제품 재트리거 방지
                    _s1CooldownUntil = DateTime.UtcNow.AddMilliseconds(1000); // S1 바운스 방지 (아두이노 !isProcessing이 주 가드)
                    SafeUiAction(() => AppendEventLog("[IR] S1 blocked — 검사+분류 시작"));
                    if (_inspection != null) await _inspection.RunAsync();
                    if (_sorting  != null) await _sorting.OnS3TriggeredAsync();
                    // 서보 발사 후 컨베이어 진동으로 인한 S1 오감지 억제
                    _s1CooldownUntil = DateTime.UtcNow.AddMilliseconds(_config.Sorting.SensorLockoutAfterServoMs);
                    // Arduino가 s1Detected 해제(5초) 후 자동으로 모터 재시작
                    // motor 명령은 새 펌웨어(v1.7.2)에서 제거됨 — 전송하지 않음
                    break;
                case "S2":
                    // S2: DEFECT 박스 검증 센서 (IsOperational 무관 — 제품이 이미 이동 중)
                    if (!irEvt.IsBlocked) return;
                    SafeUiAction(() => AppendEventLog("[IR] S2 blocked — DEFECT 검증"));
                    _sorting?.OnVerificationSensor(irEvt.Sensor);
                    break;
                case "S3":
                    // S3: HOLD 박스 검증 센서
                    if (!irEvt.IsBlocked) return;
                    SafeUiAction(() => AppendEventLog("[IR] S3 blocked — HOLD 검증"));
                    _sorting?.OnVerificationSensor(irEvt.Sensor);
                    break;
            }
        };

        // estop 이벤트: 물리 E-STOP 버튼 press/release 또는 PC timeout.
        // S1 제품 감지는 ir 이벤트를 사용하므로 여기서는 검사 트리거 없음.
        // SafetyMonitor가 RUNNING이 아닐 때 EMERGENCY 전환을 담당.
        _serial.OnEstopTriggered += (evt) =>
        {
            SafeUiAction(() =>
            {
                AppendSerialLog($"E-STOP: {evt.Reason}");
                if (_stateMachine?.CurrentState != SystemState.EMERGENCY)
                    AppendEventLog($"[Arduino] E-STOP 수신 ({evt.Reason})");
            });
        };

        _serial.OnHeartbeatReceived += evt =>
        {
            SafeUiAction(() =>
            {
                UpdateStatusIndicator(lblSerialStatus, true, "Serial");
                nodeSerial?.SetState(StatusDot.DotState.Ok, "정상");
                AppendSerialLog($"HB uptime={evt.Uptime}ms");
            });
        };

        _serial.OnDisconnected += () =>
        {
            SafeUiAction(() =>
            {
                UpdateStatusIndicator(lblSerialStatus, false, "Serial");
                nodeSerial?.SetState(StatusDot.DotState.Critical, "오프라인");
                AppendEventLog("[Serial] Arduino 연결 끊김");
            });
        };

        _serial.OnSerialError += msg =>
        {
            SafeUiAction(() => AppendEventLog($"[Serial] {msg}"));
        };

        _serial.OnRawLine += line =>
        {
            SafeUiAction(() => AppendSerialLog(line));
        };
    }

    private void WireMqttEvents()
    {
        _mqtt!.OnSensorData += data =>
        {
            SafeUiAction(() =>
            {
                UpdateSensorUI(data);
                AppendMqttLog($"{data.MqttTopic} = {data.Value:F1} {data.Unit}");
            });
            _db?.WriteSensorReading(data);
        };

        _mqtt.OnBrokerConnectionChanged += connected =>
        {
            SafeUiAction(() =>
            {
                UpdateStatusIndicator(lblMqttStatus, connected, "MQTT");
                nodeMqtt?.SetState(connected ? StatusDot.DotState.Ok : StatusDot.DotState.Critical,
                    connected ? "연결됨" : "오프라인");
                AppendEventLog(connected ? "MQTT broker 연결됨" : "MQTT broker 연결 끊김");
            });
        };

        _mqtt.OnRpiOnlineChanged += online =>
        {
            SafeUiAction(() =>
            {
                UpdateStatusIndicator(lblRpiStatus, online, "RPi");
                nodeRpi?.SetState(online ? StatusDot.DotState.Ok : StatusDot.DotState.Critical,
                    online ? "온라인" : "오프라인");
                AppendEventLog(online ? "RPi online" : "RPi offline (LWT)");
            });
        };

        _mqtt.OnRpiHeartbeat += () =>
        {
            SafeUiAction(() =>
            {
                AppendMqttLog("RPi heartbeat");
                UpdateStatusIndicator(lblRpiStatus, true, "RPi");
                nodeRpi?.SetState(StatusDot.DotState.Ok, "온라인");
            });
        };

        _mqtt.OnMqttError += msg =>
        {
            SafeUiAction(() => AppendEventLog($"[MQTT] {msg}"));
        };

        _mqtt.OnMessageReceived += (topic, payload) =>
        {
            SafeUiAction(() => AppendMqttLog($"← {topic}: {payload}"));
        };
    }

    // ════════════════════════════════════════════════════════════
    //  BUTTON HANDLERS
    // ════════════════════════════════════════════════════════════
    private async void btnStart_Click(object sender, EventArgs e)
    {
        if (_stateMachine == null || _serial == null) return;

        if (_stateMachine.TransitionTo(SystemState.RUNNING, "Operator START"))
        {
            await _serial.SetMotor(200);
            await _serial.SetLed("green", true);
            await _serial.SetLed("yellow", false);
            await _serial.SetLed("red", false);
            _db?.WriteEventLog("system_start", "INFO", "operator", "System started");
            AppendEventLog("컨베이어 기동 — RUNNING");

            // 시연 시퀀스: 15초 후 가스 WARNING → 5초 후 가스 CRITICAL
            _demoCts?.Cancel();
            _demoCts = new CancellationTokenSource();
            _ = RunDemoGasSequenceAsync(_demoCts.Token);
        }
    }

    private async void btnStop_Click(object sender, EventArgs e)
    {
        if (_stateMachine == null || _serial == null) return;

        // RUNNING → PAUSED 전이 (실패하면 이미 PAUSED/IDLE 등 → 무시)
        if (!_stateMachine.TransitionTo(SystemState.PAUSED, "Operator PAUSE"))
            return;
        _demoCts?.Cancel();

        // 컨베이어 정지 + LED 노랑
        await _serial.SetMotor(0);
        await _serial.SetLed("green", false);
        await _serial.SetLed("yellow", true);
        _db?.WriteEventLog("system_pause", "INFO", "operator", "System paused");
        AppendEventLog("컨베이어 정지 — PAUSED");
    }

    private async void btnResume_Click(object sender, EventArgs e)
    {
        if (_stateMachine == null || _serial == null) return;

        if (_stateMachine.TransitionTo(SystemState.RUNNING, "Operator RESUME"))
        {
            await _serial.SetMotor(200);
            await _serial.SetLed("green", true);
            await _serial.SetLed("yellow", false);
            AppendEventLog("컨베이어 재기동 — RUNNING");
        }
    }

    private async void btnEmergency_Click(object sender, EventArgs e)
    {
        if (_stateMachine?.CurrentState == SystemState.EMERGENCY)
        {
            // 비상 해제
            if (_safety == null || _serial == null) return;
            if (_safety.TryReset())
            {
                await _serial.ReleaseEstop();
                await _serial.SetMotor(0);
                await _serial.SetBuzzer(false);     // TriggerEmergency에서 켠 버저 끄기
                await _serial.SetLed("red", false); // 비상 LED 끄기
                _queue?.Clear();
                AppendEventLog("비상 해제 완료 — IDLE 상태");
                _db?.WriteEventLog("emergency_release", "INFO", "operator", "Emergency released from UI");
            }
        }
        else
        {
            // 비상 정지
            if (_stateMachine == null || _serial == null) return;
            _stateMachine.TransitionTo(SystemState.EMERGENCY, "Manual E-STOP from UI");
            await _serial.SetMotor(0);
            await _serial.TriggerEstop();
            _db?.WriteEventLog("manual_estop", "CRITICAL", "operator", "Manual E-STOP triggered from UI");
        }
    }

    private async void btnReset_Click(object sender, EventArgs e)
    {
        if (_safety == null || _serial == null) return;
        if (_stateMachine?.CurrentState != SystemState.EMERGENCY) return;

        _demoCts?.Cancel();

        if (_safety.TryReset())
        {
            await _serial.ReleaseEstop();
            await _serial.SetMotor(0);
            await _serial.SetBuzzer(false);     // TriggerEmergency에서 켠 버저 끄기
            await _serial.SetLed("red", false); // 비상 LED 끄기
            _queue?.Clear();
            AppendEventLog("시스템 RESET — IDLE");
            _db?.WriteEventLog("system_reset", "INFO", "operator", "System reset to IDLE");
        }
        else
        {
            AppendEventLog("RESET 실패 — EMERGENCY 상태에서만 가능");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  시연용 가스 알람 시퀀스
    // ════════════════════════════════════════════════════════════
    private async Task RunDemoGasSequenceAsync(CancellationToken ct)
    {
        try
        {
            // 15초 후: 가스 400 ppm → WARNING 알람
            await Task.Delay(15_000, ct);
            var warn = new Communication.Messages.SensorData
                { Metric = "gas", Value = 400, Quality = "good", Timestamp = DateTime.UtcNow };
            _safety?.InjectSensorData(warn);
            SafeUiAction(() =>
            {
                UpdateSensorUI(warn);
                AppendEventLog("[시연] 가스 400 ppm 주입 → WARNING");
            });

            // 5초 후: 가스 600 ppm → CRITICAL (비상정지)
            await Task.Delay(5_000, ct);
            var crit = new Communication.Messages.SensorData
                { Metric = "gas", Value = 600, Quality = "good", Timestamp = DateTime.UtcNow };
            _safety?.InjectSensorData(crit);
            SafeUiAction(() =>
            {
                UpdateSensorUI(crit);
                AppendEventLog("[시연] 가스 600 ppm 주입 → CRITICAL");
            });

            // 10초 후: 가스 200 ppm → 정상 복귀 (비상 해제 가능 상태)
            await Task.Delay(10_000, ct);
            var norm = new Communication.Messages.SensorData
                { Metric = "gas", Value = 200, Quality = "good", Timestamp = DateTime.UtcNow };
            _safety?.InjectSensorData(norm);
            SafeUiAction(() =>
            {
                UpdateSensorUI(norm);
                AppendEventLog("[시연] 가스 200 ppm → 정상 복귀, 비상 해제 가능");
            });
        }
        catch (OperationCanceledException) { }
    }

    private async void btnYoloHealth_Click(object sender, EventArgs e)
    {
        if (_yolo == null) return;
        bool ok = await _yolo.CheckHealthAsync();
        UpdateStatusIndicator(lblYoloStatus, ok, "YOLO");
        nodeYolo?.SetState(ok ? StatusDot.DotState.Ok : StatusDot.DotState.Critical, ok ? "정상" : "오류");
        AppendEventLog(ok ? $"YOLO health OK (model={_yolo.ModelVersion})" : "YOLO health FAIL");
    }

    private async void btnDbTest_Click(object sender, EventArgs e)
    {
        if (_db == null) return;
        bool ok = await Task.Run(() => _db.TestConnection());
        UpdateStatusIndicator(lblDbStatus, ok, "DB");
        nodeDb?.SetState(ok ? StatusDot.DotState.Ok : StatusDot.DotState.Critical, ok ? "정상" : "오류");
        AppendEventLog(ok ? "DB 연결 정상" : "DB 연결 실패");
    }

    private void btnClearStats_Click(object sender, EventArgs e)
    {
        _queue?.Clear();        // 미처리 항목도 함께 비움
        _queue?.ResetStats();
        _lastVerdict = null;
        UpdateStatsUI();
        UpdateProcessFlowUI(_stateMachine?.CurrentState ?? SystemState.IDLE);
        AppendEventLog("통계 및 큐 초기화");
    }

    private void btnAuditVerify_Click(object sender, EventArgs e)
    {
        if (_db == null || !_db.IsConnected)
        {
            AppendEventLog("[Audit] DB 연결 없음 — 감사 체인 검증 불가");
            return;
        }

        try
        {
            var result = AuditChainVerifier.Verify(_config.BuildConnectionString());
            string msg = result.Broken == 0
                ? $"[Audit] 감사 체인 검증 완료: {result.Checked}건 — 무결 (broken=0) ✓"
                : $"[Audit] ⚠ 감사 체인 불일치: {result.Checked}건 중 {result.Broken}건 오류, 첫 오류 id={result.FirstBrokenId}";

            AppendEventLog(msg);
            Log.Information("AuditChain: checked={C} broken={B} firstBrokenId={Id}",
                result.Checked, result.Broken, result.FirstBrokenId);

            MessageBox.Show(msg, "감사 체인 검증",
                MessageBoxButtons.OK,
                result.Broken == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendEventLog($"[Audit] 검증 오류: {ex.Message}");
            Log.Error(ex, "AuditChainVerifier failed");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  UI UPDATE HELPERS
    // ════════════════════════════════════════════════════════════
    private void UpdateStateUI(SystemState state, string reason)
    {
        string label = state switch
        {
            SystemState.IDLE => "● IDLE",
            SystemState.RUNNING => "● RUNNING",
            SystemState.PAUSED => "● PAUSED",
            SystemState.EMERGENCY => "▲ EMERGENCY",
            _ => state.ToString()
        };

        lblSystemState.Text = label;
        lblSystemState.BackColor = state.ToStateColor();
        lblSystemState.ForeColor = state == SystemState.EMERGENCY ? Color.White : Color.Black;

        // Button states
        btnStart.Enabled = state is SystemState.IDLE;
        btnStop.Enabled = state is SystemState.RUNNING;
        btnResume.Enabled = state is SystemState.PAUSED;
        btnReset.Enabled = state is SystemState.EMERGENCY;

        // Emergency button toggles text/color based on state
        btnEmergency.Text = state == SystemState.EMERGENCY ? "비상 해제" : "비상 정지";
        btnEmergency.BackColor = state == SystemState.EMERGENCY ? Color.DarkOrange : Color.Firebrick;

        AppendEventLog($"[State] {state} ← {reason}");
        _ = _mqtt?.PublishPcStateAsync(state.ToString());
        UpdateProcessFlowUI(state);
    }

    private void UpdateSensorUI(SensorData data)
    {
        switch (data.Metric)
        {
            case "temperature":
                _lastTemp = data.Value;
                lblTemp.Text = $"온도: {data.Value:F1} °C";
                var tc = data.Value >= _config.Safety.TempCritical ? DesignTokens.Critical
                    : data.Value >= _config.Safety.TempWarning ? DesignTokens.Warn : DesignTokens.Ok;
                lblTemp.ForeColor = tc;
                if (metricTemp != null) { metricTemp.Value = $"{data.Value:F1}"; metricTemp.ValueColor = tc; }
                break;
            case "humidity":
                _lastHumidity = data.Value;
                lblHumidity.Text = $"습도: {data.Value:F1} %";
                var hc = data.Value >= _config.Safety.HumidityCritical ? DesignTokens.Critical
                    : data.Value >= _config.Safety.HumidityWarning ? DesignTokens.Warn : DesignTokens.Ok;
                lblHumidity.ForeColor = hc;
                if (metricHum != null) { metricHum.Value = $"{data.Value:F1}"; metricHum.ValueColor = hc; }
                break;
            case "gas":
                _lastGas = data.Value;
                lblGas.Text = $"가스: {data.Value:F0} ppm";
                var gc = data.Value >= _config.Safety.GasCritical ? DesignTokens.Critical
                    : data.Value >= _config.Safety.GasWarning ? DesignTokens.Warn : DesignTokens.Ok;
                lblGas.ForeColor = gc;
                if (metricGas != null) { metricGas.Value = $"{data.Value:F0}"; metricGas.ValueColor = gc; }
                break;
        }
    }

    private void UpdateStatsUI()
    {
        if (_queue == null) return;
        lblTotal.Text = $"전체: {_queue.TotalInspected}";
        lblPass.Text = $"PASS: {_queue.PassCount}";
        lblDefect.Text = $"DEFECT: {_queue.DefectCount}";
        lblHold.Text = $"HOLD: {_queue.HoldCount}";

        if (_queue.TotalInspected > 0)
        {
            double passRate = _queue.PassCount * 100.0 / _queue.TotalInspected;
            lblPassRate.Text = $"양품률: {passRate:F1}%";
        }
        else
        {
            lblPassRate.Text = "양품률: —";
        }

        // Publish production stats retained (Appendix B: mfg/line1/status/pc/production)
        _ = _mqtt?.PublishPcProductionAsync(
            _queue.TotalInspected, _queue.PassCount, _queue.DefectCount, _queue.HoldCount);
    }

    private void UpdateLastInspection(ProductDecision d)
    {
        // ── Orphan legacy fields — safe no-op assignments ──
        lblLastId.Text        = d.ProductId;
        lblLastType.Text      = d.ProductType;
        lblLastYoloClass.Text = d.YoloClass ?? "-";
        lblLastConf.Text      = $"{d.YoloConfidence:F2}";
        lblLastPins.Text      = $"{d.PinCount}";
        lblLastBlur.Text      = $"{d.BlurScore:F0}";
        lblLastImage.Text     = d.Cam1ImagePath ?? "-";
        lblLastTime.Text      = $"시각: {d.InspectedAt:HH:mm:ss}";
        lblLastVerdict.Text      = d.Verdict.ToKoreanLabel();
        lblLastVerdict.ForeColor = d.Verdict.ToVerdictColor();

        // ── Result card — verdict badge ──
        var vc = d.Verdict.ToVerdictColor();
        lblResultBigVerdict.Text      = d.Verdict.ToKoreanLabel();
        lblResultBigVerdict.ForeColor = vc;
        pnlVerdictBadge.BackColor     = Color.FromArgb(35, vc);

        // ── Result card — detail table ──
        lblResultId.Text    = d.ProductId;
        lblResultType.Text  = d.ProductType;
        lblResultYolo.Text  = d.YoloClass ?? "-";
        lblResultConf.Text  = $"{d.YoloConfidence:F2}";
        lblResultPins.Text  = $"{d.PinCount}";
        lblResultBlur.Text  = $"{d.BlurScore:F0}";

        // ── Result card — inspection thumbnail (+ YOLO bbox overlay) ──
        if (!string.IsNullOrEmpty(d.Cam1ImagePath) && File.Exists(d.Cam1ImagePath))
        {
            try
            {
                picResultThumb.Image?.Dispose();
                using var fs  = File.OpenRead(d.Cam1ImagePath);
                var       bmp = new Bitmap(fs);
                if (!string.IsNullOrEmpty(d.AllDetectionsJson))
                    DrawDetectionBoxes(Graphics.FromImage(bmp), d.AllDetectionsJson);
                picResultThumb.Image = bmp;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Result thumb load failed: {Path}", d.Cam1ImagePath);
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  CAM1 BBOX OVERLAY — 검사 완료 후 4초간 annotated 프레임 표시
    // ════════════════════════════════════════════════════════════
    /// <summary>
    /// 검사 완료 직후 CAM1 WebView2에 annotated 캡처 프레임(+ bbox)을 표시한다.
    /// 4초 후 _cam1BboxTimer 가 라이브 스트림으로 자동 복귀.
    /// WebView2 미초기화 또는 이미지 없으면 조용히 건너뜀.
    /// </summary>
    private void ShowCam1BboxOverlay(ProductDecision d)
    {
        if (_cam1BboxTimer == null) return;
        if (webCam1Live.CoreWebView2 == null) return;
        if (string.IsNullOrEmpty(d.Cam1ImagePath) || !File.Exists(d.Cam1ImagePath)) return;

        try
        {
            // 캡처 이미지 로드 + bbox 드로잉
            Bitmap bmp;
            using (var fs = File.OpenRead(d.Cam1ImagePath))
                bmp = new Bitmap(fs);

            if (!string.IsNullOrEmpty(d.AllDetectionsJson))
                DrawDetectionBoxes(Graphics.FromImage(bmp), d.AllDetectionsJson);

            // JPEG base64 인코딩
            string b64;
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                b64 = Convert.ToBase64String(ms.ToArray());
            }
            bmp.Dispose();

            // Verdict 색상 배너 (판정 결과를 화면 하단에 간단히 표기)
            string verdictLabel = d.Verdict switch
            {
                Verdict.PASS   => "PASS",
                Verdict.DEFECT => "DEFECT",
                Verdict.HOLD   => "HOLD",
                _              => "—"
            };
            string verdictHex = d.Verdict switch
            {
                Verdict.PASS   => "#22c55e",
                Verdict.DEFECT => "#ef4444",
                Verdict.HOLD   => "#f59e0b",
                _              => "#64748b"
            };

            // WebView2에 정적 프레임 (이미지 + 판정 배너) 표시
            string html = $$"""
                <html><head><style>
                html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden;background:#000;position:relative}
                #img{width:100%;height:100%;object-fit:contain;display:block}
                #badge{position:absolute;bottom:8px;left:50%;transform:translateX(-50%);
                       background:{{verdictHex}};color:#fff;font:bold 14px/28px monospace;
                       padding:0 16px;border-radius:4px;opacity:.88;pointer-events:none}
                </style></head>
                <body>
                <img id="img" src="data:image/jpeg;base64,{{b64}}" />
                <div id="badge">{{verdictLabel}} — {{d.ProductType}} {{d.YoloConfidence:F2}}</div>
                </body></html>
                """;
            webCam1Live.CoreWebView2.NavigateToString(html);

            // 4초 후 라이브 스트림으로 자동 복귀
            _cam1BboxTimer.Stop();
            _cam1BboxTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CAM1 bbox overlay failed — 라이브 스트림 유지");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  PROCESS FLOW (실시간 반영)
    // ════════════════════════════════════════════════════════════
    private void UpdateProcessFlowUI(SystemState state)
    {
        if (_processFlow == null) return;

        var (convStatus, convColor) = state switch
        {
            SystemState.RUNNING   => ("운전 중",   DesignTokens.Info),
            SystemState.PAUSED    => ("일시 정지", DesignTokens.Warn),
            SystemState.EMERGENCY => ("비상 정지", DesignTokens.Critical),
            _                     => ("정지",      DesignTokens.Neutral),
        };

        bool yoloOk = _yolo?.IsHealthy ?? false;
        var (yoloStatus, yoloColor) = yoloOk
            ? ("정상", DesignTokens.Ok)
            : (_yolo == null ? "대기" : "오류",
               _yolo == null ? DesignTokens.Neutral : DesignTokens.Critical);

        bool serialOk = _serial?.IsConnected ?? false;
        var (sortStatus, sortColor) = serialOk && state == SystemState.RUNNING
            ? ("준비", DesignTokens.Info)
            : ("대기", DesignTokens.Neutral);

        var (verdictStr, verdictColor) = _lastVerdict switch
        {
            Verdict.PASS   => ("PASS",   DesignTokens.Pass),
            Verdict.DEFECT => ("DEFECT", DesignTokens.Defect),
            Verdict.HOLD   => ("HOLD",   DesignTokens.Hold),
            _              => ("—",      DesignTokens.Neutral),
        };

        _processFlow.Steps = new List<ProcessFlowView.Step>
        {
            new("투입 컨베이어",        "속도 120 mm/s", convStatus,  convColor),
            new("CAM1 상부 검사",       "FPS: 실시간",   yoloStatus,  yoloColor),
            new("판정 엔진",            "YOLO + OpenCV", "정상",       DesignTokens.Ok),
            new("분류 서보",            "위치 45.0°",    sortStatus,  sortColor),
            new("PASS / DEFECT / HOLD", verdictStr,      "",           verdictColor),
        };
        _processFlow.Invalidate();
    }

    // ════════════════════════════════════════════════════════════
    //  YOLO BBOX OVERLAY
    // ════════════════════════════════════════════════════════════
    private static void DrawDetectionBoxes(Graphics g, string detectionsJson)
    {
        try
        {
            var arr = JArray.Parse(detectionsJson);
            if (arr.Count == 0) return;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var penOk   = new Pen(Color.FromArgb(34, 197, 94), 2);
            using var penBad  = new Pen(Color.FromArgb(239, 68,  68), 2);
            using var font    = new Font("Consolas", 8, FontStyle.Bold);
            using var bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            using var fgOk    = new SolidBrush(Color.FromArgb(34, 197, 94));
            using var fgBad   = new SolidBrush(Color.FromArgb(239, 68,  68));

            bool anyBbox = false;

            foreach (var item in arr)
            {
                // bbox 파싱: {bbox:{x,y,w,h}} / {bbox:{x1,y1,x2,y2}} / flat {x1,y1,x2,y2}
                float x = 0, y = 0, w = 0, h = 0;
                var bbox = item["bbox"];
                if (bbox is JObject jo)
                {
                    x = jo["x"]?.Value<float>()  ?? jo["x1"]?.Value<float>() ?? 0;
                    y = jo["y"]?.Value<float>()  ?? jo["y1"]?.Value<float>() ?? 0;
                    w = jo["w"]?.Value<float>()  ?? ((jo["x2"]?.Value<float>() ?? x) - x);
                    h = jo["h"]?.Value<float>()  ?? ((jo["y2"]?.Value<float>() ?? y) - y);
                }
                else
                {
                    float x1 = item["x1"]?.Value<float>() ?? 0;
                    float y1 = item["y1"]?.Value<float>() ?? 0;
                    x = x1; y = y1;
                    w = (item["x2"]?.Value<float>() ?? x1) - x1;
                    h = (item["y2"]?.Value<float>() ?? y1) - y1;
                }

                if (w < 2 || h < 2) continue;  // bbox 없으면 rect 스킵
                anyBbox = true;

                string cls   = item["class"]?.ToString() ?? "";
                double conf  = item["confidence"]?.Value<double>() ?? 0;
                bool   isBad = cls.Contains("defective");

                g.DrawRectangle(isBad ? penBad : penOk, x, y, w, h);

                string lbl = $"{cls} {conf:F2}";
                var    sz  = g.MeasureString(lbl, font);
                float  ly  = y > sz.Height + 2 ? y - sz.Height - 2 : y + 2;
                g.FillRectangle(bgBrush, x, ly, sz.Width + 4, sz.Height + 2);
                g.DrawString(lbl, font, isBad ? fgBad : fgOk, x + 2, ly + 1);
            }

            // ── bbox 없을 때 fallback: 좌상단에 클래스명 텍스트 오버레이 ──────
            // 서버가 bbox 좌표를 응답에 포함하면 자동으로 rect 드로잉으로 전환됨
            if (!anyBbox)
            {
                float ty = 5f;
                foreach (var item in arr)
                {
                    string cls  = item["class"]?.ToString() ?? "";
                    double conf = item["confidence"]?.Value<double>() ?? 0;
                    bool   bad  = cls.Contains("defective");
                    string lbl  = $"▶ {cls}  {conf:F2}";
                    var    sz   = g.MeasureString(lbl, font);
                    g.FillRectangle(bgBrush, 4f, ty, sz.Width + 6, sz.Height + 2);
                    g.DrawString(lbl, font, bad ? fgBad : fgOk, 7f, ty + 1);
                    ty += sz.Height + 3;
                }
            }
        }
        catch { /* bbox 드로잉은 best-effort */ }
        finally { g.Dispose(); }
    }

    private void HandleEmergencyUI()
    {
        UpdateStateUI(SystemState.EMERGENCY, "emergency_triggered");
    }

    private void FlashEmergencyUI()
    {
        // Flash the state label once
        var orig = lblSystemState.BackColor;
        lblSystemState.BackColor = Color.OrangeRed;
        Task.Delay(200).ContinueWith(_ => SafeUiAction(() => lblSystemState.BackColor = orig));
    }

    private static void UpdateStatusIndicator(Label lbl, bool ok, string name)
    {
        lbl.Text = ok ? $"● {name}" : $"○ {name}";
        lbl.ForeColor = ok ? Color.Green : Color.Red;
    }
    // ── Log helpers ──
    private void AppendLog(RichTextBox box, string msg, Color? color = null)
    {
        if (box.Lines.Length > 2000)
        {
            // ReadOnly=true 상태에서 SelectedText="" 를 호출하면
            // Win32 RichEdit가 삭제를 거부하며 MessageBeep를 울림 → "띵 띵" 소리 원인.
            // 잠시 ReadOnly를 해제하고 삭제 후 복원.
            box.ReadOnly = false;
            box.Select(0, box.GetFirstCharIndexFromLine(500));
            box.SelectedText = "";
            box.ReadOnly = true;
        }
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = color ?? box.ForeColor;
        box.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        box.SelectionColor = box.ForeColor;
        box.ScrollToCaret();
    }

    private void AppendEventLog(string msg)
    {
        // RichTextBox background is near-black — use bright/pastel colours only.
        Color c = msg.Contains("[ALARM") || msg.Contains("EMERGENCY") ? Color.Tomato
            : msg.Contains("WARNING") || msg.Contains("경고") ? Color.Orange
            : msg.Contains("[알람 해제]") ? Color.LightGreen
            : Color.LightGray;
        AppendLog(rtbEventLog, msg, c);
    }

    private void AppendSerialLog(string msg) => AppendLog(rtbSerialLog, msg, Color.CornflowerBlue);
    private void AppendMqttLog(string msg) => AppendLog(rtbMqttLog, msg, Color.MediumSeaGreen);

    // ── Thread-safe UI helper ──
    private void SafeUiAction(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    // ════════════════════════════════════════════════════════════
    //  FORM CLOSING
    // ════════════════════════════════════════════════════════════
    private async void MainForm_FormClosing(object sender, FormClosingEventArgs e)
    {
        Log.Information("Application closing...");

        _healthCheckTimer?.Stop();
        _healthCheckTimer?.Dispose();
        _cam1BboxTimer?.Stop();
        _cam1BboxTimer?.Dispose();
        _safety?.Dispose();

        try
        {
            if (_serial?.IsConnected == true)
            {
                await _serial.SetMotor(0);
                await _serial.SetLed("green", false);
                await _serial.SetLed("yellow", false);
                await _serial.SetLed("red", false);
                await _serial.SetBuzzer(false);
            }
        }
        catch { }

        _serial?.Dispose();

        try { await (_mqtt?.StopAsync() ?? Task.CompletedTask); } catch { }
        _mqtt?.Dispose();

        _yolo?.Dispose();
        _influx?.Dispose();
        _db?.Dispose();
    }
}
