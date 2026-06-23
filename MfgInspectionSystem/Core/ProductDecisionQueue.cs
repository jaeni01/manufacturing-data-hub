using System.Collections.Concurrent;
using MfgInspectionSystem.Models;
using MfgInspectionSystem.Observability;
using Serilog;

namespace MfgInspectionSystem.Core;

public class ProductDecisionQueue
{
    private readonly ConcurrentQueue<ProductDecision> _queue = new();
    private readonly object _statsLock = new();
    private const int MaxDepth = 10;

    public event Action<ProductDecision>? OnEnqueued;
    public event Action<ProductDecision>? OnDequeued;

    public int TotalInspected { get; private set; }
    public int PassCount { get; private set; }
    public int DefectCount { get; private set; }
    public int HoldCount { get; private set; }
    public int CurrentDepth => _queue.Count;

    public bool Enqueue(ProductDecision decision)
    {
        if (_queue.Count >= MaxDepth)
        {
            Log.Warning("ProductDecisionQueue overflow! Depth={Depth}", _queue.Count);
            return false;
        }

        _queue.Enqueue(decision);

        lock (_statsLock)
        {
            TotalInspected++;
            switch (decision.Verdict)
            {
                case Verdict.PASS: PassCount++; break;
                case Verdict.DEFECT: DefectCount++; break;
                case Verdict.HOLD: HoldCount++; break;
            }
        }

        OnEnqueued?.Invoke(decision);
        AppMetrics.QueueDepth.Set(_queue.Count);
        Log.Debug("Queue enqueue: {Id} -> {Verdict} (depth={D})", decision.ProductId, decision.Verdict, _queue.Count);
        return true;
    }

    public ProductDecision? Dequeue()
    {
        if (_queue.TryDequeue(out var decision))
        {
            OnDequeued?.Invoke(decision);
            AppMetrics.QueueDepth.Set(_queue.Count);
            Log.Debug("Queue dequeue: {Id} (depth={D})", decision.ProductId, _queue.Count);
            return decision;
        }
        return null;
    }

    public bool TryDequeue(out ProductDecision? decision)
    {
        if (_queue.TryDequeue(out decision))
        {
            OnDequeued?.Invoke(decision);
            AppMetrics.QueueDepth.Set(_queue.Count);
            return true;
        }

        return false;
    }

    public ProductDecision[] PeekAll() => _queue.ToArray();

    public void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
        AppMetrics.QueueDepth.Set(0);
        Log.Information("ProductDecisionQueue cleared");
    }

    public void ResetStats()
    {
        lock (_statsLock)
        {
            TotalInspected = 0;
            PassCount = 0;
            DefectCount = 0;
            HoldCount = 0;
        }
    }
}
