using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text.Json.Nodes;
public class Bridge(Store store) : BackgroundService
{
    readonly object stateLock = new();
    readonly SemaphoreSlim controls = new(1);
    readonly SemaphoreSlim running = new(1);
    bool armed = store.Config.TransfersEnabled;
    CancellationTokenSource? run;
    // Defaults are communication checks, never a deadline since the last value change.
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan OperationDeadline { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxConcurrentChecks { get; init; } = 8;
    readonly RecoveryJournal journal=new(store.DirectoryPath);
    public object RecoveryStatus => journal.Summary();
    readonly ConcurrentDictionary<string, MappingHealth> health = new();
    ExternalJsonPool? sharedExternalPool;
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);sharedExternalPool?.Dispose();sharedExternalPool=null;journal.Dispose();
    }
    public override void Dispose(){base.Dispose();journal.Dispose();}
    public MappingHealth[] Health => health.Values.Select(x =>
        x.State != "Stopped" && x.LastCheckStarted is { } started &&
        ((x.LastCheckCompleted == null || started > x.LastCheckCompleted)
            ? DateTimeOffset.UtcNow - started > OperationDeadline
            : DateTimeOffset.UtcNow - x.LastCheckCompleted > OperationDeadline + HealthCheckInterval)
            ? x with { State = "Stalled", FailureCode = "progress-deadline" }
            : x.State!="Stopped" && x.SourceMaximumAgeSeconds>0 && x.SourceObservationAt is {} observed && DateTimeOffset.UtcNow-observed>TimeSpan.FromSeconds(x.SourceMaximumAgeSeconds)
                ? x with{State="SourceUnhealthy",FailureCode="source-stale-observation"}:x with { }).ToArray();
    public bool Armed { get { lock (stateLock) return armed; } }
    public string Status { get; private set; } = "Writes disarmed";
    readonly ActivityLog audit = new(store.DirectoryPath);
    public System.Collections.Concurrent.ConcurrentQueue<AuditEntry> Logs => audit.Entries;
    public async Task Configure(Func<Task> action)
    {
        await controls.WaitAsync();
        try
        {
            await StopRun();
            sharedExternalPool?.Dispose();sharedExternalPool=null;
            health.Clear();
            await store.Update(next => next.TransfersEnabled = false);
            await action();
        }
        finally { controls.Release(); }
    }
    public async Task SetArmed(bool value)
    {
        await controls.WaitAsync();
        try
        {
            if (!value)
            {
                await StopRun();
                await store.Update(next => next.TransfersEnabled = false);
            }
            else
            {
                if (Armed) return;
                if (!store.Config.Mappings.Any(x => x.Enabled)) throw new Exception("Enable at least one mapping.");
                var externalPool=sharedExternalPool??=new ExternalJsonPool(store);
                var destinations = new HashSet<string>();
                foreach (var mapping in store.Config.Mappings.Where(x => x.Enabled))
                {
                    var validation = await store.ValidateMapping(mapping, CancellationToken.None,externalPool);
                    if (!validation.Valid) throw new InvalidOperationException($"{mapping.Name}: " + string.Join(" ", validation.Errors));
                    if (!destinations.Add(DestinationKey(mapping,validation.Destination!.Id))) throw new InvalidOperationException("Multiple enabled mappings target the same destination point.");
                }
                await store.Update(next => next.TransfersEnabled = true);
                lock (stateLock) armed = true;
                Log("info", "Writes armed by operator. Initial current values will be synchronized.");
            }
        }
        finally { controls.Release(); }
    }
    async Task StopRun()
    {
        lock (stateLock) { armed = false; run?.Cancel(); }
        // Wait for the entire old run, including HTTP writes/readbacks, to unwind before configuration changes are committed.
        await running.WaitAsync(); running.Release();
        Status = "Writes disarmed";
    }
    void Log(string level, string message)
    {
        audit.Add(level, message);
    }
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (!Armed) { await Task.Delay(500, stop); continue; }
            await running.WaitAsync(stop);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
            try
            {
                lock (stateLock) { if (!armed) continue; run = linked; }
                await Run(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Status = exception is MetasysRequestException ? "Disconnected: " + exception.Message + " Retrying in 10 seconds." : "Disconnected - retrying in 10 seconds";
                Log("error", "stage=stream-or-preflight; code=connection-failed; retrySeconds=10");
                foreach (var mapping in store.Config.Mappings.Where(x => x.Enabled)) mapping.Status = "Disconnected — value may be stale";
                try { await Task.Delay(10000, linked.Token); } catch (OperationCanceledException) { }
            }
            finally { lock (stateLock) run = null; running.Release(); }
        }
    }
    string DestinationKey(Mapping m,string id)=>store.Get(Store.DestinationSide(m)).BaseUrl.TrimEnd('/').ToLowerInvariant()+"|"+id.ToLowerInvariant();
    async Task Run(CancellationToken ct)
    {
        var externalPool = sharedExternalPool??=new ExternalJsonPool(store);
        var resolved = new List<(Mapping m, string source, string destination, ValidationResult validation)>();
        foreach (var m in store.Config.Mappings.Where(x => x.Enabled).ToArray())
        {
            m.Status = "Resolving";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(OperationDeadline);
            store.CheckFeatures(m);
            using var dst=store.Client(Store.DestinationSide(m));
            ValidationResult validation;
            if(m.SourceKind=="json") validation=await Preflight.ValidatePoint(m,Store.ExternalPoint(m,await externalPool.Get(m.ExternalSourceId).Read(deadline.Token)),dst,deadline.Token);
            else { using var src=store.Client("source");validation=await Preflight.Validate(m,src,dst,deadline.Token); }
            if (!validation.Valid) throw new InvalidOperationException("Mapping preflight failed.");
            resolved.Add((m, validation.Source!.Id, validation.Destination!.Id, validation));
        }
        if (resolved.GroupBy(x => DestinationKey(x.m,x.destination)).Any(g => g.Count() > 1)) throw new Exception("Multiple enabled mappings target the same destination point.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var checkSlots = new SemaphoreSlim(MaxConcurrentChecks);
        var token = lifetime.Token;
        // Only invalidations are coalesced. Unproven event timestamps/values are never written.
        var queues = resolved.ToDictionary(x => x.m.Id, x => Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }));
        var ready = false;
        var subscriptionsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Signal(string? id = null)
        {
            if (!ready) return;
            foreach (var x in resolved.Where(x => id == null || x.source == id)) queues[x.m.Id].Writer.TryWrite(true);
        }
        async Task Worker((Mapping m, string source, string destination, ValidationResult validation) x)
        {
            using var source = x.m.SourceKind=="metasys"?store.Client("source"):null;
            using var destination = store.Client(Store.DestinationSide(x.m));
            var binding=RecoveryJournal.Binding(store,x.m,x.source,x.destination);
            var state = health.TryGetValue(x.m.Id, out var prior)
                ? prior with { State = "AwaitingCheck" }
                : new MappingHealth(x.m.Id, "AwaitingCheck", null, null, null, 0, 0, "none");
            state=state with{SourcePollIntervalSeconds=x.m.SourceKind=="json"?store.Config.ExternalSources[x.m.ExternalSourceId].PollSeconds:(int)HealthCheckInterval.TotalSeconds};
            health[x.m.Id] = state;
            async Task Readback(string expectedHash, CancellationToken operation, bool afterWrite)
            {
                var reliable = false;
                for (var attempt = 0; attempt < (afterWrite ? 5 : 1); attempt++)
                {
                    if (attempt > 0) await Task.Delay(500, operation);
                    var check = await destination.Get($"objects/{x.destination}/attributes/presentValue", operation);
                    x.m.DestinationValue = check?["item"]?["presentValue"]?.DeepClone();
                    var quality = check?["condition"]?["presentValue"]?["reliability"]?.ToString();
                    state = state with { DestinationQuality = quality == null ? "unreported" : quality == "reliabilityEnumSet.reliable" ? "reported-reliable" : "reported-unreliable" };
                    reliable = quality == null || quality == "reliabilityEnumSet.reliable";
                    if (reliable && x.m.DestinationValue!=null && expectedHash==RecoveryJournal.ValueHash(x.m.DestinationValue)) break;
                }
                if (!reliable || x.m.DestinationValue==null || expectedHash!=RecoveryJournal.ValueHash(x.m.DestinationValue))
                {
                    x.m.Status = !reliable ? "Write accepted; destination quality unreliable" : "Destination readback differs - no automatic overwrite";
                    state = state with { State = "DestinationUnconfirmed", FailureCode = !reliable ? "destination-quality" : "readback-mismatch", ConsecutiveFailures = state.ConsecutiveFailures + 1 };
                }
                else
                {
                    x.m.Status = "Verified"; x.m.LastSuccess = DateTimeOffset.UtcNow;
                    state = state with { State = "Healthy", LastDestinationConfirmed = DateTimeOffset.UtcNow, ConsecutiveFailures = 0, FailureCode = "none" };
                }
            }
            try
            {
                await foreach (var signal in queues[x.m.Id].Reader.ReadAllAsync(token))
                {
                    if (!Armed) break;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(OperationDeadline);
                    var operation = deadline.Token;
                    state = state with { LastCheckStarted = DateTimeOffset.UtcNow, LastHttpStatus = null };
                    health[x.m.Id] = state;
                    var stage = "worker-capacity";
                    var admitted = false;
                    try
                    {
                        await checkSlots.WaitAsync(operation); admitted = true;
                        stage = "source-read";
                        JsonNode? update;var sourceContact=DateTimeOffset.UtcNow;
                        if(x.m.SourceKind=="json")
                        {
                            var sample=await externalPool.Get(x.m.ExternalSourceId).Read(operation);
                            sourceContact=sample.FetchedAt;
                            state=state with{SourceObservationAt=sample.ObservedAt,SourcePollIntervalSeconds=store.Config.ExternalSources[x.m.ExternalSourceId].PollSeconds,SourceMaximumAgeSeconds=store.Config.ExternalSources[x.m.ExternalSourceId].MaxAgeSeconds};
                            update=new JsonObject{["item"]=new JsonObject{["presentValue"]=sample.Value.DeepClone()}};
                            if(sample.QualityReported)update["condition"]=new JsonObject{["presentValue"]=new JsonObject{["reliability"]="reliabilityEnumSet.reliable"}};
                        }
                        else update = await source!.Get($"objects/{x.source}/attributes/presentValue", operation);
                        state = state with { LastSourceContact = sourceContact };
                        var quality = update?["condition"]?["presentValue"]?["reliability"]?.ToString();
                        if (quality != null && quality != "reliabilityEnumSet.reliable")
                        {
                            x.m.Status = "Source quality unreliable - skipped";
                            state = state with { State = "SourceUnhealthy", SourceQuality = "reported-unreliable", FailureCode = "source-quality", ConsecutiveFailures = state.ConsecutiveFailures + 1 };
                        }
                        else if (update?["item"]?["presentValue"] is not JsonNode value)
                            throw new InvalidDataException();
                        else
                        {
                            state = state with { LastSourceRead = sourceContact, SourceQuality = quality == null ? "unreported" : "reported-reliable" };
                            x.m.SourceValue = value.DeepClone();
                            stage = "source-validation";
                            if (SchemaRules.Validate(value, x.validation.Destination!.Schema) != null) throw new InvalidDataException();
                            // An earlier request can have committed even if the client timed out.
                            // Resolve that outcome before issuing a different desired value.
                            stage="recovery-journal";
                            var record=journal.Read(x.m.Id,binding);
                            if(record?.SourceObservedAt is {} deliveredObservation && state.SourceObservationAt is {} currentObservation && currentObservation<deliveredObservation)
                                throw new ExternalSourceException("source-observation-regressed");
                            if (record!=null && record.Phase!="confirmed")
                            {
                                stage = "destination-readback";
                                await Readback(record.ValueHash, operation, false);
                                if (state.State != "Healthy")
                                {
                                    state = state with { State = "DeliveryUncertain", FailureCode = "prior-write-unconfirmed" };
                                    x.m.Status = "Prior write unconfirmed - further writes held for review";
                                    goto Completed;
                                }
                                stage="recovery-journal";
                                record=journal.Transition(x.m.Id,record,"confirmed");
                            }
                            // Record BEFORE sending: a timeout is an uncertain outcome, never an excuse to replay an unchanged write.
                            var valueHash=RecoveryJournal.ValueHash(value);
                            var changed = record==null || record.ValueHash!=valueHash;
                            if (changed)
                            {
                                stage="recovery-journal";
                                record=journal.Begin(x.m.Id,binding,valueHash,state.SourceObservationAt);
                                record=journal.Transition(x.m.Id,record,"submitted");
                                operation.ThrowIfCancellationRequested();
                                stage = "destination-write"; x.m.Status = "Writing source change";
                                if (x.m.WriteMode == "command")
                                {
                                    var command = x.validation.SelectedCommand!;
                                    await destination.Command(x.destination, command.Id, Preflight.CommandBody(command, x.m, value), operation);
                                }
                                else await destination.Write(x.destination, value, operation);
                            }
                            stage = "destination-readback";
                            await Readback(valueHash, operation, changed);
                            // Only matching readback can durably resolve a possibly committed write.
                            if(changed && state.State=="Healthy"){
                                stage="recovery-journal";journal.Transition(x.m.Id,record!,"confirmed");
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception e)
                    {
                        var category = e is OperationCanceledException ? "deadline" : e is InvalidDataException ? "invalid-data" : "request-failed";
                        var code = e is JournalException journalError ? journalError.Code : e is ExternalSourceException external ? external.Code : stage + "-" + category;
                        x.m.Status = e is MetasysRequestException ? stage + ": " + e.Message : code;
                        state = state with { State = "Unhealthy", FailureCode = code, ConsecutiveFailures = state.ConsecutiveFailures + 1,
                            SourceQuality = code=="source-quality"?"reported-unreliable":code=="source-quality-missing"?"unreported":state.SourceQuality,
                            DeadlineFailures = state.DeadlineFailures + (e is OperationCanceledException ? 1 : 0),
                            LastHttpStatus = e is ExternalSourceException failure ? failure.HttpStatus : e is HttpRequestException { StatusCode: { } status } ? (int)status : null };
                    }
                    finally { if (admitted) checkSlots.Release(); }
                    Completed:
                    state = state with { LastCheckCompleted = DateTimeOffset.UtcNow, CompletedChecks = state.CompletedChecks + 1 };
                    if (state.FailureCode != "none") state = state with { LastFailureCode = state.FailureCode,
                        LastFailureAt = DateTimeOffset.UtcNow, LastFailureHttpStatus = state.LastHttpStatus };
                    var before = health[x.m.Id]; health[x.m.Id] = state;
                    if (before.State != state.State || before.FailureCode != state.FailureCode ||
                        (state.ConsecutiveFailures > 0 && state.ConsecutiveFailures % 12 == 0))
                        Log(state.State == "Healthy" ? "info" : "warning", $"mapping={x.m.Id}; state={state.State}; code={state.FailureCode}; http={state.LastHttpStatus}; failures={state.ConsecutiveFailures}; checks={state.CompletedChecks}; deadlines={state.DeadlineFailures}");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { health[x.m.Id] = state with { State = "Stopped" }; }
        }
        async Task PeriodicChecks(string mappingId, int index)
        {
            try
            {
                await subscriptionsReady.Task.WaitAsync(token);
                // Spread fixed periodic checks across the interval. COV on any mapping
                // never postpones these checks or resets another mapping's schedule.
                var mapping=resolved[index].m;
                var interval=mapping.SourceKind=="json"?TimeSpan.FromSeconds(Math.Min(HealthCheckInterval.TotalSeconds,store.Config.ExternalSources[mapping.ExternalSourceId].PollSeconds)):HealthCheckInterval;
                await Task.Delay(TimeSpan.FromTicks(interval.Ticks / resolved.Count * (index + 1)), token);
                if(mapping.SourceKind!="metasys"||ready)queues[mappingId].Writer.TryWrite(true);
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(token))if(mapping.SourceKind!="metasys"||ready)queues[mappingId].Writer.TryWrite(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        var workers = resolved.Select(Worker).ToArray();
        var periodic = Task.WhenAll(resolved.Select((x, index) => PeriodicChecks(x.m.Id, index)));
        async Task StreamLoop()
        {
            while(!token.IsCancellationRequested)
            {
                using var src=store.Client("source");
                try
                {
                    Status="Connecting COV stream";
                    await src.Stream((evt,data)=>
                    {
                        if(evt=="hello")return Subscribe();
                        if(evt=="object.values.notification"){foreach(var x in resolved.Where(x=>x.m.SourceKind=="metasys"))queues[x.m.Id].Writer.TryWrite(true);return Task.CompletedTask;}
                        if(evt=="object.values.heartbeatImproved")
                        {
                            var heartbeat=JsonNode.Parse(data);
                            if(heartbeat?["activeSubscriptions"] is JsonArray active&&src.SubscriptionIds.Count>0)
                            {
                                var ids=active.Select(x=>x?.ToString()??"").ToHashSet();
                                if(!src.SubscriptionIds.IsSubsetOf(ids))throw new IOException("Missing subscriptions.");
                            }
                        }
                        if(evt=="object.values.update")
                        {
                            var payload=JsonNode.Parse(data);var updates=payload?["value"] is JsonArray a?a.ToArray():new[]{payload};
                            foreach(var update in updates)Signal(update?["item"]?["id"]?.ToString());
                        }
                        return Task.CompletedTask;
                        async Task Subscribe()
                        {
                            var stream=data.Trim('"');
                            if(!Guid.TryParse(stream,out _)){var j=JsonNode.Parse(data);stream=(j?["id"]??j?["streamId"])?.GetValue<string>()??throw new InvalidDataException();}
                            foreach(var id in resolved.Where(x=>x.m.SourceKind=="metasys").Select(x=>x.source).Distinct())await src.Subscribe(id,stream,token);
                            ready=true;Signal();subscriptionsReady.TrySetResult();Status="COV connected - periodic verification active";
                        }
                    },token);
                }
                catch(OperationCanceledException)when(token.IsCancellationRequested){return;}
                catch(Exception exception){Status=exception is MetasysRequestException?"Disconnected COV: "+exception.Message+" Retrying in 10 seconds; periodic checks continue.":"Disconnected COV - retrying in 10 seconds; periodic checks continue";Log("warning","stage=cov; code=stream-disconnected; retrySeconds=10");await Task.Delay(10000,token);}
            }
        }
        try
        {
            foreach(var x in resolved.Where(x=>x.m.SourceKind=="json"))queues[x.m.Id].Writer.TryWrite(true);
            subscriptionsReady.TrySetResult();
            if(resolved.Any(x=>x.m.SourceKind=="metasys"))await StreamLoop();
            else {ready=true;Status="JSON polling - periodic destination verification active";await Task.Delay(Timeout.Infinite,token);}
        }        finally
        {
            lifetime.Cancel();
            foreach (var q in queues.Values) q.Writer.TryComplete();
            await Task.WhenAll(workers.Append(periodic));
        }
    }
}

public record MappingHealth(string MappingId, string State, DateTimeOffset? LastSourceRead,
    DateTimeOffset? LastDestinationConfirmed, DateTimeOffset? LastCheckCompleted,
    long CompletedChecks, int ConsecutiveFailures, string FailureCode)
{
    public string SourceQuality { get; init; } = "unreported";
    public string DestinationQuality { get; init; } = "unreported";
    public DateTimeOffset? LastSourceContact { get; init; }
    public DateTimeOffset? SourceObservationAt { get; init; }
    public int SourcePollIntervalSeconds { get; init; } = 300;
    public int SourceMaximumAgeSeconds { get; init; }
    public DateTimeOffset? LastCheckStarted { get; init; }
    public int? LastHttpStatus { get; init; }
    public long DeadlineFailures { get; init; }
    public string? LastFailureCode { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public int? LastFailureHttpStatus { get; init; }
    public string ObservationFreshness { get; init; } = "not-established-by-current-read";
}
