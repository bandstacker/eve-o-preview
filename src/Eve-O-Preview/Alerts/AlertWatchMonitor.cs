using System;
using System.Collections.Generic;

namespace EveOPreview.Alerts;

public enum AlertEventKind
{
    /// <summary>More non-friendly standing icons than before (including ones present when watching starts).</summary>
    HostileArrived,
    /// <summary>The member count rose without a matching new icon (a pilot EVE-O cannot see).</summary>
    UnknownArrived,
    Reminder,
    AllClear,
    Paused,
    Resumed,
}

public sealed record AlertEvent(AlertEventKind Kind, int Arrivals, string Detail);

/// <summary>One scan of a watch. <paramref name="Scan"/> is null unless the sample was live.</summary>
public sealed record AlertObservation(AlertSampleState State, StandingScan Scan, int? Count);

/// <summary>
/// The alarm rules for one watch, free of capture and output so they can be tested directly.
/// Readings must hold for <see cref="StableScans"/> scans before they count, so a single misread
/// frame or a list redraw never alarms.
/// </summary>
public sealed class AlertWatchMonitor
{
    public const int StableScans = 2;
    /// <summary>How long a count increase waits for a new icon (in the same or a later scan) to explain it.</summary>
    public static readonly TimeSpan CountGrace = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan ClearDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PauseAfter = TimeSpan.FromSeconds(5);

    private readonly List<(int Amount, DateTimeOffset Expires)> _credits = new();
    private (int Value, int Streak) _hostileCandidate = (-1, 0), _friendlyCandidate = (-1, 0), _countCandidate = (-1, 0);
    private bool _iconsKnown;
    private int _pending;
    private DateTimeOffset _pendingDeadline;
    private DateTimeOffset? _inactiveSince;
    private DateTimeOffset _lastSound, _lastLive;

    public AlertWatchMonitor(DateTimeOffset started) => _lastLive = started;

    public int Hostile { get; private set; }
    public int Friendly { get; private set; }
    public int? Count { get; private set; }
    public int Unexplained { get; private set; }
    public bool IsPaused { get; private set; }
    public bool HasLiveFrame { get; private set; }
    /// <summary>Someone non-friendly (or unidentified) is in Local.</summary>
    public bool IsActive => Hostile > 0 || Unexplained > 0;
    /// <summary>An alarm was raised and Local has not been clear long enough to send the all-clear.</summary>
    public bool IsAlarmed { get; private set; }
    public bool IsAcknowledged { get; private set; }
    public StandingScan LastScan { get; private set; } = StandingScan.Empty;

    /// <summary>Silences reminders until the next arrival.</summary>
    public void Acknowledge() { if (IsAlarmed) IsAcknowledged = true; }

    public IReadOnlyList<AlertEvent> Update(AlertObservation observation, DateTimeOffset now, int reminderSeconds)
    {
        var events = new List<AlertEvent>();
        if (observation.State != AlertSampleState.Live || observation.Scan is null)
        {
            if (!IsPaused && now - _lastLive >= PauseAfter)
            {
                IsPaused = true;
                events.Add(new(AlertEventKind.Paused, 0, observation.State == AlertSampleState.Stale
                    ? "The client draws nothing (hidden or minimized)." : "The client cannot be captured."));
            }
        }
        else
        {
            _lastLive = now;
            HasLiveFrame = true;
            if (IsPaused) { IsPaused = false; events.Add(new(AlertEventKind.Resumed, 0, "")); }
            LastScan = observation.Scan;
            Observe(observation.Scan, observation.Count, now, events);
        }

        bool arrived = events.Exists(e => e.Kind is AlertEventKind.HostileArrived or AlertEventKind.UnknownArrived);
        if (arrived)
        {
            IsAlarmed = true;
            IsAcknowledged = false;
            _lastSound = now;
        }
        if (IsActive) _inactiveSince = null;
        else if (IsAlarmed)
        {
            _inactiveSince ??= now;
            if (now - _inactiveSince.Value >= ClearDelay)
            {
                IsAlarmed = false;
                IsAcknowledged = false;
                _inactiveSince = null;
                events.Add(new(AlertEventKind.AllClear, 0, ""));
            }
        }
        if (!arrived && IsActive && IsAlarmed && !IsAcknowledged && reminderSeconds > 0
            && now - _lastSound >= TimeSpan.FromSeconds(reminderSeconds))
        {
            _lastSound = now;
            events.Add(new(AlertEventKind.Reminder, 0, Describe()));
        }
        return events;
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (Hostile > 0) parts.Add(LastScan.DescribeHostile() is { Length: > 0 } icons ? icons : $"{Hostile} non-friendly");
        if (Unexplained > 0) parts.Add($"{Unexplained} unidentified");
        if (Count is { } count) parts.Add($"Local {count}");
        return string.Join(" · ", parts);
    }

