#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// Parses Stable-Baselines3 PPO output for training metrics.
/// </summary>
public class TrainingMetricsParser
{
    public struct Metric
    {
        public string Name;
        public float Value;
        public DateTime Timestamp;
    }

    readonly List<Metric> _rewardHistory = new();
    readonly List<Metric> _lossHistory = new();
    readonly List<Metric> _valueLossHistory = new();
    readonly List<Metric> _klHistory = new();
    readonly List<Metric> _episodeLengthHistory = new();

    public IReadOnlyList<Metric> RewardHistory => _rewardHistory;
    public IReadOnlyList<Metric> LossHistory => _lossHistory;
    public IReadOnlyList<Metric> ValueLossHistory => _valueLossHistory;
    public IReadOnlyList<Metric> KLHistory => _klHistory;
    public IReadOnlyList<Metric> EpisodeLengthHistory => _episodeLengthHistory;

    public float? LatestReward => _rewardHistory.Count > 0 ? _rewardHistory[^1].Value : null;
    public float? LatestLoss => _lossHistory.Count > 0 ? _lossHistory[^1].Value : null;
    public float? LatestValueLoss => _valueLossHistory.Count > 0 ? _valueLossHistory[^1].Value : null;
    public float? LatestKL => _klHistory.Count > 0 ? _klHistory[^1].Value : null;

    public event Action<string, float> OnMetricParsed;

    /// <summary>Fired for every <c>SHILATE-HEALTH &lt;signal&gt;</c> line emitted by train.py.</summary>
    public event Action<string> OnHealthMarker;

    // SB3 format: |    metric_name           | 123.45      |
    // More flexible pattern to handle various spacing
    static readonly Regex MetricPattern = new(
        @"\|\s*(\w+)\s*\|\s*([-+]?\d+\.?\d*(?:[eE][-+]?\d+)?)\s*\|",
        RegexOptions.Compiled);

    // SHILATE-METRIC key=value   |   SHILATE-HEALTH signal extra...
    static readonly Regex StructuredMetricPattern = new(
        @"^SHILATE-METRIC\s+(\w+)\s*=\s*([-+]?\d+\.?\d*(?:[eE][-+]?\d+)?)",
        RegexOptions.Compiled);

    static readonly Regex StructuredHealthPattern = new(
        @"^SHILATE-HEALTH\s+(.+)$",
        RegexOptions.Compiled);

    public void ParseLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        // Structured marker lines emitted directly by train.py — preferred path
        var healthMatch = StructuredHealthPattern.Match(line);
        if (healthMatch.Success)
        {
            OnHealthMarker?.Invoke(healthMatch.Groups[1].Value.Trim());
            return;
        }

        var structuredMetric = StructuredMetricPattern.Match(line);
        if (structuredMetric.Success)
        {
            string name = structuredMetric.Groups[1].Value;
            string valueStr = structuredMetric.Groups[2].Value;
            if (float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value))
            {
                RecordMetric(name, value);
            }
            return;
        }

        // Try to match SB3 table format
        var matches = MetricPattern.Matches(line);
        foreach (Match match in matches)
        {
            string name = match.Groups[1].Value;
            string valueStr = match.Groups[2].Value;

            if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value))
                continue;

            RecordMetric(name, value);
        }
    }

    void RecordMetric(string name, float value)
    {
        var metric = new Metric
        {
            Name = name,
            Value = value,
            Timestamp = DateTime.Now,
        };

        switch (name)
        {
            case "ep_rew_mean":
                AddMetric(_rewardHistory, metric);
                OnMetricParsed?.Invoke(name, value);
                break;
            case "ep_len_mean":
                AddMetric(_episodeLengthHistory, metric);
                OnMetricParsed?.Invoke(name, value);
                break;
            case "loss":
            case "policy_loss":
            case "policy_gradient_loss":
                AddMetric(_lossHistory, metric);
                OnMetricParsed?.Invoke(name, value);
                break;
            case "value_loss":
                AddMetric(_valueLossHistory, metric);
                OnMetricParsed?.Invoke(name, value);
                break;
            case "approx_kl":
                AddMetric(_klHistory, metric);
                OnMetricParsed?.Invoke(name, value);
                break;
        }
    }

    void AddMetric(List<Metric> list, Metric metric)
    {
        list.Add(metric);
        if (list.Count > 500)
            list.RemoveAt(0);
    }

    public void Clear()
    {
        _rewardHistory.Clear();
        _lossHistory.Clear();
        _valueLossHistory.Clear();
        _klHistory.Clear();
        _episodeLengthHistory.Clear();
    }

    public (float min, float max) GetRange(IReadOnlyList<Metric> metrics)
    {
        if (metrics.Count == 0) return (0, 1);

        float min = float.MaxValue;
        float max = float.MinValue;

        foreach (var m in metrics)
        {
            if (m.Value < min) min = m.Value;
            if (m.Value > max) max = m.Value;
        }

        if (Math.Abs(max - min) < 0.0001f)
        {
            min -= 0.5f;
            max += 0.5f;
        }

        return (min, max);
    }
}
#endif
