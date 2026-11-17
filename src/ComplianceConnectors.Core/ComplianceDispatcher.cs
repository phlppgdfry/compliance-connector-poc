using Polly;
using Polly.Retry;

namespace ComplianceConnectors.Core;

public enum ReportStatus
{
    NotApplicable,
    Delivered,
    DeadLettered
}

public sealed record ReportOutcome(string RegimeCode, ReportStatus Status, int Attempts, string? Error = null);

/// <summary>
/// Raised for every single delivery attempt, so a UI or a log can show the retry story as it
/// happens instead of only the final outcome.
/// </summary>
public sealed record AttemptEvent(string DocumentId, string RegimeCode, int Attempt, bool Succeeded, string? Error, bool WillRetry);

/// <summary>
/// Stands in for the "Azure Service Bus topic" from the proposal: one document.signed event
/// fans out to every applicable reporter. Each reporter gets its own retry policy and failure
/// isolation, so a slow or flaky government API for one country can never block another country,
/// or the document flow itself.
/// </summary>
public sealed class ComplianceDispatcher
{
    private readonly IReadOnlyList<IComplianceReporter> _reporters;
    private readonly ResiliencePipeline _retryPipeline;
    private readonly Action<string>? _log;
    private readonly Action<AttemptEvent>? _onAttempt;
    private readonly int _maxRetries;

    public ComplianceDispatcher(
        IEnumerable<IComplianceReporter> reporters,
        Action<string>? log = null,
        int maxRetries = 3,
        TimeSpan? retryDelay = null,
        Action<AttemptEvent>? onAttempt = null)
    {
        _reporters = reporters.ToList();
        _log = log;
        _onAttempt = onAttempt;
        _maxRetries = maxRetries;
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxRetries,
                Delay = retryDelay ?? TimeSpan.FromMilliseconds(50),
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = args =>
                {
                    _log?.Invoke($"    retry {args.AttemptNumber + 1}/{maxRetries} after: {args.Outcome.Exception?.Message}");
                    return default;
                }
            })
            .Build();
    }

    public IReadOnlyList<string> RegimeCodes => _reporters.Select(r => r.RegimeCode).ToList();

    public async Task<IReadOnlyList<ReportOutcome>> DispatchAsync(SignedDocument document, CancellationToken cancellationToken = default)
    {
        var tasks = _reporters.Select(reporter => HandleOneAsync(reporter, document, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Re-offers a dead-lettered report to one regime only. The other regimes already have their
    /// report; sending it again would create duplicates on their side.
    /// </summary>
    public Task<ReportOutcome> RedeliverAsync(SignedDocument document, string regimeCode, CancellationToken cancellationToken = default)
    {
        var reporter = _reporters.SingleOrDefault(r => r.RegimeCode == regimeCode)
            ?? throw new ArgumentException($"Unknown regime '{regimeCode}'.", nameof(regimeCode));
        return HandleOneAsync(reporter, document, cancellationToken);
    }

    private async Task<ReportOutcome> HandleOneAsync(IComplianceReporter reporter, SignedDocument document, CancellationToken ct)
    {
        if (!reporter.AppliesTo(document))
            return new ReportOutcome(reporter.RegimeCode, ReportStatus.NotApplicable, 0);

        var attempts = 0;
        try
        {
            await _retryPipeline.ExecuteAsync(async token =>
            {
                attempts++;
                try
                {
                    await reporter.ReportAsync(document, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _onAttempt?.Invoke(new AttemptEvent(document.DocumentId, reporter.RegimeCode, attempts, false, ex.Message, attempts <= _maxRetries));
                    throw;
                }
                _onAttempt?.Invoke(new AttemptEvent(document.DocumentId, reporter.RegimeCode, attempts, true, null, false));
            }, ct);

            _log?.Invoke($"  [{reporter.RegimeCode}] delivered ({attempts} attempt{(attempts == 1 ? "" : "s")})");
            return new ReportOutcome(reporter.RegimeCode, ReportStatus.Delivered, attempts);
        }
        catch (Exception ex)
        {
            // Exhausted retries: the document itself stays valid — the report goes to a
            // dead-letter queue for follow-up instead of blocking the transport.
            _log?.Invoke($"  [{reporter.RegimeCode}] DEAD-LETTERED after {attempts} attempts: {ex.Message}");
            return new ReportOutcome(reporter.RegimeCode, ReportStatus.DeadLettered, attempts, ex.Message);
        }
    }
}
