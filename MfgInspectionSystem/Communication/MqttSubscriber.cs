using System.Text;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using MfgInspectionSystem.Communication.Messages;
using MfgInspectionSystem.Config;
using MfgInspectionSystem.Models;
using MfgInspectionSystem.Observability;
using Newtonsoft.Json.Linq;
using Serilog;
namespace MfgInspectionSystem.Communication;

public class MqttSubscriber : IDisposable
{
    private IManagedMqttClient? _client;
    private readonly MqttConfig _cfg;
    private bool _disposed;
    private long _publishSeq = 0;

    // Events
    public event Action<SensorData>? OnSensorData;
    public event Action<bool>? OnRpiOnlineChanged;
    public event Action? OnRpiHeartbeat;
    public event Action<bool>? OnBrokerConnectionChanged;
    public event Action<string>? OnMqttError;
    public event Action<string, string>? OnMessageReceived;  // topic, payload (debug)

    public bool IsConnected => _client?.IsConnected == true;
    public DateTime LastRpiHeartbeat { get; private set; } = DateTime.MinValue;

    // Sensor cache
    public double Temperature { get; private set; }
    public double Humidity { get; private set; }
    public double GasLevel { get; private set; }

    public MqttSubscriber(MqttConfig cfg) => _cfg = cfg;

    public async Task StartAsync()
    {
        var factory = new MqttFactory();
        _client = factory.CreateManagedMqttClient();

        var builder = new MqttClientOptionsBuilder()
            .WithClientId(_cfg.ClientId)
            .WithTcpServer(_cfg.BrokerHost, _cfg.BrokerPort)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithCleanSession(false);

        if (!string.IsNullOrEmpty(_cfg.Username))
            builder = builder.WithCredentials(_cfg.Username, _cfg.Password);

        // PC LWT — broker publishes this if we disconnect ungracefully
        var lwtPayload = $"{{\"online\":false,\"node\":\"{_cfg.ClientId}\"," +
                         $"\"ts\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}";
        builder = builder
            .WithWillTopic($"{_cfg.Topics.StatusPcPublishBase}/online")
            .WithWillPayload(lwtPayload)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain(true);

        if (_cfg.TlsEnabled)
        {
            var certs = LoadClientCertificates(_cfg);
            var customCa = LoadCustomCa(_cfg);

            builder = builder.WithTlsOptions(tls =>
            {
                tls.UseTls(true);
                // TLS 1.2/1.3 둘 다 허용 — Windows + Mosquitto mTLS는 1.3 단독에서 가끔 깨짐
                tls.WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
                tls.WithAllowUntrustedCertificates(_cfg.AllowUntrustedCertificates);
                tls.WithIgnoreCertificateChainErrors(false);
                tls.WithIgnoreCertificateRevocationErrors(false);

                if (certs.Count > 0)
                    tls.WithClientCertificates(certs);

                // ── 자체 CA 기반 chain 검증 (OS 신뢰 저장소 의존 X) ─────────
                if (customCa != null)
                {
                    tls.WithCertificateValidationHandler(ctx =>
                    {
                        using var chain = new X509Chain();
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(customCa);
                        chain.ChainPolicy.ExtraStore.Add(customCa);

                        using var serverCert = new X509Certificate2(ctx.Certificate);
                        bool chainOk = chain.Build(serverCert);

                        if (!chainOk)
                        {
                            var status = string.Join("; ",
                                chain.ChainStatus.Select(s => $"{s.Status}:{s.StatusInformation.Trim()}"));
                            Log.Warning("TLS chain build failed: {Status}", status);
                            return false;
                        }

                        // server cert에 SAN(IP 포함) 들어가 있으니 엄격 검증
                        var nonChainErrors = ctx.SslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors;
                        if (nonChainErrors != SslPolicyErrors.None)
                        {
                            Log.Warning("TLS policy error (non-chain): {Errors}", nonChainErrors);
                            return false;
                        }

                        Log.Debug("TLS chain validated against custom CA: subject={S}", serverCert.Subject);

                        return true;
                    });
                }
                else if (_cfg.AllowUntrustedCertificates)
                {
                    tls.WithCertificateValidationHandler(ctx =>
                    {
                        Log.Warning("MQTT TLS accepted with errors: {Errors}", ctx.SslPolicyErrors);
                        return true;
                    });
                }
            });

            Log.Information("MQTT TLS enabled: host={Host} port={Port} customCa={HasCa}",
                _cfg.BrokerHost, _cfg.BrokerPort, customCa != null);
        }

        var clientOptions = builder.Build();

        var managedOptions = new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(clientOptions)
            .WithAutoReconnectDelay(TimeSpan.FromMilliseconds(_cfg.ReconnectDelayMs))
            .Build();

        _client.ApplicationMessageReceivedAsync += HandleMessageAsync;
        _client.ConnectedAsync += async e =>
        {
            Log.Information("MQTT broker connected: {Host}:{Port} (tls={Tls})",
                _cfg.BrokerHost, _cfg.BrokerPort, _cfg.TlsEnabled);
            OnBrokerConnectionChanged?.Invoke(true);
            // Publish PC online status (Sparkplug B NBIRTH 개념)
            try
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic($"{_cfg.Topics.StatusPcPublishBase}/online")
                    .WithPayload($"{{\"online\":true,\"node\":\"{_cfg.ClientId}\",\"ts\":{ts}}}")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(true)
                    .Build();
                await _client.EnqueueAsync(msg);
                AppMetrics.MqttMessages.WithLabels($"{_cfg.Topics.StatusPcPublishBase}/online", "out").Inc();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to publish PC online status");
            }
        };
        _client.DisconnectedAsync += e =>
        {
            Log.Warning("MQTT broker disconnected: {Reason}", e.Reason);
            OnBrokerConnectionChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        await _client.StartAsync(managedOptions);

        await _client.SubscribeAsync(_cfg.Topics.EnvSubscribe, MqttQualityOfServiceLevel.AtMostOnce);
        await _client.SubscribeAsync(_cfg.Topics.StatusRpiSubscribe, MqttQualityOfServiceLevel.AtLeastOnce);

        Log.Information("MQTT subscribed: {Env} | {Status}",
            _cfg.Topics.EnvSubscribe, _cfg.Topics.StatusRpiSubscribe);
    }

