// Offline health contract only. No Metasys HTTP writer or live notification implementation exists here.
public sealed class HealthPublishingOptions
{
    public bool Enabled { get; set; }
    public string RecipientOrigin { get; set; } = "";
    public List<HealthPointBinding> Points { get; set; } = [];
}
public sealed record HealthPointBinding(string Signal, string ObjectId, string ValueKind, string WriteMode, string? CommandId = null, string? Priority = null);
public sealed record MappingHealthSignal(string MappingId, bool SourceCommunicationHealthy, bool SourceQualityReported, bool SourceQualityReliable,
    bool DestinationConfirmed, bool DestinationQualityReported, bool DestinationQualityReliable, bool WorkerProgressing, long CompletedChecks, string Code);
public sealed record HealthFrame(int SchemaVersion, string RuntimeInstance, long ProcessSequence, DateTimeOffset CapturedAt,
    bool TransfersArmed, bool OverallHealthy, string Scope, MappingHealthSignal[] Mappings);

public sealed class HealthFrameProducer(TimeProvider clock)
{
    readonly string instance = Guid.NewGuid().ToString("N");
    long sequence;
    public HealthFrame Capture(bool armed, IEnumerable<MappingHealth> mappings, TimeSpan checkInterval, TimeSpan operationDeadline)
    {
        var now = clock.GetUtcNow();
        bool Recent(DateTimeOffset? t) => t is { } value && value <= now && now - value <= checkInterval + operationDeadline;
        var signals = mappings.Select(m =>
        {
            var inFlight = m.LastCheckStarted is { } started && (m.LastCheckCompleted == null || started > m.LastCheckCompleted);
            var progressing = m.CompletedChecks > 0 && m.State != "Stopped" && m.State != "Stalled" && Recent(m.LastCheckCompleted) &&
                (!inFlight || now - m.LastCheckStarted!.Value <= operationDeadline);
            var sourceQuality = m.SourceQuality == "reported-reliable";
            var destinationQuality = m.DestinationQuality == "reported-reliable";
            var sourceTime=m.LastSourceContact ?? m.LastSourceRead;
            var source = sourceTime is {} contacted && contacted<=now && now-contacted<=TimeSpan.FromSeconds(m.SourcePollIntervalSeconds)+operationDeadline &&
                (m.SourceMaximumAgeSeconds<=0 || m.SourceObservationAt is {} observed && observed<=now.AddSeconds(30) && now-observed<=TimeSpan.FromSeconds(m.SourceMaximumAgeSeconds)) &&
                (!m.FailureCode.StartsWith("source-",StringComparison.Ordinal)||m.FailureCode=="source-quality");
            var destination = Recent(m.LastDestinationConfirmed) &&
                !m.FailureCode.StartsWith("destination-", StringComparison.Ordinal) &&
                m.FailureCode is not ("readback-mismatch" or "prior-write-unconfirmed");
            var code = !progressing ? "worker-progress-unconfirmed" : !source ? "source-health-unconfirmed" : !sourceQuality ? "source-quality-unconfirmed" :
                !destination ? "destination-delivery-unconfirmed" : !destinationQuality ? "destination-quality-unconfirmed" : m.State != "Healthy" ? "mapping-operation-unhealthy" : "none";
            return new MappingHealthSignal(m.MappingId, source, m.SourceQuality is "reported-reliable" or "reported-unreliable", sourceQuality,
                destination, m.DestinationQuality is "reported-reliable" or "reported-unreliable", destinationQuality, progressing, m.CompletedChecks, code);
        }).ToArray();
        var healthy = armed && signals.Length > 0 && signals.All(x => x.SourceCommunicationHealthy && x.DestinationConfirmed && x.WorkerProgressing && x.Code == "none");
        return new(1, instance, Interlocked.Increment(ref sequence), now, armed, healthy,
            "communication-and-delivery-only; physical-observation-age-not-established", signals);
    }
}

