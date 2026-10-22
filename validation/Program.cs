using System.Net;
using System.Text.Json.Nodes;
using static ContractTransport;

int passed = 0;
async Task Test(string name, Func<Task> action) { try { await action(); Console.WriteLine("PASS " + name); passed++; } catch(Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); Environment.ExitCode = 1; } }
const string id = "32cfddc0-411f-5b9e-9a6d-442cb62fecbb";
const string stream = "b6d184ee-e137-44aa-b9b1-14bc704cd25d";
foreach(var version in new[]{"v5","v6"})
{
    var transport = new ContractTransport();
    var http = new HttpClient(transport);
    using var api = new Api(new Connection{BaseUrl="https://adx.test",ApiVersion=version,Username="api-user"},"secret",http);
    transport.Expect(async q => {
        Check(q.Method==HttpMethod.Post && q.RequestUri!.AbsolutePath==$"/api/{version}/login", "login endpoint/method");
        var body=JsonNode.Parse(await q.Content!.ReadAsStringAsync());
        Check(body?["username"]?.ToString()=="api-user" && body?["password"]?.ToString()=="secret", "login credentials");
        return Json(new JsonObject{["accessToken"]="contract-token",["expires"]=DateTimeOffset.UtcNow.AddHours(1).ToString("O")}.ToJsonString());
    });
    await Test(version+" login",()=>api.Login(default));
    await Test(version+" reserved reference resolution",async()=>{
        const string reference="adx:engine/Folder.Temp & Humidity";
        transport.Expect(q=>{
            Check(q.Headers.Authorization?.ToString()=="Bearer contract-token","bearer propagation");
            Check(q.RequestUri!.AbsolutePath==$"/api/{version}/objects/identifiers","resolve route");
            Check(q.RequestUri.Query=="?fqr="+Uri.EscapeDataString(reference),"FQR query encoding");
            return Task.FromResult(Json("\""+id+"\""));
        });
        Check(await api.Resolve(reference,default)==id,"bare GUID response");
    });
    await Test(version+" GUID avoids lookup",async()=>Check(await api.Resolve(id,default)==id,"GUID pass-through"));
    await Test(version+" discovery envelope",async()=>{
        transport.Expect(q=>{Check(q.RequestUri!.Query=="?depth=1&flatten=true","documented hierarchy query");return Task.FromResult(Json("{\"items\":[{\"id\":\""+id+"\",\"name\":\"Temp\",\"hasChildrenMatchingQuery\":false}],\"next\":null}"));});
        var result=await api.Get("objects?depth=1&flatten=true",default);
        Check(result?["items"] is JsonArray arr && arr.Count==1 && arr[0]?["name"]?.ToString()=="Temp","discovery envelope retained");
    });
    foreach(var scalar in new[]{"72.5","true","\"binaryPVEnumSet.active\""})
    await Test(version+" typed PATCH "+scalar,async()=>{
        transport.Expect(async q=>{
            Check(q.Method==HttpMethod.Patch && q.RequestUri!.AbsolutePath==$"/api/{version}/objects/{id}","PATCH route");
            var body=JsonNode.Parse(await q.Content!.ReadAsStringAsync());
            Check(JsonNode.DeepEquals(body?["item"]?["presentValue"],JsonNode.Parse(scalar)),"native scalar must not stringify");
            Check(body!.AsObject().Count==1 && body["item"]!.AsObject().Count==1,"exact write scope");
            return Json("{}");
        });
        await api.Write(id,JsonNode.Parse(scalar)!,default);
    });
    await Test(version+" subscription accepts empty 202",async()=>{
        transport.Expect(q=>{
            Check(q.Method==HttpMethod.Get && q.RequestUri!.AbsolutePath==$"/api/{version}/objects/{id}/attributes/presentValue","subscription route");
            Check(q.Headers.GetValues("METASYS-SUBSCRIBE").Single()==stream,"stream header");
            var r=new HttpResponseMessage(HttpStatusCode.Accepted);
            r.Headers.Add("METASYS-SUBSCRIPTION-LOCATION",$"https://adx.test/api/{version}/objects/streams/{stream}/subscriptions/123");
            return Task.FromResult(r);
        });
        await api.Subscribe(id,stream,default);
    });
    await Test(version+" 403 surfaced",async()=>{
        transport.Expect(q=>Task.FromResult(Json("{}",HttpStatusCode.Forbidden)));
        try { await api.Get("objects",default); throw new Exception("Forbidden was swallowed"); }
        catch(Exception e) when(e.Message.Contains("403")) { }
    });
    await Test(version+" SSE framing",async()=>{
        transport.Expect(q=>{
            Check(q.Headers.Authorization?.Parameter=="contract-token","stream token");
            Check(q.RequestUri!.AbsolutePath==$"/api/{version}/stream","SSE endpoint");
            return Task.FromResult(Events(": comment\r\nevent: hello\r\ndata: "+stream+"\r\n\r\nevent: object.values.update\r\ndata: {\"item\":\r\ndata: {\"presentValue\":42}}\r\n\r\n"));
        });
        var events=new List<(string,string)>();
        try{await api.Stream((kind,data)=>{events.Add((kind,data));return Task.CompletedTask;},default);}
        catch(IOException){ }
        Check(events.Count==2 && events[0]==("hello",stream),"hello frame");
        Check(JsonNode.Parse(events[1].Item2)?["item"]?["presentValue"]?.GetValue<int>()==42,"multiline SSE data");
    });
    transport.Complete();
}
await Test("Bridge orchestration", BridgeScenario.Run);
await Test("Readback mismatch never records success", BridgeScenario.ReadbackMismatch);
await Test("Unreliable matching destination never records success", BridgeScenario.DestinationQuality);
await Test("Expired token renewal", FailureScenarios.TokenExpiry);
await Test("Unconfirmed subscription rejected", FailureScenarios.MissingSubscriptionHeader);
await Test("Stream reconnects before token expiry", FailureScenarios.StreamTokenLifetime);
await Test("Failed durable settings save rolls back", FailureScenarios.DurableSettingsFailure);
await Test("Changed host/account requires password", FailureScenarios.BlankPasswordChanges);
await Test("Live metadata and command preflight", PreflightScenarios.Run);
await Test("Regression: destination drift with unchanged source", BridgeScenario.RiskDestinationDrift);
await Test("Regression: synthetic older source event", BridgeScenario.RiskOlderEvent);
await Test("Regression: blocked destination holds later mappings", BridgeScenario.RiskBlockedDestination);
await Test("Periodic stable-source health, outage and recovery", BridgeScenario.SourceHealth);
await Test("Aggregate deadline and uncertain write reconciliation", BridgeScenario.DeadlineAndUncertainWrite);
await Test("Per-mapping destination503 and conservative recovery", BridgeScenario.PartialDestinationFailure);
await Test("Quiet mapping schedule independent of other COV", BridgeScenario.QuietMappingSchedule);
await Test("Health publisher disabled defaults and simulation-only bindings", WatchdogScenarios.DisabledAndBindings);
await Test("Watchdog stable values and five-minute verification", WatchdogScenarios.ConstantHealthy);
await Test("Independent watchdog detects producer crash", WatchdogScenarios.Crash);
await Test("Heartbeat cannot mask worker stall", WatchdogScenarios.Stall);
await Test("Separate source health and verified recovery", WatchdogScenarios.SourceOutageAndRecovery);
await Test("Separate destination delivery outage", WatchdogScenarios.DestinationOutage);
await Test("Lost health publishing path", WatchdogScenarios.HeartbeatDeliveryLoss);
await Test("Notification failure remains unconfirmed", WatchdogScenarios.NotificationLoss);
await Test("Runtime restart and heartbeat replay rejection", WatchdogScenarios.RestartAndReplay);
await Test("Required mapping inventory and unknown quality", WatchdogScenarios.InventoryAndQuality);
await Test("Independent worker counter expiry despite healthy flag", WatchdogScenarios.IndependentCounterStall);
await Test("JSON scalar read, credential header and shared poll budget", ExtensionScenarios.ReadAndCache);
await Test("External429/service errors with bounded retries", ExtensionScenarios.RateAndServiceErrors);
await Test("Null, invalid, units, old/future and regressed observations", ExtensionScenarios.InvalidAndStale);
await Test("External timeout, response limits and redirect refusal", ExtensionScenarios.LimitsAndTimeout);
await Test("Explicit v1 migration and encrypted API secrets", ExtensionScenarios.MigrationAndSecrets);
await Test("Mixed v5/v6 recipient fanout, source failure and safe recovery", ExtensionScenarios.FanoutAndRecovery);
await Test("Extension gate and paused instance prohibit source requests/writes", ExtensionScenarios.FeatureGateAndPaused);
await Test("Durable journal crash windows and confirmed commit",RecoveryScenarios.CrashWindows);
await Test("Actual bridge restart readback, drift and unavailable journal",BridgeScenario.DurableRestart);
await Test("External observation ordering survives process restart",ExtensionScenarios.DurableSourceOrdering);
await Test("Corrupt/incomplete journal holds writes",RecoveryScenarios.CorruptAndIncomplete);
await Test("Journal disk-full, single writer and changed binding",RecoveryScenarios.StorageAndBinding);
await Test("Bounded diagnostic export secret canaries",RecoveryScenarios.ExportRedaction);
Console.WriteLine($"{passed} contract scenarios passed; exit {Environment.ExitCode}");