    private Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        string topic = e.ApplicationMessage.Topic;
        string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

        OnMessageReceived?.Invoke(topic, payload);
        AppMetrics.MqttMessages.WithLabels(topic, "in").Inc();

        try
        {
            var json = JObject.Parse(payload);

            if (topic.Contains("/env/"))
            {
                var parts = topic.Split('/');
                string nodeId = parts.Length >= 4 ? parts[3] : "";
                string metric = parts.Length >= 5 ? parts[4] : "";

                // env 메시지를 보내는 노드가 RPi면 heartbeat 겸용 처리
                if (nodeId.Contains("rpi", StringComparison.OrdinalIgnoreCase))
                {
                    LastRpiHeartbeat = DateTime.UtcNow;
                    OnRpiHeartbeat?.Invoke();
                }

                var data = new SensorData
                {
                    Metric = metric,
                    Value = json["value"]?.Value<double>() ?? 0,
                    Unit = json["unit"]?.ToString() ?? "",
                    Quality = json["quality"]?.ToString() ?? "good",
                    GasStatus = json["status"]?.ToString(),
                    Source = json["source"]?.ToString(),
                    Seq = json["seq"]?.Value<long>() ?? 0,
                    Timestamp = json["ts"] != null
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)(json["ts"]!.Value<double>() * 1000)).UtcDateTime
                        : DateTime.UtcNow,
                    MqttTopic = topic
                };

                switch (metric)
                {
                    case "temperature": Temperature = data.Value; break;
                    case "humidity": Humidity = data.Value; break;
                    case "gas": GasLevel = data.Value; break;
                }

