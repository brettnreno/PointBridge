# Metasys watchdog health: offline contract and commissioning boundary

## Status

Implemented in staged source: a pure health-frame producer, authenticated read-only diagnostic snapshot, disabled/empty publishing configuration, a simulation-only publishing interface, and an independent watchdog consumer with deterministic tests. There is NO live Metasys health-point writer, no native watchdog program installed, and no notification transport. App startup rejects HealthPublishing.Enabled=true so an incomplete publisher cannot be mistaken for commissioned protection. This does not enable or alter transfers.

RecipientOrigin is empty and Points is empty by default; see health-publishing.disabled.example.json. The separate simulator permits only reserved HTTPS .invalid origins and WriteMode=simulation. It sends to an in-memory interface with no HttpClient/API transport. Bindings require unique object IDs and all component signals; a process-only heartbeat binding is insufficient. No real point IDs, credentials, cookies, passwords, user accounts or customer settings appear in these defaults.

GET /api/health/watchdog-frame requires existing application authentication. It is diagnostic-only. A request increments processSequence; it is NOT a scheduled publisher or proof that transfer work progressed. The frame schema is health-frame.schema.json (camelCase serialization). No raw measurements, passwords, bearer tokens or vendor response bodies are included.

## Signals and independent decisions

- runtimeInstance plus processSequence distinguishes new frames, process restarts and replay. The independent consumer uses its own monotonic receive timer, not the sender's wall clock.
- Each mapping supplies completedChecks and workerProgressing separately from sourceCommunicationHealthy, sourceQualityReported/sourceQualityReliable, destinationConfirmed and destinationQualityReported/destinationQualityReliable.
- Reported bad quality is different from missing quality. A successful source response can establish communication while quality is explicitly unreliable. Neither is eligible for overallHealthy. Missing quality stays unconfirmed. Physical observation age is explicitly NOT established by this interface.
- Five-minute periodic checks remain the default; schedules are staggered, with bounded concurrent checks. An unchanged measured value is healthy when checks and reported quality succeed. No COV/change is required. Held destination trend records cannot substitute for new readback confirmation.
- Process progress does not imply work is healthy. The consumer independently observes per-worker completedChecks advancement, including a faulty publisher simulation that keeps renewing a healthy flag while freezing counters. Expired progress, counter regression, missing required mappings, known source/write failure, unavailable quality or a stalled worker produces an alarm state.
- A restarted runtime must advance fresh worker checks before recovery. Replayed sequences/retired runtimes cannot refresh the receive timeout. A required active bridge unexpectedly paused is a fault; maintenance suppression must be explicit and separately commissioned.

The offline consumer defaults demonstrate 90 seconds of heartbeat silence and 345 seconds of worker silence (300-second checks plus45-second operation budget). These are SIMULATOR parameters, not an approved pharmacy alarm SLA. Live heartbeat cadence, missed checks, operation bounds, acknowledgement/repeat/recovery policy and maintenance handling need site acceptance. The five-minute reconciliation preference does not set every alarm delay.

## Precise blocker to live health publishing

No target health objects or their actual metadata have been selected. Their numeric/boolean data types, ranges/counter rollover, writability, supported adjustment commands/priority and reliable readback are unknown. A generic PATCH or guessed command could fail or interfere with priority control. This work therefore deliberately supplies a schema and offline simulator rather than guessing a live health write contract.

Multiple Metasys point writes also do not establish an atomic health frame. Native logic must reject torn/partial snapshots using a commissioned commit/generation scheme or equivalent coherent observation; a new process counter with old healthy flags must not look healthy. Actual target types, write/readback semantics and the native watchdog's supported logic must be verified before a production sink can be designed and enabled. A successful API login is not this evidence.

Notification acknowledgement is modeled by a fake sink only. No native delivery acknowledgement API has been established. The simulator retains delivery-unconfirmed on notification loss and never clears the underlying alarm merely because notification was acknowledged. Actual delivery to the existing Metasys notification route and monitoring of that route require witnessed commissioning; the application cannot guarantee alert receipt while it or the notification infrastructure is dead.

## On-site work still required (operator only, no agent customer access)

1. Select dedicated health objects and identify the available independent supervisory node. Obtain non-secret object metadata for supported value types, commands/priorities/ranges and readback, and define expected mapping inventory. Keep pharmacy values separate from health flags/counters.
2. Commission native logic that detects missing/nonadvancing process and worker signals, component failures, inconsistent snapshots and restart/reset. If the recipient API cannot be written, its independently running missing-heartbeat watchdog must still alarm. To cover an entire recipient/bridge host outage, use an available different supervisory node; a dead server cannot send its own notification.
3. Attach alarms to the existing Metasys notification route, define escalation/acknowledgement/repeat/recovery and explicit maintenance suppression. Verify actual receipt and notifier-failure indication. No recipient addresses/passwords need to be sent in chat.
4. With only approved noncritical test objects, witness constant-value healthy operation, source failure, destination write/readback failure, process crash/stall, health-publication path failure, supervisor/recipient outage, recovery and notification loss. A watchdog test must run independently of the bridge process, not only a UI refresh.
5. Preserve the original protections until these tests are accepted. Record expected/observed behavior and confirmed alarm delivery. Do not enable pharmacy transfers, copied-value alarms or release packaging based only on simulator success.

## Offline evidence

validation/WatchdogScenarios.cs uses virtual clocks and in-memory endpoint/notification sinks. Existing transfer regressions remain in validation/BridgeScenario.cs. Verification output is verification/WATCHDOG-REGRESSIONS.txt. The local HTTP fixture covers authentication on diagnostic routes and confirms a paused instance cannot report overallHealthy. These are development tests, not Metasys-native alarm validation.

External HTTP/weather sources and multiple recipient connections remain a separate upcoming feature. No release packaging, live health writes, point creation, alarm configuration, external notifications or customer access occurred.
