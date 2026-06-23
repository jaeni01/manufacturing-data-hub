using MfgInspectionSystem.Observability;
using Serilog;

namespace MfgInspectionSystem.Core;

public enum SystemState { IDLE, RUNNING, PAUSED, EMERGENCY }

public class StateMachine
{
    private SystemState _state = SystemState.IDLE;
    private readonly object _lock = new();

    public event Action<SystemState, SystemState, string>? OnStateChanged;

    public SystemState CurrentState { get { lock (_lock) return _state; } }
    public bool IsOperational => CurrentState == SystemState.RUNNING;
    public DateTime LastTransition { get; private set; } = DateTime.UtcNow;
    public string LastReason { get; private set; } = "init";

    public bool TransitionTo(SystemState target, string reason)
    {
        lock (_lock)
        {
            // EMERGENCY can always be entered from any state
            if (target == SystemState.EMERGENCY)
            {
                if (_state == SystemState.EMERGENCY) return false; // already in emergency
                Apply(_state, target, reason);
                return true;
            }

            if (!IsValidTransition(_state, target))
            {
                Log.Warning("Invalid state transition: {From} -> {To} ({Reason})", _state, target, reason);
                return false;
            }

            Apply(_state, target, reason);
            return true;
        }
    }

    private void Apply(SystemState from, SystemState to, string reason)
    {
        _state = to;
        LastTransition = DateTime.UtcNow;
        LastReason = reason;
        Log.Information("State: {From} -> {To} | {Reason}", from, to, reason);
        foreach (SystemState state in Enum.GetValues<SystemState>())
            AppMetrics.SystemState.WithLabels(state.ToString()).Set(state == to ? 1 : 0);
        OnStateChanged?.Invoke(from, to, reason);
    }

    private static bool IsValidTransition(SystemState from, SystemState to) => (from, to) switch
    {
        (SystemState.IDLE, SystemState.RUNNING) => true,
        (SystemState.RUNNING, SystemState.PAUSED) => true,
        (SystemState.RUNNING, SystemState.IDLE) => true,
        (SystemState.PAUSED, SystemState.RUNNING) => true,
        (SystemState.PAUSED, SystemState.IDLE) => true,
        (SystemState.EMERGENCY, SystemState.IDLE) => true,
        _ => false
    };
}