public sealed record SimulatedHealthEnvelope(string RecipientOrigin, IReadOnlyList<HealthPointBinding> Points, HealthFrame Frame);
public interface ISimulatedHealthEndpoint
{
    Task<bool> AcceptAsync(SimulatedHealthEnvelope envelope, CancellationToken ct);
}
public static class SimulatedHealthPublisher
{
    public static async Task<string> PublishAsync(HealthPublishingOptions options, HealthFrame frame, ISimulatedHealthEndpoint endpoint, CancellationToken ct = default)
    {
        if (!options.Enabled) return "disabled";
        // Reserved .invalid destinations only. This path cannot be configured to metasys-lab/customer origins.
        if (!Uri.TryCreate(options.RecipientOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            !origin.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(origin.UserInfo) ||
            origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new InvalidOperationException("Only a reserved .invalid simulation recipient is allowed.");
        if (options.Points.Count == 0 || options.Points.Any(p => string.IsNullOrWhiteSpace(p.Signal) ||
            !Guid.TryParse(p.ObjectId, out _) || p.ValueKind is not ("boolean" or "counter") ||
            p.ValueKind != ExpectedKind(p.Signal, frame) || p.WriteMode != "simulation") ||
            options.Points.Select(p => p.ObjectId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Points.Count ||
            options.Points.Select(p => p.Signal).Distinct(StringComparer.Ordinal).Count() != options.Points.Count ||
            RequiredSignals(frame).Any(signal => !options.Points.Any(p => p.Signal == signal)))
            throw new InvalidOperationException("Simulation bindings must declare unique signals/IDs and boolean/counter kinds.");
        try { return await endpoint.AcceptAsync(new(options.RecipientOrigin, options.Points.ToArray(), frame), ct) ? "accepted-simulation" : "delivery-unconfirmed"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return "delivery-unconfirmed"; }
    }
    static string? ExpectedKind(string signal, HealthFrame frame)
    {
        if (signal == "process.sequence") return "counter";
        if (signal is "bridge.healthy" or "bridge.armed") return "boolean";
        foreach (var mapping in frame.Mappings)
        {
            if (signal == $"mapping:{mapping.MappingId}:completedChecks") return "counter";
            if (new[] { "sourceHealthy", "destinationConfirmed", "workerProgressing", "sourceQualityReported", "destinationQualityReported", "sourceQualityReliable", "destinationQualityReliable" }
                .Any(field => signal == $"mapping:{mapping.MappingId}:{field}")) return "boolean";
        }
        return null;
    }
    public static IEnumerable<string> RequiredSignals(HealthFrame frame)
    {
        yield return "process.sequence"; yield return "bridge.healthy"; yield return "bridge.armed";
        foreach (var mapping in frame.Mappings)
            foreach (var field in new[] { "sourceHealthy", "destinationConfirmed", "workerProgressing", "completedChecks", "sourceQualityReported", "destinationQualityReported", "sourceQualityReliable", "destinationQualityReliable" })
                yield return $"mapping:{mapping.MappingId}:{field}";
    }
}

public sealed class IndependentWatchdogOptions
{
    public TimeSpan MaxHeartbeatSilence { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan MaxWorkerProgressSilence { get; init; } = TimeSpan.FromSeconds(345);
    public string[] RequiredMappingIds { get; init; } = [];
}
public sealed record WatchdogDecision(bool Alarm, string Code, long Incident, bool NotificationPending,
    string NotificationState, int NotificationFailures);
public interface ISimulatedAlarmNotifications
{
    Task<bool> NotifyAsync(WatchdogDecision decision, CancellationToken ct);
}

// This consumer intentionally has no reference to Bridge or its process. A separately running
// Metasys-side implementation must supply its own timer and health-point observations.
public sealed class IndependentHealthWatchdog
{
    readonly TimeProvider clock;
    readonly IndependentWatchdogOptions options;
    HealthFrame? last;
    long? receivedAt;
    string code = "awaiting-heartbeat";
    bool alarm = true, pending;
    long incident;
    string notification = "not-attempted";
    int notificationFailures;
    Dictionary<string, long>? restartBaseline;
    readonly HashSet<string> retiredInstances = [];
    readonly Dictionary<string, (long Count, long AdvancedAt)> progress = [];
    readonly HashSet<string> regressed = [];
    public IndependentHealthWatchdog(TimeProvider clock, IndependentWatchdogOptions options)
    {
        if (options.MaxHeartbeatSilence <= TimeSpan.Zero || options.MaxWorkerProgressSilence <= TimeSpan.Zero || options.RequiredMappingIds.Length == 0 ||
            options.RequiredMappingIds.Any(string.IsNullOrWhiteSpace) || options.RequiredMappingIds.Distinct().Count() != options.RequiredMappingIds.Length)
            throw new ArgumentException("An independent watchdog needs expected mappings and a positive silence limit.");
        this.clock = clock; this.options = options;
    }
    public bool Receive(HealthFrame frame)
    {
        if (frame == null || frame.Mappings == null || frame.Mappings.Any(m => m == null || m.CompletedChecks < 0) ||
            frame.SchemaVersion != 1 || !Guid.TryParseExact(frame.RuntimeInstance, "N", out _) || frame.ProcessSequence < 1 ||
            frame.Mappings.Select(x => x.MappingId).Distinct().Count() != frame.Mappings.Length) return false;
        if (retiredInstances.Contains(frame.RuntimeInstance)) return false;
        if (last != null && frame.RuntimeInstance == last.RuntimeInstance && frame.ProcessSequence <= last.ProcessSequence) return false;
        if (last != null && frame.RuntimeInstance != last.RuntimeInstance)
        {
            retiredInstances.Add(last.RuntimeInstance);
            restartBaseline = frame.Mappings.ToDictionary(x => x.MappingId, x => x.CompletedChecks);
            progress.Clear(); regressed.Clear();
        }
        else if (restartBaseline != null && options.RequiredMappingIds.All(id =>
            frame.Mappings.Any(m => m.MappingId == id && restartBaseline.TryGetValue(id, out var count) && m.CompletedChecks > count)))
            restartBaseline = null;
        foreach (var mapping in frame.Mappings)
        {
            if (!progress.TryGetValue(mapping.MappingId, out var previous) || mapping.CompletedChecks > previous.Count)
            {
                progress[mapping.MappingId] = (mapping.CompletedChecks, clock.GetTimestamp());
                regressed.Remove(mapping.MappingId);
            }
            else if (mapping.CompletedChecks < previous.Count) regressed.Add(mapping.MappingId);
        }
        last = frame;
        receivedAt = clock.GetTimestamp(); // Local monotonic time; never trust sender wall time as a heartbeat.
        Evaluate();
        return true;
    }
    public WatchdogDecision Evaluate()
    {
        string next;
        if (receivedAt == null || last == null) next = "missing-heartbeat";
        else if (clock.GetElapsedTime(receivedAt.Value, clock.GetTimestamp()) > options.MaxHeartbeatSilence) next = "missing-heartbeat";
        else if (!options.RequiredMappingIds.All(id => last.Mappings.Any(m => m.MappingId == id))) next = "mapping-inventory-incomplete";
        else if (restartBaseline != null) next = "restart-awaiting-checks";
        else if (!last.TransfersArmed) next = "required-transfers-paused";
        else if (regressed.Count > 0) next = "worker-counter-regressed";
        else if (options.RequiredMappingIds.Any(id => !progress.TryGetValue(id, out var p) ||
            clock.GetElapsedTime(p.AdvancedAt, clock.GetTimestamp()) > options.MaxWorkerProgressSilence)) next = "worker-progress-expired";
        else if (last.Mappings.Any(m => !m.WorkerProgressing || m.CompletedChecks < 1)) next = "worker-stalled-or-unconfirmed";
        else if (last.Mappings.Any(m => !m.SourceCommunicationHealthy || !m.SourceQualityReported || !m.SourceQualityReliable)) next = "source-health-unconfirmed";
        else if (last.Mappings.Any(m => !m.DestinationConfirmed || !m.DestinationQualityReported || !m.DestinationQualityReliable)) next = "destination-delivery-unconfirmed";
        else if (!last.OverallHealthy || last.Mappings.Any(m => m.Code != "none")) next = "mapping-operation-unhealthy";
        else next = "healthy";
        var nextAlarm = next != "healthy";
        if (next != code)
        {
            // First successful baseline needs no notification. All faults and later recovery do.
            pending = nextAlarm || (incident > 0 && alarm);
            incident++; code = next; alarm = nextAlarm; notification = pending ? "pending" : "not-needed";
        }
        return new(alarm, code, incident, pending, notification, notificationFailures);
    }
    public async Task<WatchdogDecision> NotifySimulatedAsync(ISimulatedAlarmNotifications sink, CancellationToken ct = default)
    {
        var decision = Evaluate();
        if (!pending) return decision;
        bool delivered;
        try { delivered = await sink.NotifyAsync(decision, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { delivered = false; }
        if (incident != decision.Incident) return Evaluate(); // An old acknowledgment cannot clear a newer incident.
        if (delivered) { pending = false; notification = "acknowledged-simulation"; }
        else { notificationFailures++; notification = "delivery-unconfirmed"; }
        return new(alarm, code, incident, pending, notification, notificationFailures);
    }
}
