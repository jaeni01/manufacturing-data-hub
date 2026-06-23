using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using MfgInspectionSystem.Communication.Messages;
using MfgInspectionSystem.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace MfgInspectionSystem.Communication;

public class SerialManager : IDisposable
{
    private SerialPort? _port;
    private readonly SerialConfig _cfg;
    private readonly StringBuilder _rxBuffer = new();
    private readonly object _sendLock = new();
    private int _seqCounter;
    private bool _disposed;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingAcks = new();
    private readonly Channel<string> _cmdChannel = Channel.CreateUnbounded<string>();
    
    // Events
    public event Action<IrEvent>? OnIrEvent;
    public event Action<EstopEvent>? OnEstopTriggered;
    public event Action<HeartbeatEvent>? OnHeartbeatReceived;
    public event Action<string>? OnSerialError;
    public event Action? OnDisconnected;
    public event Action<string>? OnRawLine;

    public bool IsConnected => _port?.IsOpen == true;
    public DateTime LastHeartbeatTime { get; private set; } = DateTime.MinValue;

    public SerialManager(SerialConfig cfg) => _cfg = cfg;

    public bool Connect()
    {
        try
        {
            _port = new SerialPort(_cfg.PortName, _cfg.BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                NewLine = "\n",
                DtrEnable = true,
                RtsEnable = true
            };
            _port.DataReceived += Port_DataReceived;
            _port.ErrorReceived += (s, e) => OnSerialError?.Invoke($"Serial error: {e.EventType}");
            _port.Open();

            _ = Task.Run(SendLoop);

            Log.Information("Serial connected: {Port} @ {Baud}", _cfg.PortName, _cfg.BaudRate);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Serial connect failed: {Port}", _cfg.PortName);
            OnSerialError?.Invoke($"Connect failed: {ex.Message}");
            return false;
        }
    }

    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            string data = _port!.ReadExisting();
            _rxBuffer.Append(data);

            int idx;
            while ((idx = _rxBuffer.ToString().IndexOf('\n')) >= 0)
            {
                string line = _rxBuffer.ToString(0, idx).Trim();
                _rxBuffer.Remove(0, idx + 1);
                if (!string.IsNullOrEmpty(line))
                    ProcessLine(line);
            }
        }
        catch (Exception ex)
        {
            OnSerialError?.Invoke($"Read error: {ex.Message}");
        }
    }

    private void ProcessLine(string json)
    {
        OnRawLine?.Invoke(json);
        try
        {
            var jObj = JObject.Parse(json);
            string? evtType = jObj["evt"]?.ToString();
            if (evtType == null)
            {
                // Check for ACK
                if (jObj["ack"] != null)
                {
                    int seq = jObj["seq"]?.Value<int>() ?? jObj["ack"]?.Value<int>() ?? 0;
                    if (_pendingAcks.TryRemove(seq, out var tcs))
                        tcs.TrySetResult(true);
                }
                return;
            }

            switch (evtType)
            {
                case "ir":
                    OnIrEvent?.Invoke(new IrEvent
                    {
                        Sensor = jObj["sensor"]?.ToString() ?? "",
                        State = jObj["state"]?.ToString() ?? "",
                        Timestamp = jObj["ts"]?.Value<long>() ?? 0,
                        Sequence = jObj["seq"]?.Value<int>() ?? 0
                    });
                    break;

                case "estop":
                    OnEstopTriggered?.Invoke(new EstopEvent
                    {
                        Reason = jObj["reason"]?.ToString() ?? "hw_estop",
                        Timestamp = jObj["ts"]?.Value<long>() ?? 0
                    });
                    break;

                case "hb":
                    LastHeartbeatTime = DateTime.UtcNow;
                    OnHeartbeatReceived?.Invoke(new HeartbeatEvent
                    {
                        Uptime = jObj["uptime"]?.Value<long>() ?? jObj["ts"]?.Value<long>() ?? 0,
                        Sequence = jObj["seq"]?.Value<int>() ?? 0
                    });
                    break;

                case "ack":
                    int ackSeq = jObj["seq"]?.Value<int>() ?? 0;
                    if (_pendingAcks.TryRemove(ackSeq, out var ackTcs))
                        ackTcs.TrySetResult(true);
                    break;

                case "err":
                    OnSerialError?.Invoke($"Arduino error: {jObj["msg"]?.ToString()}");
                    break;

                case "boot":
                    Log.Information("Arduino boot event");
                    break;
            }
        }
        catch (JsonException ex)
        {
            Log.Warning("Invalid JSON from Arduino: {Line} ({Err})", json, ex.Message);
        }
    }

    private async Task SendLoop()
    {
        await foreach (var cmd in _cmdChannel.Reader.ReadAllAsync())
        {
            try
            {
                if (_port?.IsOpen == true)
                {
                    lock (_sendLock)
                        _port.WriteLine(cmd);
                    Log.Debug("Serial TX: {Cmd}", cmd);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Serial send failed: {Cmd}", cmd);
                OnSerialError?.Invoke($"Send failed: {ex.Message}");
            }
        }
    }

    public async Task<bool> SendCommandAsync(string cmd, string? target, int value,
        int retries = -1, int ackTimeoutMs = -1)
    {
        int maxRetries = retries < 0 ? _cfg.MaxRetries : retries;
        int timeout = ackTimeoutMs < 0 ? _cfg.AckTimeoutMs : ackTimeoutMs;
        int seq = Interlocked.Increment(ref _seqCounter);
        var command = new SerialCommand { Command = cmd, Target = target, Value = value, Sequence = seq };
        string json = JsonConvert.SerializeObject(command);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[seq] = tcs;

             await _cmdChannel.Writer.WriteAsync(json);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            if (completed == tcs.Task && tcs.Task.Result)
                return true;

            _pendingAcks.TryRemove(seq, out _);
            Log.Warning("ACK timeout attempt {A}/{M} for cmd={Cmd}", attempt + 1, maxRetries, cmd);
        }
        return false;
    }

    // Convenience helpers
    public Task<bool> SetMotor(int speed) => SendCommandAsync("motor", "conveyor", speed);
    public Task<bool> SetServoA(int angle) => SendCommandAsync("servo", "A", angle);
    public Task<bool> SetServoB(int angle) => SendCommandAsync("servo", "B", angle);
    public Task<bool> SetLed(string color, bool on) => SendCommandAsync("led", color, on ? 1 : 0);
    public Task<bool> SetBuzzer(bool on) => SendCommandAsync("buzzer", "main", on ? 1 : 0);
    public Task<bool> TriggerEstop() => SendCommandAsync("estop", null, 1, retries: 1);
    public Task<bool> ReleaseEstop() => SendCommandAsync("estop_release", null, 0, retries: 1);
    public Task<bool> Ping() => SendCommandAsync("ping", null, 0, retries: 1);

    public void Disconnect()
    {
        try { if (_port?.IsOpen == true) _port.Close(); }
        catch { }
        OnDisconnected?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cmdChannel.Writer.TryComplete();
        Disconnect();
        _port?.Dispose();
    }
}
