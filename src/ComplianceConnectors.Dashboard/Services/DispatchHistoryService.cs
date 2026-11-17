using System.Collections.Immutable;
using ComplianceConnectors.Core;
using ComplianceConnectors.Core.Reporters;
using ComplianceConnectors.Core.Simulation;

namespace ComplianceConnectors.Dashboard.Services;

public sealed record DispatchRecord(SignedDocument Document, IReadOnlyList<ReportOutcome> Outcomes, DateTimeOffset DispatchedAtUtc);

public sealed record DeadLetter(SignedDocument Document, string RegimeCode, int Attempts, string? Error, DateTimeOffset AtUtc);

/// <summary>Live view of one regime while a document is being dispatched.</summary>
public sealed class Lane(string regimeCode, string mechanism)
{
    public string RegimeCode { get; } = regimeCode;
    public string Mechanism { get; } = mechanism;
    // Swapped atomically, so the UI thread never enumerates a list that a retry is appending to.
    public ImmutableList<AttemptEvent> Attempts { get; set; } = [];
    public ReportStatus? Final { get; set; }
    public bool Active { get; set; }

    /// <summary>Outcome as soon as this lane knows it, without waiting for the slowest regime.</summary>
    public ReportStatus? Settled =>
        Final ?? (Attempts.Any(a => a.Succeeded) ? ReportStatus.Delivered
            : Attempts.Any(a => !a.Succeeded && !a.WillRetry) ? ReportStatus.DeadLettered
            : null);
}

/// <summary>
/// One instance per browser session (scoped). Wraps the same ComplianceDispatcher as the console
/// demo, adds per-regime fault injection, a live attempt feed, a dead-letter queue and replay.
/// </summary>
public sealed class DispatchHistoryService
{
    private readonly ComplianceDispatcher _dispatcher;
    private readonly Dictionary<string, FaultInjectingReporter> _reporters;
    private readonly List<DispatchRecord> _history = [];
    private readonly List<DeadLetter> _deadLetters = [];
    private readonly object _gate = new();
    private int _counter;

    public DispatchHistoryService()
    {
        var reporters = new[]
        {
            new FaultInjectingReporter(new MatisReporter()),
            new FaultInjectingReporter(new DiwassReporter()),
            new FaultInjectingReporter(new DecaReporter()),
        };
        foreach (var r in reporters) r.Latency = TimeSpan.FromMilliseconds(450);
        _reporters = reporters.ToDictionary(r => r.RegimeCode);

        Lanes =
        [
            new Lane("BE-MATIS", "CSV-export (OVAM)"),
            new Lane("EU-DIWASS", "EU-API"),
            new Lane("ES-DECA", "Nationale API"),
        ];

        // Slow enough to follow with your eyes: 0.5 s, 1 s, 2 s between attempts.
        _dispatcher = new ComplianceDispatcher(reporters, maxRetries: 3, retryDelay: TimeSpan.FromMilliseconds(500), onAttempt: OnAttempt);
    }

    public IReadOnlyList<Lane> Lanes { get; }
    // Snapshots: the UI renders on its own thread while dispatches complete on others.
    public IReadOnlyList<DispatchRecord> History { get { lock (_gate) return _history.ToList(); } }
    public IReadOnlyList<DeadLetter> DeadLetters { get { lock (_gate) return _deadLetters.ToList(); } }
    public SignedDocument? Current { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsReplay { get; private set; }

    /// <summary>Raised from background threads: subscribers must marshal to their own context.</summary>
    public event Action? OnChange;

    public bool IsDown(string regime) => _reporters[regime].IsDown;

    public void SetDown(string regime, bool down)
    {
        _reporters[regime].IsDown = down;
        OnChange?.Invoke();
    }

    public async Task DispatchAsync(string origin, string destination, bool waste)
    {
        var document = new SignedDocument($"DOC-{Interlocked.Increment(ref _counter):000}", DocumentType.AnnexVii, origin, destination, waste, DateTimeOffset.UtcNow);
        StartRun(document);
        IsReplay = false;
        foreach (var lane in Lanes) lane.Active = _reporters[lane.RegimeCode].AppliesTo(document);
        OnChange?.Invoke();

        var outcomes = await _dispatcher.DispatchAsync(document);
        lock (_gate)
        {
            foreach (var o in outcomes) Lanes.Single(l => l.RegimeCode == o.RegimeCode).Final = o.Status;
            _history.Insert(0, new DispatchRecord(document, outcomes, DateTimeOffset.UtcNow));
            foreach (var o in outcomes.Where(o => o.Status == ReportStatus.DeadLettered))
                _deadLetters.Insert(0, new DeadLetter(document, o.RegimeCode, o.Attempts, o.Error, DateTimeOffset.UtcNow));
        }
        IsBusy = false;
        OnChange?.Invoke();
    }

    public async Task RedeliverAsync(DeadLetter letter)
    {
        StartRun(letter.Document);
        IsReplay = true;
        foreach (var lane in Lanes) lane.Active = lane.RegimeCode == letter.RegimeCode;
        OnChange?.Invoke();

        var outcome = await _dispatcher.RedeliverAsync(letter.Document, letter.RegimeCode);
        lock (_gate)
        {
            Lanes.Single(l => l.RegimeCode == outcome.RegimeCode).Final = outcome.Status;
            _deadLetters.Remove(letter);
            if (outcome.Status == ReportStatus.DeadLettered)
                _deadLetters.Insert(0, letter with { Attempts = letter.Attempts + outcome.Attempts, Error = outcome.Error, AtUtc = DateTimeOffset.UtcNow });
            _history.Insert(0, new DispatchRecord(letter.Document, [outcome], DateTimeOffset.UtcNow));
        }
        IsBusy = false;
        OnChange?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _history.Clear();
            _deadLetters.Clear();
        }
        foreach (var lane in Lanes) { lane.Attempts = []; lane.Final = null; lane.Active = false; }
        Current = null;
        OnChange?.Invoke();
    }

    private void StartRun(SignedDocument document)
    {
        IsBusy = true;
        Current = document;
        foreach (var lane in Lanes)
        {
            lane.Attempts = [];
            lane.Final = null;
        }
    }

    private void OnAttempt(AttemptEvent e)
    {
        var lane = Lanes.Single(l => l.RegimeCode == e.RegimeCode);
        lock (_gate) lane.Attempts = lane.Attempts.Add(e);
        OnChange?.Invoke();
    }

    /// <summary>Per-regime counters across the session, for the summary cards.</summary>
    public IReadOnlyList<RegimeSummary> Summaries
    {
        get
        {
            lock (_gate)
            {
                return _history
                    .SelectMany(r => r.Outcomes)
                    .Where(o => o.Status != ReportStatus.NotApplicable)
                    .GroupBy(o => o.RegimeCode)
                    .Select(g => new RegimeSummary(
                        g.Key,
                        g.Count(o => o.Status == ReportStatus.Delivered),
                        g.Count(o => o.Status == ReportStatus.DeadLettered),
                        g.Sum(o => o.Attempts)))
                    .OrderBy(s => s.RegimeCode)
                    .ToList();
            }
        }
    }
}

public sealed record RegimeSummary(string RegimeCode, int Delivered, int DeadLettered, int TotalAttempts);
