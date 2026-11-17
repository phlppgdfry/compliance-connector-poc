namespace ComplianceConnectors.Core.Simulation;

/// <summary>
/// Decorator for demos and tests: wraps a real reporter and can switch its regime "down" or add
/// latency, without touching the reporter itself. This is how the dashboard shows that one
/// regime's outage never affects another regime.
/// </summary>
public sealed class FaultInjectingReporter(IComplianceReporter inner) : IComplianceReporter
{
    public bool IsDown { get; set; }

    /// <summary>Extra delay per call, so humans can follow the fan-out in a UI.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    public string RegimeCode => inner.RegimeCode;

    public IComplianceReporter Inner => inner;

    public bool AppliesTo(SignedDocument document) => inner.AppliesTo(document);

    public async Task ReportAsync(SignedDocument document, CancellationToken cancellationToken)
    {
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, cancellationToken);
        if (IsDown) throw new HttpRequestException($"{RegimeCode} unavailable (simulated outage, 503)");
        await inner.ReportAsync(document, cancellationToken);
    }
}