    private void Observe(StandingScan scan, int? count, DateTimeOffset now, List<AlertEvent> events)
    {
        _credits.RemoveAll(c => c.Expires <= now);
        bool hostileChanged = Stable(ref _hostileCandidate, scan.Hostile, out int hostile);
        bool friendlyChanged = Stable(ref _friendlyCandidate, scan.Friendly, out int friendly);
        if (!_iconsKnown)
        {
            // The first stable reading: anyone non-friendly already present alarms; friendlies are the baseline.
            if (_hostileCandidate.Streak < StableScans || _friendlyCandidate.Streak < StableScans) return;
            _iconsKnown = true;
            Hostile = hostile; Friendly = friendly;
            if (Hostile > 0) events.Add(new(AlertEventKind.HostileArrived, Hostile, scan.DescribeHostile()));
        }
        else
        {
            if (hostileChanged && hostile != Hostile)
            {
                if (hostile > Hostile)
                {
                    events.Add(new(AlertEventKind.HostileArrived, hostile - Hostile, scan.DescribeHostile()));
                    _credits.Add((hostile - Hostile, now + CountGrace));
                }
                Hostile = hostile;
            }
            if (friendlyChanged && friendly != Friendly)
            {
                if (friendly > Friendly) _credits.Add((friendly - Friendly, now + CountGrace));
                Friendly = friendly;
            }
        }

        if (count is { } read && read is >= 0 and < 10000 && Stable(ref _countCandidate, read, out int stableCount))
        {
            if (Count is not { } previous) Count = stableCount; // The first reading is the baseline.
            else if (stableCount != previous)
            {
                if (stableCount > previous)
                {
                    if (_pending == 0) _pendingDeadline = now + CountGrace;
                    _pending += stableCount - previous;
                }
                else
                {
                    int left = previous - stableCount, fromPending = Math.Min(left, _pending);
                    _pending -= fromPending;
                    Unexplained = Math.Max(0, Unexplained - (left - fromPending));
                }
                Count = stableCount;
            }
        }
        // New icons (just before or after the count rose) explain the arrival.
        for (int i = 0; i < _credits.Count && _pending > 0; i++)
        {
            int used = Math.Min(_pending, _credits[i].Amount);
            _pending -= used;
            _credits[i] = (_credits[i].Amount - used, _credits[i].Expires);
        }
        _credits.RemoveAll(c => c.Amount <= 0);
        if (_pending > 0 && now >= _pendingDeadline)
        {
            Unexplained += _pending;
            events.Add(new(AlertEventKind.UnknownArrived, _pending, $"Local rose to {Count}"));
            _pending = 0;
        }
    }

    /// <summary>True when <paramref name="value"/> has just held for <see cref="StableScans"/> scans.</summary>
    private static bool Stable(ref (int Value, int Streak) candidate, int value, out int stable)
    {
        candidate = candidate.Value == value ? (value, candidate.Streak + 1) : (value, 1);
        stable = value;
        return candidate.Streak == StableScans;
    }
}
