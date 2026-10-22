using static ContractTransport;
public static class WatchdogScenarios
{
    static readonly TimeSpan Interval=TimeSpan.FromSeconds(300),Deadline=TimeSpan.FromSeconds(45);
    sealed class Clock:TimeProvider
    {
        long ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>ticks;
        public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,10,22,0,0,0,TimeSpan.Zero)+TimeSpan.FromTicks(ticks);
        public void Advance(int seconds)=>ticks+=TimeSpan.FromSeconds(seconds).Ticks;
    }
    static MappingHealth Healthy(Clock c,long count=1)=>new("map-a","Healthy",c.GetUtcNow(),c.GetUtcNow(),c.GetUtcNow(),count,0,"none")
        {LastCheckStarted=c.GetUtcNow().AddSeconds(-1),SourceQuality="reported-reliable",DestinationQuality="reported-reliable"};
    static IndependentHealthWatchdog Watch(Clock c,params string[] expected)=>new(c,new(){RequiredMappingIds=expected.Length==0?["map-a"]:expected});
    static HealthFrame Frame(HealthFrameProducer p,MappingHealth m)=>p.Capture(true,[m],Interval,Deadline);
    static HealthPublishingOptions Options(HealthFrame frame)=>new(){Enabled=true,RecipientOrigin="https://health-fixture.invalid",
        Points=SimulatedHealthPublisher.RequiredSignals(frame).Select(s=>new HealthPointBinding(s,Guid.NewGuid().ToString(),s.EndsWith("completedChecks")||s=="process.sequence"?"counter":"boolean","simulation")).ToList()};
    sealed class Endpoint:ISimulatedHealthEndpoint
    {
        public bool Fail,Throw;public int Calls;public HealthFrame? Received;
        public Task<bool> AcceptAsync(SimulatedHealthEnvelope e,CancellationToken ct){Calls++;if(Throw)throw new Exception("fixture-secret-not-real");if(!Fail)Received=e.Frame;return Task.FromResult(!Fail);}
    }
    sealed class Notifications:ISimulatedAlarmNotifications
    {
        public bool Fail;public int Calls;public Task<bool> NotifyAsync(WatchdogDecision d,CancellationToken ct){Calls++;return Task.FromResult(!Fail);}
    }
    public static async Task DisabledAndBindings()
    {
        var clock=new Clock();var producer=new HealthFrameProducer(clock);var frame=Frame(producer,Healthy(clock));var endpoint=new Endpoint();
        var options=new HealthPublishingOptions();
        Check(!options.Enabled&&options.RecipientOrigin==""&&options.Points.Count==0,"shipping health defaults empty and disabled");
        Check(await SimulatedHealthPublisher.PublishAsync(options,frame,endpoint)=="disabled"&&endpoint.Calls==0,"disabled publisher cannot contact even fake endpoint");
        options.Enabled=true;options.RecipientOrigin="https://metasys-lab";
        try{await SimulatedHealthPublisher.PublishAsync(options,frame,endpoint);throw new Exception("live origin accepted");}catch(InvalidOperationException){}
        options=Options(frame);
        Check(await SimulatedHealthPublisher.PublishAsync(options,frame,endpoint)=="accepted-simulation"&&endpoint.Calls==1,"only reserved fixture origin dispatches to simulation interface");
        options.Points.Add(new("unknown-signal",Guid.NewGuid().ToString(),"boolean","simulation"));
        try{await SimulatedHealthPublisher.PublishAsync(options,frame,endpoint);throw new Exception("unknown binding accepted");}catch(InvalidOperationException){}
        Check(endpoint.Calls==1,"invalid bindings rejected before endpoint call");
    }
    public static Task ConstantHealthy()
    {
        var clock=new Clock();var receiverClock=new Clock();var producer=new HealthFrameProducer(clock);var watch=Watch(receiverClock);var m=Healthy(clock);long checks=1;
        watch.Receive(Frame(producer,m));
        for(var n=1;n<=20;n++){
            clock.Advance(30);receiverClock.Advance(30);if(n%10==0)m=Healthy(clock,++checks);
            var frame=Frame(producer,m);Check(frame.OverallHealthy,"stable value stays healthy between five-minute checks");watch.Receive(frame);
            Check(!watch.Evaluate().Alarm,"healthy communication does not require value changes");
        }
        Check(m.CompletedChecks==3,"source checks follow five-minute cadence independently of process heartbeat");return Task.CompletedTask;
    }
    public static Task Crash()
    {
        var clock=new Clock();var receiverClock=new Clock();var producer=new HealthFrameProducer(clock);var watch=Watch(receiverClock);
        watch.Receive(Frame(producer,Healthy(clock)));receiverClock.Advance(91);
        Check(watch.Evaluate().Code=="missing-heartbeat"&&watch.Evaluate().Alarm,"independent consumer detects crashed producer without producer code running");return Task.CompletedTask;
    }
    public static Task Stall()
    {
        var clock=new Clock();var receiverClock=new Clock();var producer=new HealthFrameProducer(clock);var watch=Watch(receiverClock);var m=Healthy(clock);
        watch.Receive(Frame(producer,m));m=m with{LastCheckStarted=clock.GetUtcNow().AddSeconds(1)};clock.Advance(47);receiverClock.Advance(47);
        var frame=Frame(producer,m);watch.Receive(frame);
        Check(frame.ProcessSequence==2&&!frame.OverallHealthy&&frame.Mappings.Single().SourceCommunicationHealthy,"live process heartbeat cannot mask stalled worker");
        Check(watch.Evaluate().Code=="worker-stalled-or-unconfirmed","independent consumer alarms on worker stall while heartbeat arrives");return Task.CompletedTask;
    }
    public static Task SourceOutageAndRecovery()
    {
        var clock=new Clock();var receiverClock=new Clock();var producer=new HealthFrameProducer(clock);var watch=Watch(receiverClock);var m=Healthy(clock);
        watch.Receive(Frame(producer,m));clock.Advance(30);receiverClock.Advance(30);
        var fault=m with{State="Unhealthy",FailureCode="source-read-request-failed",LastCheckCompleted=clock.GetUtcNow(),CompletedChecks=2};var frame=Frame(producer,fault);watch.Receive(frame);
        Check(!frame.Mappings[0].SourceCommunicationHealthy&&frame.Mappings[0].DestinationConfirmed,"source and destination health are separate");
        Check(watch.Evaluate().Code=="source-health-unconfirmed","known source failure alarms before old successful read ages out");
        clock.Advance(30);receiverClock.Advance(30);watch.Receive(Frame(producer,Healthy(clock,3)));
        Check(!watch.Evaluate().Alarm&&watch.Evaluate().NotificationPending,"fresh verified checks create recovery notification");return Task.CompletedTask;
    }
    public static Task DestinationOutage()
    {
        var clock=new Clock();var receiverClock=new Clock();var producer=new HealthFrameProducer(clock);var watch=Watch(receiverClock);var m=Healthy(clock);
        watch.Receive(Frame(producer,m));clock.Advance(30);receiverClock.Advance(30);
        var fault=m with{State="Unhealthy",FailureCode="destination-write-request-failed",LastCheckCompleted=clock.GetUtcNow(),CompletedChecks=2};var frame=Frame(producer,fault);watch.Receive(frame);
        Check(frame.Mappings[0].SourceCommunicationHealthy&&!frame.Mappings[0].DestinationConfirmed,"destination outage cannot use held trend value as confirmation");
        Check(watch.Evaluate().Code=="destination-delivery-unconfirmed","recipient/write failure detected independently");return Task.CompletedTask;
    }
    public static async Task HeartbeatDeliveryLoss()
    {
        var clock=new Clock();var receiverClock=new Clock();var producer=new HealthFrameProducer(clock);var watch=Watch(receiverClock);var endpoint=new Endpoint();
        var initial=Frame(producer,Healthy(clock));var options=Options(initial);
        await SimulatedHealthPublisher.PublishAsync(options,initial,endpoint);watch.Receive(endpoint.Received!);
        endpoint.Fail=true;clock.Advance(30);receiverClock.Advance(30);
        Check(await SimulatedHealthPublisher.PublishAsync(options,Frame(producer,Healthy(clock,2)),endpoint)=="delivery-unconfirmed","publisher recognizes simulated lost delivery");
        receiverClock.Advance(61);Check(watch.Evaluate().Code=="missing-heartbeat","receiver detects path failure even when producer cannot report fault there");
        endpoint.Throw=true;Check(await SimulatedHealthPublisher.PublishAsync(options,Frame(producer,Healthy(clock,3)),endpoint)=="delivery-unconfirmed","sink exception details are withheld");
    }
    public static async Task NotificationLoss()
    {
        var clock=new Clock();var watch=Watch(clock);var notifications=new Notifications{Fail=true};
        var fault=watch.Evaluate();Check(fault.Alarm,"missing producer alarm active");
        var failed=await watch.NotifySimulatedAsync(notifications);
        Check(failed.Alarm&&failed.NotificationPending&&failed.NotificationState=="delivery-unconfirmed"&&failed.NotificationFailures==1,"notification loss cannot clear alarm or be reported delivered");
        notifications.Fail=false;var delivered=await watch.NotifySimulatedAsync(notifications);
        Check(delivered.Alarm&&!delivered.NotificationPending,"acknowledgment does not clear underlying fault");
        var calls=notifications.Calls;await watch.NotifySimulatedAsync(notifications);Check(notifications.Calls==calls,"acknowledged unchanged alarm not repeatedly sent");
    }
    public static Task RestartAndReplay()
    {
        var clock=new Clock();var producer=new HealthFrameProducer(clock);var watch=Watch(clock);var old=Frame(producer,Healthy(clock));watch.Receive(old);
        clock.Advance(50);Check(!watch.Receive(old),"replayed heartbeat not accepted");clock.Advance(41);Check(watch.Evaluate().Code=="missing-heartbeat","replayed frames cannot refresh receive timer");
        var replacement=new HealthFrameProducer(clock);watch.Receive(Frame(replacement,Healthy(clock)));
        Check(watch.Evaluate().Code=="restart-awaiting-checks","new runtime must revalidate worker progress");
        watch.Receive(Frame(replacement,Healthy(clock)));Check(watch.Evaluate().Alarm,"process heartbeat alone does not prove restart recovery");
        watch.Receive(Frame(replacement,Healthy(clock,2)));Check(!watch.Evaluate().Alarm,"fresh completed checks establish restart recovery");
        Check(!watch.Receive(Frame(producer,Healthy(clock,9))),"retired runtime cannot be replayed as a new healthy instance");return Task.CompletedTask;
    }
    public static Task IndependentCounterStall()
    {
        var clock=new Clock();var producer=new HealthFrameProducer(clock);var watch=Watch(clock);var frame=Frame(producer,Healthy(clock));watch.Receive(frame);
        for(var n=1;n<=12;n++){
            clock.Advance(30);
            // Deliberately model a faulty publisher that renews a healthy flag while freezing worker counters.
            watch.Receive(frame with{ProcessSequence=n+1,CapturedAt=clock.GetUtcNow()});
        }
        Check(watch.Evaluate().Code=="worker-progress-expired","consumer independently detects frozen worker counter despite healthy live heartbeat");
        watch.Receive(frame with{ProcessSequence=14,Mappings=[frame.Mappings[0] with{CompletedChecks=0}]});
        Check(watch.Evaluate().Code=="worker-counter-regressed","worker counter regression cannot become recovery");return Task.CompletedTask;
    }
    public static Task InventoryAndQuality()
    {
        var clock=new Clock();var producer=new HealthFrameProducer(clock);var watch=Watch(clock,"map-a","map-b");watch.Receive(Frame(producer,Healthy(clock)));
        Check(watch.Evaluate().Code=="mapping-inventory-incomplete","omitting a failed mapping cannot produce healthy status");
        var unknown=Frame(producer,Healthy(clock) with{SourceQuality="unreported",DestinationQuality="unreported"});
        Check(!unknown.OverallHealthy&&!unknown.Mappings[0].SourceQualityReported&&!unknown.Mappings[0].DestinationQualityReported,"missing quality never becomes proven reliable health");
        var empty=producer.Capture(true,[],Interval,Deadline);Check(!empty.OverallHealthy,"empty required work is not healthy");
        Check(!watch.Receive(empty with{Mappings=null!}),"malformed frame cannot refresh independent heartbeat");
        var unreliable=Frame(producer,Healthy(clock) with{SourceQuality="reported-unreliable",LastSourceRead=clock.GetUtcNow().AddSeconds(-600),LastSourceContact=clock.GetUtcNow()});
        Check(unreliable.Mappings[0].SourceCommunicationHealthy&&unreliable.Mappings[0].SourceQualityReported&&!unreliable.Mappings[0].SourceQualityReliable&&!unreliable.OverallHealthy,"successful source communication is separate from unreliable process quality");
        var expired=Frame(producer,Healthy(clock) with{SourceObservationAt=clock.GetUtcNow().AddSeconds(-61),SourceMaximumAgeSeconds=60});
        Check(!expired.OverallHealthy&&!expired.Mappings[0].SourceCommunicationHealthy,"expired JSON observation faults even before next scheduled poll with recent API contact");
        var json=System.Text.Json.JsonSerializer.SerializeToElement(unknown,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var schemaPath=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../app/health-frame.schema.json"));
        using var schema=System.Text.Json.JsonDocument.Parse(File.ReadAllText(schemaPath));
        var expected=schema.RootElement.GetProperty("required").EnumerateArray().Select(x=>x.GetString()).ToHashSet();
        Check(expected.SetEquals(json.EnumerateObject().Select(x=>x.Name)),"serialized diagnostic root matches documented schema fields");
        var mappingFields=schema.RootElement.GetProperty("properties").GetProperty("mappings").GetProperty("items").GetProperty("required").EnumerateArray().Select(x=>x.GetString()).ToHashSet();
        Check(mappingFields.SetEquals(json.GetProperty("mappings")[0].EnumerateObject().Select(x=>x.Name)),"serialized component health matches documented schema fields");
        return Task.CompletedTask;
    }
}
