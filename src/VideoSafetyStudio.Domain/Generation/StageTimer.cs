using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoSafetyStudio.Domain.Generation;

/// <summary>Per-stage timing of one ComfyUI prompt, built from its websocket events. Pure logic: feed events, read results.</summary>
public sealed class StageTimer(JsonObject workflow)
{
    private sealed class NodeRecord(double start)
    {
        public double Start { get; } = start;
        public double? End { get; set; }
        public int Steps { get; set; }
        public int MaxSteps { get; set; }
    }

    private readonly Dictionary<string, NodeRecord> _records = [];
    private readonly HashSet<string> _cached = [];
    private string? _current;

    public double? Started { get; private set; }
    public double? Finished { get; private set; }
    public string? Outcome { get; private set; }
    public bool Done => Finished is not null;

    /// <summary>The live stage and sampler step, for progress events.</summary>
    public JobProgress? Progress =>
        _current is not null && _records.TryGetValue(_current, out var record)
            ? new JobProgress(Stages.Of(workflow, _current), record.Steps, record.MaxSteps)
            : null;

    public void Feed(string type, JsonElement data, double now)
    {
        switch (type)
        {
            case "execution_start":
                Started = now;
                break;
            case "execution_cached":
                if (data.TryGetProperty("nodes", out var nodes))
                {
                    foreach (var node in nodes.EnumerateArray())
                    {
                        _cached.Add(node.ToString());
                    }
                }

                break;
            case "executing":
                Close(now);
                if (data.TryGetProperty("node", out var current) && current.ValueKind != JsonValueKind.Null)
                {
                    _current = current.ToString();
                    _records[_current] = new NodeRecord(now);
                }
                else if (Started is not null)
                {
                    Finish(now, "success"); // older servers: node=null means the prompt finished
                }

                break;
            case "progress":
                var key = data.TryGetProperty("node", out var node2) && node2.ValueKind != JsonValueKind.Null ? node2.ToString() : _current;
                if (key is not null && _records.TryGetValue(key, out var record))
                {
                    record.Steps = data.TryGetProperty("value", out var value) ? value.GetInt32() : 0;
                    record.MaxSteps = data.TryGetProperty("max", out var max) ? max.GetInt32() : 0;
                }

                break;
            case "execution_success":
                Finish(now, "success");
                break;
            case "execution_error" or "execution_interrupted":
                Finish(now, "error");
                break;
        }
    }

    public void Finish(double now, string outcome)
    {
        Close(now);
        if (Finished is null)
        {
            Finished = now;
            Outcome = outcome;
        }
    }

    private void Close(double now)
    {
        if (_current is not null && _records.TryGetValue(_current, out var record) && record.End is null)
        {
            record.End = now;
        }

        _current = null;
    }

    /// <summary>(stage, node id, start, end) for every executed node, in order.</summary>
    public IReadOnlyList<(string Stage, string Node, double Start, double End)> Intervals() =>
        [.. _records.Select(pair => (Stages.Of(workflow, pair.Key), pair.Key, pair.Value.Start, pair.Value.End ?? pair.Value.Start))];

    public TimingSummary Summary()
    {
        var seconds = new Dictionary<string, double>();
        var nodesByStage = new Dictionary<string, List<string>>();
        var steps = new Dictionary<string, int>();
        var cachedOnly = new HashSet<string>();

        foreach (var (stage, node, start, end) in Intervals())
        {
            seconds[stage] = seconds.GetValueOrDefault(stage) + (end - start);
            nodesByStage.TryAdd(stage, []);
            nodesByStage[stage].Add(node);
            steps[stage] = steps.GetValueOrDefault(stage) + _records[node].Steps;
        }

        foreach (var node in _cached.Where(node => !_records.ContainsKey(node)))
        {
            var stage = Stages.Of(workflow, node);
            if (!nodesByStage.TryAdd(stage, [node]))
            {
                nodesByStage[stage].Add(node);
            }
            else
            {
                cachedOnly.Add(stage);
            }
        }

        var stages = nodesByStage.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var elapsed = Math.Round(seconds.GetValueOrDefault(pair.Key), 3);
                var stepCount = steps.GetValueOrDefault(pair.Key);
                return new StageSummary(elapsed, pair.Value, stepCount, cachedOnly.Contains(pair.Key),
                    stepCount > 0 ? Math.Round(elapsed / stepCount, 3) : null);
            });
        double? total = Started is not null && Finished is not null ? Math.Round(Finished.Value - Started.Value, 3) : null;
        return new TimingSummary(Outcome, total, stages);
    }
}