                OnSensorData?.Invoke(data);
            }
            else if (topic.EndsWith("/rpi/online"))
            {
                bool online = json["online"]?.Value<bool>() ?? false;
                OnRpiOnlineChanged?.Invoke(online);
                Log.Information("RPi online status: {Status}", online);
            }
            else if (topic.EndsWith("/rpi/heartbeat"))
            {
                LastRpiHeartbeat = DateTime.UtcNow;
                OnRpiHeartbeat?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MQTT message parse error on {Topic}", topic);
            OnMqttError?.Invoke($"Parse error on {topic}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce)
    {
        if (_client == null || !IsConnected) return;
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(qos)
                .WithRetainFlag(retain)
                .Build();
            await _client.EnqueueAsync(msg);
            AppMetrics.MqttMessages.WithLabels(topic, "out").Inc();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MQTT publish failed: {Topic}", topic);
        }
    }

    public Task PublishAlarmAsync(string alarmType, string severity, object details)
    {
        // Route to the correct alarm subtopic based on the alarm type
        var subtopic = alarmType switch
        {
            "temp_critical" or "humidity_critical" or "gas_critical"
                or "temp_warning" or "humidity_warning" or "gas_warning" => "environment",
            "arduino_timeout" or "arduino_disconnected"
                or "rpi_timeout" or "rpi_offline"
                or "mqtt_broker_disconnected" => "watchdog",
            "sorting_mismatch" or "sorting_timeout" => "sorting",
            _ => "safety"
        };

        var payload = JObject.FromObject(details);
        payload["type"] = alarmType;
        payload["severity"] = severity;
        payload["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return PublishAsync(
            $"{_cfg.Topics.AlarmPublishBase}/{subtopic}",
            payload.ToString(Newtonsoft.Json.Formatting.None),
            retain: false,
            qos: MqttQualityOfServiceLevel.AtLeastOnce);
    }

    public Task PublishInspectionResultAsync(ProductDecision d)
    {
        var payload = new JObject
        {
            ["product_id"]      = d.ProductId,
            ["correlation_id"]  = d.CorrelationId,
            ["verdict"]         = d.Verdict.ToString(),
            ["product_type"]    = d.ProductType,
            ["yolo"] = new JObject
            {
                ["class"]            = d.YoloClass,
                ["confidence"]       = Math.Round(d.YoloConfidence, 4),
                ["model_version"]    = d.ModelVersion,
                ["inference_time_ms"]= d.InferenceTimeMs,
            },
            ["opencv"] = new JObject
            {
                ["pin_count"]  = d.PinCount,
                ["blur_score"] = Math.Round(d.BlurScore, 1),
                ["roi_aligned"]= d.RoiAligned,
            },
            ["defect_detail"] = d.DefectDetail,
            // image_path 는 PC 로컬 경로 (예: images/20260428/PRD-xxx_cam1.jpg).
            // E(RPi)는 PC 파일시스템에 접근할 수 없으므로 image_path 만으로는 이미지를 볼 수 없음.
            // → image_data (base64 JPEG) 를 함께 포함해 E 가 직접 디코딩 가능하게 함.
            ["image_path"]    = d.Cam1ImagePath,
            ["environment"] = new JObject
            {
                ["temperature"] = d.EnvironmentTemp,
                ["humidity"]    = d.EnvironmentHumidity,
            },
            ["inspected_at"] = d.InspectedAt.ToString("O"),
            ["ts"]  = new DateTimeOffset(d.InspectedAt).ToUnixTimeSeconds(),
            ["seq"] = Interlocked.Increment(ref _publishSeq),
        };

        // 이미지 파일이 존재하면 base64 로 인코딩해서 페이로드에 포함
        // E 는 payload["image_data"] 를 base64 decode → JPEG 바이트로 바로 사용 가능
        // 일반 640×480 JPEG ≈ 30–60 KB → base64 ≈ 40–80 KB — Mosquitto 기본 1 MB 제한 내
        if (!string.IsNullOrEmpty(d.Cam1ImagePath) && File.Exists(d.Cam1ImagePath))
        {
            try
            {
                byte[] imgBytes = File.ReadAllBytes(d.Cam1ImagePath);
                payload["image_data"] = Convert.ToBase64String(imgBytes);
                payload["image_mime"] = "image/jpeg";
                payload["image_size"] = imgBytes.Length;   // E 가 크기 검증용으로 사용 가능
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MQTT image embed failed — sending path only: {Path}", d.Cam1ImagePath);
            }
        }

        return PublishAsync(
            _cfg.Topics.InspectionResultPublish,
            payload.ToString(Newtonsoft.Json.Formatting.None),
            retain: false,
            qos: MqttQualityOfServiceLevel.AtLeastOnce);
    }

    public Task PublishSortingResultAsync(ProductDecision d)
    {
        var payload = new JObject
        {
            ["product_id"]          = d.ProductId,
            ["correlation_id"]      = d.CorrelationId,
            ["expected_verdict"]    = d.Verdict.ToString(),
            ["verified"]            = d.Verified,
            ["verification_sensor"] = d.VerificationSensor,
            ["expected_sensor"]     = d.ExpectedSensor,
            ["sorted_at"] = (d.SortedAt ?? DateTime.UtcNow).ToString("O"),
            ["ts"]  = new DateTimeOffset(d.SortedAt ?? DateTime.UtcNow).ToUnixTimeSeconds(),
            ["seq"] = Interlocked.Increment(ref _publishSeq),
        };

        return PublishAsync(
            _cfg.Topics.SortingResultPublish,
            payload.ToString(Newtonsoft.Json.Formatting.None),
            retain: false,
            qos: MqttQualityOfServiceLevel.AtLeastOnce);
    }

    /// <summary>Publish PC state change retained (Appendix B: mfg/line1/status/pc/state).</summary>
    public Task PublishPcStateAsync(string state)
    {
        var payload = new JObject
        {
            ["state"] = state,
            ["ts"]    = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        return PublishAsync(
            $"{_cfg.Topics.StatusPcPublishBase}/state",
            payload.ToString(Newtonsoft.Json.Formatting.None),
            retain: true,
            qos: MqttQualityOfServiceLevel.AtMostOnce);
    }

    /// <summary>Publish cumulative production counts retained (Appendix B: mfg/line1/status/pc/production).</summary>
    public Task PublishPcProductionAsync(int total, int pass, int defect, int hold)
    {
        var payload = new JObject
        {
            ["total"]  = total,
            ["pass"]   = pass,
            ["defect"] = defect,
            ["hold"]   = hold,
            ["ts"]     = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        return PublishAsync(
            $"{_cfg.Topics.StatusPcPublishBase}/production",
            payload.ToString(Newtonsoft.Json.Formatting.None),
            retain: true,
            qos: MqttQualityOfServiceLevel.AtMostOnce);
    }

    public async Task StopAsync()
    {
        if (_client != null)
        {
            // Graceful NDEATH: explicitly publish offline before disconnecting
            try
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic($"{_cfg.Topics.StatusPcPublishBase}/online")
                    .WithPayload($"{{\"online\":false,\"node\":\"{_cfg.ClientId}\",\"ts\":{ts}}}")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(true)
                    .Build();
                await _client.EnqueueAsync(msg);
                await Task.Delay(250); // Allow message to flush before disconnect
            }
            catch { }

            await _client.StopAsync();
        }
    }
    private static X509Certificate2? LoadCustomCa(MqttConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.CaCertPath)) return null;

        string path = Path.IsPathRooted(cfg.CaCertPath)
            ? cfg.CaCertPath
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, cfg.CaCertPath);

        if (!File.Exists(path))
        {
            Log.Warning("CA cert file not found: {Path}", path);
            return null;
        }

        try { return new X509Certificate2(path); }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load CA cert from {Path}", path);
            return null;
        }
    }

    private static List<X509Certificate2> LoadClientCertificates(MqttConfig cfg)
    {
        var list = new List<X509Certificate2>();

        if (!string.IsNullOrEmpty(cfg.ClientCertPfxPath) && File.Exists(cfg.ClientCertPfxPath))
        {
            list.Add(new X509Certificate2(
                cfg.ClientCertPfxPath,
                cfg.ClientCertPfxPassword,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable));
            return list;
        }

        if (!string.IsNullOrEmpty(cfg.ClientCertPath) && File.Exists(cfg.ClientCertPath))
        {
            if (!string.IsNullOrEmpty(cfg.ClientKeyPath) && File.Exists(cfg.ClientKeyPath))
            {
                // CreateFromPemFile produces an ephemeral key on some .NET/OS combos that
                // causes the TLS handshake to fail when the key is used on a different thread.
                // PFX round-trip persists the key through the Windows key store, avoiding that.
                using var temp = X509Certificate2.CreateFromPemFile(cfg.ClientCertPath, cfg.ClientKeyPath);
                var pfxBytes = temp.Export(X509ContentType.Pfx);
                list.Add(new X509Certificate2(pfxBytes, "",
                    X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable));
            }
            else
                list.Add(new X509Certificate2(cfg.ClientCertPath));
        }

        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
    }
}
