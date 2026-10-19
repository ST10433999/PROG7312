# PROG7312 Part 2 — demo video script

**Length:** about 6 minutes. **Set-up:** API and client running, browser on the left, IDE on the right. Do one full dry run first: the predictive-engine scene needs two earlier occurrences, and a dry run tells you exactly where to click.

---

## 0:00 – 0:25 · Intro (Gateway home)

> "This is my Part 2 submission for PROG7312. Part 1 built the ingestion gateway; Part 2 activates the Real-Time Command Stream pillar. Everything runs on .NET 10 – a Minimal API and a Blazor WebAssembly client – and the source is on GitHub."

## 0:25 – 0:55 · Navigation without losing state (Main menu, 30 marks)

Click Home → Sensor Ingestion → Command Stream → Command history → Sensor Ingestion → Command Stream.

> "Every page is an asynchronous route. The Part 1 Pulse Board keeps polling, the API status dot stays green, and the Command Stream state lives on the server, so nothing is lost when I switch modules."

## 0:55 – 1:45 · Queue and PriorityQueue (15 marks)

1. Point at the **Telemetry FIFO** counter. Click **📦 Burst 200 normal packets**.
   > "These 200 packets hit `/api/stream/telemetry/batch` exactly like devices. They join a FIFO `Queue<T>`. The worker drains eight every 250 ms, so you can see the backlog draining."
2. While the backlog is still draining, click **⚡ Power spike (2 600 W)**.
   > "This reading is above 2 000 W, so the router puts it in the `PriorityQueue` instead. In the Critical alerts card it was processed within about 250 ms, ahead of all the packets still queued."
3. Switch to `CommandStreamState.cs` and show `_telemetryQueue` and `_criticalAlerts`. Point out the `(Severity, Sequence)` priority: *"The sequence number keeps equal-severity alerts in arrival order, because the built-in priority queue isn't stable."* Then show the first loop in `CommandWorker.ExecuteAsync`.

## 1:45 – 2:20 · HashSet (10 marks)

1. Click **⚡ Power spike** again.
   > "Same meter, still faulted. `HashSet.Add` returns false, so the duplicate alert is dropped. The *duplicates dropped* counter goes up and no second alert appears."
2. Select the meter in the simulator, enter `1230` and click **Send reading**. The fault disappears from **Active faults**.
   > "A normal reading means the condition has cleared, so the MAC is removed from the set."
3. Show `TryRaiseCritical` in code.

## 2:20 – 3:00 · Stack undo (15 marks with the queues)

1. Click **On** for *Pump 01*, then **↶ Undo: TurnOn Pump 01**. The pump goes back to OFF.
2. Click **⛔ Shut down all valves**, then **↶ Undo** once.
   > "Each operator action is one entry on a `Stack<CommandBatch>`. A bulk action is a single entry, so one Undo restores every valve to the exact state it was in, not just the opposite command."
3. Show `CommandStreamState.Undo()`.

## 3:00 – 3:40 · Dictionary and SortedDictionary (15 marks)

1. Type `Pump 01` in the device search.
   > "Devices live in a `Dictionary<string, SmartDevice>` keyed by MAC, so lookups are O(1) whenever a command or packet arrives."
2. Scroll to **📈 Sensor timeline** and pick the soil probe or the meter.
   > "Every processed reading goes into a `SortedDictionary` keyed by timestamp and sequence. The graph reads it in order, so there's no sort step."
3. Open **📜 Full history**: the same structure for commands, with undone rows struck through.

## 3:40 – 5:10 · Predictive Action & Recommendation Engine (30 marks)

*Before recording, do two occurrences off camera (or show them quickly):* select **Soil probe 000** → **💧 Critical moisture drop** → search `Pump 01` → **On** → **Send reading** with 48. Repeat once.

On camera:
1. Click **💧 Critical moisture drop** (third occurrence).
   > "Watch the suggestions panel. Without me searching, the engine proposes *TurnOn · Pump 01* at 100 %, *seen 2×*, plus *Check Pump 01*. Pump 01 has a star and has moved to the top of the table."
2. Read out the rationale: *"After 'Soil probe 000: Critical moisture drop…', operators issued TurnOn on Pump 01 in 2 of 2 previous occurrences."*
3. Click **Run**. The suggestion disappears because the operator has now acted on this occurrence.
4. Switch to `PredictiveEngine.cs`.
   > "It's sequential association-rule mining over a 10-minute sliding window. Conditions are processed critical alerts; actions are commands and searches. Confidence is the number of times the action followed the condition divided by earlier occurrences, with a minimum support of two. Unresolved faults and repeat offenders are added as insights."
5. Optionally open `http://localhost:5200/api/suggestions/stats`.

## 5:10 – 5:40 · Terminal proof

```bash
./scripts/simulate-devices.sh
```
> "The same behaviour from the command line, hitting the API like a fleet of devices: route `Priority`, then `SuppressedDuplicate`, and the processed alert shows how many queued packets it overtook."

## 5:40 – 6:00 · GitHub and Docker

```bash
git log --oneline | wc -l
docker compose up --build
```
> "Over 25 commits with descriptive messages, Dockerfiles and docker-compose, and a README section 11 that explains how to test every queue and dictionary. Thanks for watching."

---

### Shot checklist

- [ ] Burst first, then the power spike while the FIFO is still draining ("ahead of N queued packets" with N > 0)
- [ ] Duplicate suppressed on camera, then cleared with a normal reading
- [ ] Single undo **and** bulk undo
- [ ] Timeline graph with at least ~20 points
- [ ] Suggestion appears *before* searching, then disappears after **Run**
- [ ] `git log --oneline | wc -l` > 25
