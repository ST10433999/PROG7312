# PROG7312 · Smart-X IoT Mesh Ecosystem (Parts 1–3)

> Portfolio of Evidence: *The Smart-X IoT Mesh Ecosystem*. Final, fully functioning application.
> Backend: **ASP.NET Core Minimal API on .NET 10** · Frontend: **Blazor WebAssembly** · Shared domain library in C# · **xUnit** test suite · Docker Compose

Smart-X is a hybrid IoT ecosystem: thousands of ESP32 nodes in a mesh publish multi-typed telemetry (`float` soil moisture, `int` wattage, `bool` valve states) to a central gateway. The application has three integrated pillars, all reachable from one menu:

| Pillar | Part | What it does | Core data structures |
| --- | --- | --- | --- |
| 📡 **Sensor Data Ingestion & Telemetry** | 1 | Register sensors, ingest typed telemetry, attach encrypted configs/logs, watch the live **Node Pulse Board** with anomaly triage | `TelemetryPacket<T>` generics + operator overloading, jagged/multi-dimensional arrays, recursion, custom ring buffer |
| ⚡ **Real-Time Command Stream & History** | 2 | Stream processing, critical-alert fast lane, undoable overrides, timelines, a predictive suggestion engine | `Queue<T>`, `PriorityQueue`, `Stack<T>`, `Dictionary`, `SortedDictionary`, `HashSet` |
| 🕸 **Network Topology & Mesh Routing** | 3 | The mesh as a graph: route tracking by MAC, BFS reachability per subnet, self-healing after failures, a power-efficient MST, and instant historical range queries | Custom **AVL tree** (augmented), custom **binary min-heap**, **graph** (adjacency list), **BFS / DFS / Tarjan**, **Prim / Kruskal** MST (+ union–find), Dijkstra |

The Part 1 research report is in [`docs/PROG7312_POE_Part1_Research_Report.pdf`](docs/PROG7312_POE_Part1_Research_Report.pdf). Changes made in response to lecturer feedback are listed in [`docs/FEEDBACK_UPDATES.md`](docs/FEEDBACK_UPDATES.md).

---

## Contents

1. [Architecture](#1-architecture)
2. [Prerequisites](#2-prerequisites)
3. [Quick start (local): restore → compile → test → boot API → run client](#3-quick-start-local)
4. [Quick start (Docker)](#4-quick-start-docker)
5. [Using the application](#5-using-the-application)
6. [API reference (Part 1)](#6-api-reference)
7. [How the Part 1 technical requirements are met](#7-how-the-briefs-technical-requirements-are-met)
8. [Configuration](#8-configuration)
9. [Project structure](#9-project-structure)
10. [Troubleshooting](#10-troubleshooting)
11. [Part 2: Real-Time Command Stream & History](#11-part-2--real-time-command-stream--history)
12. [Part 3: Network Topology & Mesh Routing](#12-part-3--network-topology--mesh-routing)
13. [Automated tests](#13-automated-tests)
14. [Updates made from lecturer feedback](#14-updates-made-from-lecturer-feedback)

---

## 1. Architecture

```
┌────────────────────────────────────┐   HTTP/JSON (async)   ┌──────────────────────────────────────────────┐
│  SmartX.Client  (Blazor WASM)      │ ────────────────────▶ │  SmartX.Api  (.NET 10 Minimal API)            │
│  · Home: 3 pillars, live counts    │ ◀──────────────────── │  Part 1  GatewayState, NodeChannel<T>,        │
│  · Part 1 ingestion pages          │   multipart uploads   │          HistoricalBatchStore<T>[][],         │
│  · Part 2 /command-stream          │                       │          TopologyValidator, AttachmentService │
│  · Part 3 /topology                │                       │  Part 2  CommandStreamState, StreamRouter,     │
│  · UiState: filters & selection    │                       │          CommandWorker, PredictiveEngine       │
│    survive pillar switches         │                       │  Part 3  MeshNetworkService (graph, MST, BFS), │
│                                    │                       │          MeshMonitor, TelemetryHistoryIndex    │
└──────────────────┬─────────────────┘                       └──────────────────────┬───────────────────────┘
                   │              ┌────────────────────────────────────┐            │
                   └─────────────▶│  SmartX.Shared                      │◀───────────┘
                                  │  TelemetryPacket<T>, DTOs,          │
                                  │  AvlTree<K,V>, BinaryMinHeap<T>,    │
                                  │  DisjointSet, ring buffer, …        │
                                  └────────────────────────────────────┘
                                  tests/SmartX.Tests (xUnit): 34 tests over the structures and services
```

* **`SmartX.Shared`**: class library referenced by both ends. Holds every DTO with its DataAnnotations, so the **same validation rules run in the browser and on the server**. It also holds the custom data structures: `TelemetryPacket<T>`, `TelemetryRingBuffer<T>`, `HistoricalBatchStore<T>`, and in Part 3 `AvlTree<TKey,TValue>`, `BinaryMinHeap<T>` and `DisjointSet`.
* **`SmartX.Api`**: Minimal API. At start-up it seeds 240 sensors with 24 h of jagged-array history, 24 operational devices, a 7-day archive in the AVL history index (≈ 424 000 readings) and the mesh graph (245 vertices, ≈ 590 links). Three `BackgroundService`s keep it live:
  * `TelemetrySimulator` plays the ESP32 fleet.
  * `CommandWorker` drains the Part 2 queues.
  * `MeshMonitor` re-reads the Part 1 heartbeat and heals the mesh.
* **`SmartX.Client`**: Blazor WASM SPA. Every API call is `async`/`await` with a `CancellationToken` through the typed `GatewayApiClient`. Pages poll with `PeriodicTimer` and cancel cleanly when the operator navigates away. A singleton `UiState` keeps each pillar's filters and selection while the operator switches modules.

---

## 2. Prerequisites

| Tool | Version | Check |
|------|---------|-------|
| .NET SDK | **10.0** or later | `dotnet --list-sdks` must list `10.0.x` |
| A modern browser | Chrome / Edge / Firefox / Safari | WebAssembly support |
| *(optional)* Docker Desktop / Engine | 24+ with Compose v2 | `docker compose version` |
| *(optional)* Git, curl, python3 | any | for cloning and the smoke-test scripts |

Install the .NET 10 SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>. A .NET 8 or 9 SDK fails with *NETSDK1045: The current .NET SDK does not support targeting .NET 10.0*.

---

## 3. Quick start (local)

```bash
# 1. Clone
git clone https://github.com/ST10433999/PROG7312.git SmartX
cd SmartX

# 2. Restore NuGet dependencies for all four projects (Api, Client, Shared, Tests)
dotnet restore SmartX.slnx

# 3. Compile the whole solution (expect: 0 Warning(s), 0 Error(s))
dotnet build SmartX.slnx --no-restore

# 4. Run the unit tests (expect: Passed! – Failed: 0)
dotnet test SmartX.slnx --no-build
```

Then open **two terminals** in the repository folder:

**Terminal A: boot the .NET API**

```bash
dotnet run --project src/SmartX.Api
# → Now listening on: http://localhost:5200
# → Seeded 240 sensors, … historical packets in 24 jagged batches.
# → History index: … readings in 240 AVL trees (tallest 12).
# → Mesh graph built: 245 vertices, 591 links.
```

Start-up takes about two seconds while the archive is indexed. Sanity check: <http://localhost:5200/api/health> returns JSON with `"status": "ok"`, `meshNodes` and `historyReadings`. The OpenAPI document is at <http://localhost:5200/openapi/v1.json>.

**Terminal B: run the Blazor client**

```bash
dotnet run --project src/SmartX.Client
# → Now listening on: http://localhost:5100
```

Open **<http://localhost:5100>**. The sidebar shows a green dot with *API online · net10.0.x* once the client has reached the API. All three pillars are active on the home page.

> **Ports.** The API listens on **5200** and the client on **5100** (see each project's `Properties/launchSettings.json`). The client reads the API address from `src/SmartX.Client/wwwroot/appsettings.json` (`ApiBaseUrl`). If you change the API port, update that file and add the client origin to `Cors:AllowedOrigins` in `src/SmartX.Api/appsettings.json`.

**Smoke tests from a terminal** (API running):

```bash
./scripts/simulate-devices.sh     # Part 2: FIFO, PriorityQueue, HashSet, Stack, SortedDictionary
./scripts/mesh-demo.sh            # Part 3: MST, Kruskal cross-check, route, self-healing, BFS, AVL range query, benchmark
```

**Optional: publish a release build**

```bash
dotnet publish src/SmartX.Api    -c Release -o publish/api
dotnet publish src/SmartX.Client -c Release -o publish/client   # static files in publish/client/wwwroot
```

---

## 4. Quick start (Docker)

The repo ships a Dockerfile per project and a `docker-compose.yml` that wires them together.

```bash
docker compose up --build
```

| Service | URL | Notes |
|---------|-----|-------|
| `smartx-client` | <http://localhost:8081> | nginx serving the published WASM bundle |
| `smartx-api` | <http://localhost:8080/api/health> | ASP.NET Core; attachments persisted in the `smartx-attachments` volume |

The client container rewrites `appsettings.json` at start-up from the `API_BASE_URL` environment variable (default `http://localhost:8080/`), so the browser talks to the API published on the host. Stop with `docker compose down` (add `-v` to drop the attachments volume). Run the smoke tests against Docker with `./scripts/mesh-demo.sh http://localhost:8080`.

To run only the API in Docker and the client from `dotnet run`, set `Cors__AllowedOrigins__1=http://localhost:5100` (already included in the API image).

---

## 5. Using the application

| Page | Route | What to do |
|------|-------|-----------|
| **Gateway home** | `/` | Landing menu with the three pillars, all active, plus live counts (sensors, packets, mesh nodes online, indexed history). The sidebar shows badges for pending critical alerts and mesh nodes down, plus a shortcut to the last sensor you focused on. |
| **Sensor Ingestion** | `/ingestion` | The **Node Pulse Board**. Every node is a tile with a 60-sample sparkline, coloured *Healthy / Drift / Spike / Silent* and sorted worst-first. Click a tile to simulate a reading or report an issue into the **triage feed**. |
| **Register sensor** | `/ingestion/register` | Validated registration form (MAC, name, category, interval, facility → zone → sub-zone → node), with the **recursive placement check**. A new sensor joins the mesh graph automatically. |
| **Sensor registry / profile** | `/ingestion/sensors`, `/ingestion/sensors/{id}` | Profile, live statistics, 24 h history, encrypted **attachments**, and **🕸 Mesh route & history**, which opens the sensor in the topology pillar. |
| **Data structures** | `/ingestion/data-structures` | Part 1 evidence: operator overloading, jagged arrays, hourly grid, recursive validator trace. |
| **Command Stream** | `/command-stream` | Part 2 dashboard: device simulator, critical alerts, suggestions (with **Run/Show** and **✕ Dismiss** feedback), faults, device overrides with undo, history, sensor timeline. See [§11](#11-part-2--real-time-command-stream--history). |
| **Command history** | `/command-stream/history` | Full SortedDictionary history with filters. |
| **Mesh Topology** | `/topology`, `/topology/{mac}` | Part 3 dashboard: live mesh map, MST, route tracker, self-healing log, BFS reachability per subnet, AVL-indexed historical log, "under the hood" panel. See [§12](#12-part-3--network-topology--mesh-routing). |

---
## 6. API reference

Part 1 routes are below; Part 2 routes are in [§11.4](#114-part-2-api-reference) and Part 3 routes in [§12.6](#126-part-3-api-reference).

Base URL `http://localhost:5200/api` (Docker: `http://localhost:8080/api`).

| Method | Route | Purpose |
|--------|-------|---------|
| GET | `/health` | Liveness + counts |
| GET | `/sensors?search=&category=&page=&pageSize=` | List profiles |
| POST | `/sensors` | Register (400 with field errors / 409 on duplicate MAC) |
| GET · DELETE | `/sensors/{id}` | One profile |
| GET | `/sensors/by-mac/{mac}` | Lookup by MAC |
| GET · POST | `/sensors/{id}/attachments` | List / multipart upload (`files` field, multiple) |
| GET · DELETE | `/sensors/{id}/attachments/{attachmentId}` | Download (decrypts) / remove |
| POST | `/telemetry` | Ingest one reading `{ macAddress, kind: Float\|Int\|Bool, value \| state }` |
| POST | `/telemetry/batch` | Ingest an array of readings |
| GET | `/telemetry/{mac}/recent` | Rolling window (ring buffer) |
| GET | `/telemetry/{mac}/history` | 24 h history flattened from the jagged store |
| GET | `/telemetry/batches` | Jagged-array shape + flatten timing + hourly grid |
| GET | `/telemetry/aggregate-demo` | `Meter3 = Meter1 + Meter2` via operator overloading |
| GET | `/pulse?max=` | Fleet snapshot for the Pulse Board |
| GET · POST | `/triage` | Severity-ranked feed / report an issue |
| POST | `/triage/{id}/acknowledge?by=` | Acknowledge |
| GET | `/topology` | Deployment tree |
| GET | `/topology/validate` | Recursive whole-tree validation |
| POST | `/topology/validate-placement` | Recursive single-path check |
| GET | `/topology/path/{mac}` | Recursive search for a node's path |

Example:

```bash
curl -X POST http://localhost:5200/api/telemetry -H "content-type: application/json" \
     -d '{"macAddress":"A1:2F:00:00:28:48","kind":"Float","value":91.5}'
# → {"state":"Spike","zScore":38.2,"payloadType":"Single","message":"Spike: 91.5% is +38.2σ from the 60-sample baseline." ...}
```

---

## 7. How the brief's technical requirements are met

| Requirement | Where | Notes |
|-------------|-------|-------|
| **Generics — `TelemetryPacket<T>`** | `Shared/Telemetry/TelemetryPacket.cs` | `readonly record struct TelemetryPacket<T> where T : struct`. The JIT specialises per value type, so `float`, `int` and `bool` payloads are stored inline — **no boxing**. `TelemetryOps<T>` resolves arithmetic once per `T` (generic math `INumber<T>` for numerics, OR/XOR for `bool`) into cached delegates. |
| **Operator overloading** | same file | `+` aggregates (`Meter3 = Meter1 + Meter2`), `-` gives deltas, `> < >= <=` compare packets or a packet against a raw threshold, implicit unwrap to `T`. Used live by `/telemetry/aggregate-demo`. |
| **Jagged / multi-dimensional arrays → `List<T>`** | `Shared/Collections/HistoricalBatchStore.cs` | Historical batches land in `TelemetryPacket<T>[][]` (each hourly batch has its own length); `Flatten()` transfers them into a pre-sized, timestamp-sorted `List<T>`; `HourlyGrid()` builds a rectangular `double[24,3]`. |
| **Recursion** | `Shared/Topology/TopologyValidator.cs` | `ValidateTree` (depth-first, inherits/tightens tier config, rolls totals back up, explicit leaf base case and a depth guard) and `ValidatePlacement` (descends *Facility A → Zone 1 → Sub-Zone B*). Also recursive `FindPath`, `EnsurePath`, `CountSensors`. Runs on every registration. |
| **Collections / custom data structure** | `Shared/Collections/TelemetryRingBuffer<T>` | Fixed-capacity circular buffer with O(1) push and incrementally maintained mean/variance (z-score in O(1)). Gateway state uses `ConcurrentDictionary<,>` and `List<T>` throughout (triage feed, attachments, sparklines). |
| **Startup interface** | `Client/Pages/Home.razor` | Three pillars (Part 1: two were disabled; all three are active since Part 3). |
| **API integration layer** | `Api/Endpoints/*`, `Client/Services/GatewayApiClient.cs` | Minimal API route groups; fully asynchronous typed client with `CancellationToken`s. |
| **Sensor payload management** | `RegisterSensor.razor`, `SensorEndpoints.cs` | MAC, deployment location (facility/zone/sub-zone/node), category; shared DataAnnotations on both sides. |
| **Media/log attachment** | `SensorDetail.razor`, `AttachmentService.cs` | `InputFile` → `MultipartFormDataContent` streamed (not buffered) → `CryptoStream` AES-256-CBC with per-file IV; SHA-256 computed in the same pass; extension/size whitelist on both sides. |
| **Dynamic engagement feature** | `Ingestion.razor`, `GatewayState.BuildFleetPulse` | *Node Pulse Board with anomaly triage* — live tiles + z-score classification + heartbeat dropout detection + severity-ranked `List<TriageItem>` with cooldown and auto-resolve. Not a progress bar. |
| **Mock data seeding** | `Api/Services/MockDataSeeder.cs`, `TelemetrySimulator.cs` | 240 sensors, ~6 500 historical packets in 72 jagged batches, live simulator with spikes and dropouts. Tune via `Seed:SensorCount` (tested to 5 000). |

---

## 8. Configuration

`src/SmartX.Api/appsettings.json` (override with environment variables using `__` as separator, e.g. `Seed__SensorCount=1000`):

| Key | Default | Meaning |
|-----|---------|---------|
| `Seed:SensorCount` | `240` | Mock sensors created at start-up |
| `Simulator:Enabled` | `true` | Run the ESP32 fleet simulator |
| `Simulator:TickMilliseconds` | `500` | Scheduler tick |
| `Simulator:SpikeProbability` | `0.004` | Per-packet chance of an injected spike |
| `Simulator:SilenceProbability` | `0.0015` | Per-packet chance a node goes silent |
| `Attachments:Root` | `App_Data/attachments` | Where encrypted files are written |
| `Attachments:AesKeyBase64` | *(empty → dev key)* | 32-byte base64 key for AES-256 |
| `Cors:AllowedOrigins` | `localhost:5100/7100/8080` | Client origins |

`src/SmartX.Client/wwwroot/appsettings.json`: `ApiBaseUrl` (default `http://localhost:5200/`).

---

## 9. Project structure

```
SmartX/
├─ SmartX.slnx                  solution: 3 src projects + tests
├─ docker-compose.yml
├─ docker/                      nginx.conf, client-entrypoint.sh
├─ docs/                        Part 1 research report, Part 2/3 demo scripts, FEEDBACK_UPDATES.md
├─ scripts/                     simulate-devices.sh (Part 2), mesh-demo.sh (Part 3)
├─ README.md
├─ src/
│  ├─ SmartX.Shared/            Telemetry/ (TelemetryPacket<T>, TelemetryOps<T>)
│  │                            Collections/ (TelemetryRingBuffer<T>, HistoricalBatchStore<T>, AvlTree, BinaryMinHeap, DisjointSet)
│  │                            Topology/ (DeploymentNode, recursive TopologyValidator) · Models/ · Commands/ (Part 2)
│  │                            Mesh/, History/ (Part 3 DTOs)
│  ├─ SmartX.Api/               Program.cs, Endpoints/, Services/ (Parts 1–2), Mesh/ and History/ (Part 3), Dockerfile
│  └─ SmartX.Client/            Pages/, Components/ (incl. Components/Mesh), Layout/, Services/, wwwroot/css/{app,part2,part3}.css
└─ tests/
   └─ SmartX.Tests/             xUnit tests for the data structures and services
```

---

## 10. Troubleshooting

| Symptom | Fix |
|---------|-----|
| Sidebar says **API unreachable** | Start the API first (`dotnet run --project src/SmartX.Api`) and confirm <http://localhost:5200/api/health>. |
| Browser console shows a **CORS** error | The client origin must be listed under `Cors:AllowedOrigins` in the API's `appsettings.json` (or `Cors__AllowedOrigins__N` env var). |
| `dotnet` says the SDK is missing | Install .NET **10** (`dotnet --list-sdks` must show `10.0.x`). `global.json` is not pinned, so any 10.x works. |
| Port already in use | Change `applicationUrl` in the relevant `Properties/launchSettings.json`, then update `ApiBaseUrl` / CORS as above. |
| Upload rejected | Only `.json .yaml .yml .cfg .ini .txt .log .csv .png .jpg .jpeg .webp .pdf .bin`, ≤ 25 MB per file, ≤ 10 files per request. |
| `NETSDK1045: The current .NET SDK does not support targeting .NET 10.0` | An older SDK is first on the PATH. Install .NET 10 and check `dotnet --list-sdks`. |
| Topology map empty or *Building the mesh graph…* | The API is still seeding (≈ 2 s) or unreachable. Check `/api/health` shows `meshNodes`. |
| Nodes turn grey on their own | Expected: the Part 1 simulator drops random nodes, the heartbeat marks them silent, and the mesh heals. Set `Mesh:UseHeartbeat` to `false` to see only operator-made failures. |
| Docker: client loads but no data | The browser must reach the API on the host: set `API_BASE_URL` on the client service to the API's public URL and add that client origin to the API's CORS list. |


---

## 11. Part 2 — Real-Time Command Stream & History

Part 2 turns the gateway into a real-time processing engine. Devices publish telemetry to a new stream endpoint; normal packets join a FIFO queue, critical packets jump it, operators send undoable overrides to actuators, and a predictive engine learns how operators respond to alerts so it can suggest the response next time.

```
 ESP32 / simulator ──POST /api/stream/telemetry──▶ StreamRouter
                                                   │  normal ─────────────▶ Queue<TelemetryIngestRequest>  (FIFO)
                                                   │  critical (new) ─────▶ PriorityQueue<CriticalAlert,(severity,seq)>
                                                   │  critical (repeat) ──▶ dropped by HashSet<string> active faults
                                                   ▼
                              CommandWorker (every 250 ms): PriorityQueue first → ≤ 8 FIFO packets → commands
                                                   │
          Part 1 pipeline (TelemetryPacket<T>, ring buffer, z-score) ◀──┘──▶ SortedDictionary timeline
 Dashboard ──POST /api/commands[/bulk]──▶ Queue<CommandEnvelope> ──▶ Dictionary<string,SmartDevice> ──▶ Stack<CommandBatch> (undo)
                                                                                                  └──▶ SortedDictionary history
 Alerts + operator actions ──▶ PredictiveEngine (sequential association rules) ──▶ "Suggested actions & insights"
```

### 11.1 Data structures and where to see them

| Rubric item | Collection | Code | Visible on `/command-stream` |
| --- | --- | --- | --- |
| Message queue (FIFO) | `Queue<TelemetryIngestRequest>` | `CommandStreamState._telemetryQueue` | **Telemetry FIFO** counter. The worker drains 8 packets every 250 ms, so a burst builds a backlog you can watch shrink. |
| Priority queue | `PriorityQueue<CriticalAlert, (int Severity, long Sequence)>` | `CommandStreamState._criticalAlerts` | **Critical alerts** card: each processed alert shows its latency and *"ahead of N queued packets"*. The sequence number keeps equal-severity alerts in arrival order (the BCL priority queue is not stable on its own). |
| Stack (undo) | `Stack<CommandBatch>` | `CommandStreamState._undoStack` | **↶ Undo** button (shows the action it will revert). A bulk override such as *Shut down all valves* is one stack entry, so one Undo restores every valve to its previous state. |
| Dictionary — O(1) lookup | `Dictionary<string, SmartDevice>` keyed by MAC | `CommandStreamState._devices` | Device table, commands, E-STOP and suggestions all resolve devices by MAC. Sensors are resolved through Part 1's `ConcurrentDictionary` on every streamed packet. |
| Sorted dictionary — timeline | `SortedDictionary<StreamKey, TimelinePoint>` | `CommandStreamState._timeline` | **Sensor timeline** graph – values come out already in timestamp order, so the graph needs no sort. |
| Sorted dictionary — history | `SortedDictionary<StreamKey, CommandRecord>` | `CommandStreamState._commandHistory` | **Recent history** panel and `/command-stream/history`. `StreamKey` = (timestamp, sequence) so two items on the same tick never collide. |
| Hash set — unique faults | `HashSet<string>` | `CommandStreamState._activeFaults` | **Active faults** card and the *duplicates dropped* counter. `Add()` returning false is what suppresses a repeat alert for a node that is already faulted. |
| Predictive engine | `Dictionary<string, Dictionary<string,DecayingCounter>>` pair weights + `Queue<ConditionEvent>` sliding window (decay, pruning and feedback added after Part 2 feedback) | `PredictiveEngine` | **🧠 Suggested actions & insights** panel with **Run/Show** and **✕ Dismiss**; suggested devices get a ★ and move to the top of the table. |

### 11.2 How the predictive engine works

The engine mines **sequential association rules** over a 10-minute sliding window:

1. *Conditions* are processed critical alerts – e.g. `Soil probe 000: Critical moisture drop: 12% (below 20%)`, a sensor disconnect, or an operator E-STOP.
2. *Actions* are what the operator does next – issuing a command on a device, or searching for a device on the dashboard (searches are reported to `POST /api/suggestions/search` after an 800 ms debounce and count only when they identify exactly one device).
3. Each action is credited once to every condition in the window. When a condition fires again, every action that has followed it at least **twice** is proposed with
   `confidence = weight of (condition → action) ÷ weight of earlier occurrences of the condition`.
4. Insights are added on top: devices or sensors with unresolved faults, and nodes that raised three or more alerts in the window.
5. **Since Part 3 (lecturer feedback):** every count is a `DecayingCounter` (half-life `Predictive:HalfLifeHours`, default 6 h), so recent behaviour outweighs old behaviour. A rule needs a *recency-weighted* support of 1.5. A prune pass removes patterns that have faded below 0.05 and caps rules per condition. Operators give **feedback**: *Run/Show* reinforces the rule; *✕ Dismiss* halves it, hides it for 30 minutes and lowers its confidence through a smoothed acceptance factor (`POST /api/suggestions/feedback`).

This is the brief's example: if an operator searches for *Pump 01* after *Soil probe 000* drops below 20 %, the third time the moisture drops Pump 01 is highlighted and *TurnOn · Pump 01* is offered before anyone searches.

### 11.3 How to test it

Start the API and client (`dotnet run --project src/SmartX.Api`, `dotnet run --project src/SmartX.Client`) and open <http://localhost:5100/command-stream>. Everything below can also be run from a terminal with `./scripts/simulate-devices.sh` (pass `http://localhost:8080` when using Docker).

| What to prove | Steps | Expected |
| --- | --- | --- |
| **FIFO queue** | Click **📦 Burst 200 normal packets**. | *Telemetry FIFO* jumps to ~200 and drains at ~32 packets/s; *Packets processed* climbs. |
| **Simulate a high-priority alert** | Straight after the burst, click **⚡ Power spike (2 600 W)** (or **💧 Critical moisture drop**). | The toast says the packet went to the PriorityQueue; in **Critical alerts** it is processed within ~250 ms *"ahead of N queued packets"* while the FIFO is still draining. |
| **Same thing with curl** | `curl -X POST http://localhost:5200/api/stream/telemetry -H "content-type: application/json" -d '{"macAddress":"<meter MAC>","kind":"Int","value":2600}'` | `202 Accepted` with `"route":"Priority"`. |
| **HashSet duplicate suppression** | Click **⚡ Power spike** again for the same meter. | `"route":"SuppressedDuplicate"`; *duplicates dropped* goes up by one and no second alert appears. Send a normal value (e.g. 1230) and the fault clears, so the next spike alerts again. |
| **Stack undo** | Click **On** for a pump, then **↶ Undo: TurnOn Pump 01**. Then click **⛔ Shut down all valves** and **↶ Undo** once. | The pump returns to its previous state; one Undo restores all four valves. Undone records are struck through in the history. |
| **E-STOP** | Click **E-STOP** on any device, then **Reset**. | The device goes STOPPED and into the fault set; Reset clears it. |
| **Dictionary lookup / search** | Type `Pump 01` (or a MAC fragment) in the device search box. | The table filters instantly; after 0.8 s the search is reported to the engine. |
| **Sorted timeline** | Pick the soil probe or meter in **📈 Sensor timeline**. | Processed values plotted in timestamp order, including critical points. |
| **Predictive engine** | Choose *Soil probe 000*, then repeat twice: **💧 Critical moisture drop** → search `Pump 01` → **On** for Pump 01 → set value to `48` and **Send reading** (clears the fault). Then click **💧** a third time. | *Run: TurnOn · Pump 01* (100 %, seen 2×) and *Check Pump 01* appear before you search; Pump 01 gets a ★ and moves to the top. `GET /api/suggestions/stats` shows the learned rules. |

Thresholds (in `StreamRouter.cs`): soil moisture below 20 % → Critical, above 85 % → High; power above 2 000 W → Critical.

### 11.4 Part 2 API reference

Base URL `http://localhost:5200/api` (Docker: `http://localhost:8080/api`).

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/stream/telemetry` | A device publishes one reading. `202` with `route` = `Fifo`, `Priority` or `SuppressedDuplicate`. |
| POST | `/stream/telemetry/batch` | A burst of readings (≤ 2 000). |
| GET | `/stream/queue` | FIFO depth, pending critical alerts, command queue depth, processed/rejected counts. |
| GET | `/stream/timeline?mac=&take=` | Processed readings in timestamp order (SortedDictionary). |
| GET | `/stream/timeline/sensors` | Sensors present in the timeline. |
| GET | `/commands/stream` | Everything the dashboard renders, in one call. |
| POST | `/commands` | Override one device: `{ macAddress, command, priority, reason }`. |
| POST | `/commands/bulk` | Override every device of a `kind` and/or `category`. |
| POST | `/commands/undo` | Pop the last action off the Stack and restore previous states. |
| GET | `/commands/history?mac=&take=` | Executed commands, newest first. |
| GET | `/alerts/pending` · `/alerts/recent` | Alerts waiting in / processed from the PriorityQueue. |
| POST | `/alerts/critical` | Raise an alert manually (dropped if the node is already faulted). |
| POST | `/alerts/{id}/acknowledge` | Mark a processed alert as dispatched. |
| GET | `/devices?q=&inAlert=` · `/devices/{mac}` | Search the registry / O(1) lookup by MAC. |
| GET | `/devices/faults` · POST `/devices/{mac}/clear-fault` | The active-fault HashSet. |
| GET | `/suggestions` · `/suggestions/stats` | Recommendations / what the engine has learned. |
| POST | `/suggestions/search` | Report an operator search `{ term }`. |

### 11.5 Configuration added in Part 2

| Key | Default | Meaning |
| --- | --- | --- |
| `Seed:DeviceCount` | `24` | Operational devices seeded into the Dictionary (pumps, valves, relays, heating pads, fans, grow lights). |
| `Stream:TelemetryPerTick` | `8` | FIFO packets processed per 250 ms tick (8 → 32 packets/s). Raise it to drain bursts faster. |

### 11.6 Files added or changed in Part 2

```
src/SmartX.Shared/Commands/                        NEW   DeviceModels, CommandModels, StreamModels, AlertModels,
                                                         SuggestionModels, CommandStreamSnapshot (shared DTOs)
src/SmartX.Api/Services/CommandStreamState*.cs     NEW   one partial file per structure: Dictionary (core), Queue (Telemetry),
                                                         PriorityQueue + HashSet (Alerts), SortedDictionary (Timeline),
                                                         command Queue + Stack + SortedDictionary (Commands)
src/SmartX.Api/Services/TelemetryRules.cs          NEW   thresholds that make a reading critical
src/SmartX.Api/Services/StreamRouter.cs            NEW   FIFO / PriorityQueue / duplicate routing
src/SmartX.Api/Services/PredictiveEngine*.cs       NEW   observation + suggestion halves of the engine
src/SmartX.Api/Services/CommandWorker.cs           NEW   BackgroundService that processes the queues
src/SmartX.Api/Services/DeviceRegistrySeeder.cs    NEW   seeds the operational devices
src/SmartX.Api/Endpoints/*Endpoints.cs             NEW   Stream, Command, Alert, Device, Suggestion groups + CommandStreamEndpoints
src/SmartX.Api/Program.cs, appsettings.json        EDIT  Part 2 services, endpoints and settings
src/SmartX.Client/Pages/CommandStream.razor        NEW   live dashboard
src/SmartX.Client/Pages/CommandHistory.razor       NEW   full command history
src/SmartX.Client/wwwroot/css/part2.css            NEW   Part 2 styles; linked from wwwroot/index.html
src/SmartX.Client/Pages/Home.razor, Layout/MainLayout.razor, Services/GatewayApiClient.cs, _Imports.razor   EDIT
docker-compose.yml                                 EDIT  Seed__DeviceCount, Stream__TelemetryPerTick
scripts/simulate-devices.sh                        NEW   curl device simulator / smoke test
docs/Part2_Demo_Script.md                          NEW   video walkthrough
```


---

## 12. Part 3 — Network Topology & Mesh Routing

Part 3 completes the ecosystem. The ESP32 fleet is modelled as a **mesh network graph**. The gateway now:

* tracks how each sensor's telemetry hops to the central server,
* finds which devices a gateway or subnet can still reach,
* rebuilds the most power-efficient routing tree when a node fails, which is the "self-healing" behaviour of Zigbee, Thread and ESP-NOW meshes,
* answers historical questions such as "every moisture reading between 14:00 and 16:00 for Node A" from a self-balancing tree instead of scanning a list.

```
 Part 1 sensor registry ──▶ MeshTopologyBuilder ──▶ MeshGraph (adjacency list, Dictionary<MAC, vertex>)
                                                      │
 Part 1 heartbeat (silent nodes) ──┐                  ▼
 operator "Simulate failure" ──────┴──▶ MeshNetworkService ──▶ Prim (BinaryMinHeap) / Kruskal (DisjointSet) → MST
        MeshMonitor every 2 s                │                 Dijkstra (BinaryMinHeap) → cheapest single paths
                                             │                 BFS (Queue) → reachability per subnet, hop levels
                                             │                 DFS (Stack) → islands · Tarjan → critical relays
                                             ▼
                                  HealingEvent (who was re-routed, who is isolated)

 Part 1 ingestion (live) + jagged batches + 7-day archive ──▶ TelemetryHistoryIndex
                                  Dictionary<MAC, AvlTree<(timestamp, seq), value>> ──▶ range queries, hourly buckets
```

### 12.1 The mesh being modelled

The facility is a 160 m × 100 m floor plan. Each **zone is a subnet** occupying one quadrant, with a **border router** in its centre. The routers connect to the **central gateway** over a wired PoE backbone. Every sensor links by radio to its 4 nearest neighbours within range, and nodes near a zone boundary link across it, which is what lets traffic escape a subnet whose router has failed. Each link carries two numbers from `RadioModel`:

* **Energy per frame**: the first-order radio model (Heinzelman, Chandrakasan and Balakrishnan, 2000), `E = 2·E_elec·k + ε_amp·k·d²` with `E_elec = 50 nJ/bit`, `ε_amp = 100 pJ/bit/m²` and `k = 2 000 bits`. This is the **edge weight** used by every algorithm.
* **RSSI**: the log-distance path-loss model, `RSSI = −40 − 27·log₁₀(d)` dBm. Links weaker than −78 dBm (≈ 25 m) are not created.

### 12.2 Data structures and their role

| Brief item | Structure | Code | Role in the "Network Topology" feature |
| --- | --- | --- | --- |
| BST / AVL / Red-Black tree | **`AvlTree<TKey,TValue>`**, written from scratch and augmented with subtree *size* and *sum/min/max* | `Shared/Collections/AvlTree.cs` | One tree per sensor holds its timestamped history. Range queries, counts and min/avg/max over any time window run in O(log n), so the historical log never iterates a large unsorted list. |
| Heap | **`BinaryMinHeap<T>`**, array-backed, from scratch | `Shared/Collections/BinaryMinHeap.cs` | Priority queue of candidate radio links for Prim's MST and of tentative distances for Dijkstra. |
| Graph | **`MeshGraph`**: adjacency list (`List<MeshEdge>` per vertex) + `Dictionary<MAC, vertex>` | `Api/Mesh/MeshGraph.cs`, `MeshTopologyBuilder.cs` | Vertices = ESP32 sensors, zone routers and the gateway; edges = wireless (or PoE) links weighted by energy. |
| Graph traversal: BFS | `Queue<T>` | `Api/Mesh/GraphTraversal.cs` → `BreadthFirst` | "Which devices can the gateway / Zone 2's router reach, in how many hops?" Lists reached devices level by level, the ones that are down, and powered devices that were cut off, each with **where to hop instead**. |
| Graph traversal: DFS | `Stack<T>` (iterative) | `GraphTraversal.ConnectedComponents`, `ArticulationPoints` | Counts the islands the mesh has split into, and finds **critical relays** (articulation points, Tarjan's algorithm), single points of failure flagged on the map before anything fails. |
| Minimum spanning tree | **Prim** (min-heap, lazy deletion) and **Kruskal** (sort + `DisjointSet` union–find) | `Api/Mesh/SpanningTrees.cs`, `Shared/Collections/DisjointSet.cs` | The most power-efficient routing backbone: every sensor gets exactly one uplink and the total energy of the tree is minimal. Rebuilt whenever the set of down nodes changes, and that rebuild is the self-healing. |
| Shortest path (extra) | **Dijkstra** (min-heap) | `SpanningTrees.Dijkstra` | Shown next to each tracked route for comparison (see 12.4). |

#### AVL tree: logarithmic range queries on history

Telemetry arrives in timestamp order, the worst case for a plain BST: it degenerates into a linked list of depth *n*. The AVL tree stores each subtree's height and repairs any node whose balance factor reaches ±2 with one or two rotations on the way back up from an insert or delete:

```csharp
// Shared/Collections/AvlTree.cs
private Node Rebalance(Node n)
{
    Update(n);                                                   // height, size, sum/min/max from the children
    var balance = HeightOf(n.Left) - HeightOf(n.Right);
    if (balance > 1)
    {
        if (HeightOf(n.Left!.Left) < HeightOf(n.Left.Right))
            n.Left = RotateLeft(n.Left);                         // left-right case
        return RotateRight(n);                                   // left-left case
    }
    if (balance < -1)
    {
        if (HeightOf(n.Right!.Right) < HeightOf(n.Right.Left))
            n.Right = RotateRight(n.Right);                      // right-left case
        return RotateLeft(n);                                    // right-right case
    }
    return n;
}
```

Two augmentations go beyond a textbook AVL tree. Every node also caches the **size** of its subtree and the **sum/min/max** of the readings in it. The range walk prunes subtrees outside `[from, to]`, and a page deep inside a range skips whole subtrees using their size, so page *k* costs O(log n + page size), not O(k):

```csharp
// whole subtree inside the range and covered by the skip → jump it in O(1)
if (aboveFrom && belowTo && cursor.Skip >= n.Size) { cursor.Skip -= n.Size; return; }
```

The count and min/avg/max for any window come from at most two root-to-leaf paths, without touching the readings:

```csharp
// Api/History/TelemetryHistoryIndex.cs – one sensor, one window
var total     = tree.CountRange(lo, hi);          // O(log n) from subtree sizes
var aggregate = tree.Aggregate(lo, hi);           // O(log n) count/sum/min/max
var visited   = tree.Range(lo, hi, page, skip, take, newestFirst);   // O(log n + take)
```

The hourly summary chart is one `Aggregate` call per bucket: 168 buckets for a week, each O(log n). Keys are `(UTC ticks, sequence)` so two readings with the same timestamp never collide. The sensor's tree is found through a `Dictionary<MAC, …>` in O(1). Reads share a `ReaderWriterLockSlim`, and a retention cap evicts the oldest reading with an O(log n) delete-min.

**Measured scaling** (`GET /api/history/benchmark`, 200 random 2-hour windows, readings arriving in time order, .NET 10.0.13 on an Apple-silicon MacBook):

| Readings | Tree height (AVL bound) | AVL count | AVL min/avg/max | AVL fetch all matches | Linear scan of unsorted list | Speed-up |
| ---: | :---: | ---: | ---: | ---: | ---: | ---: |
| 200 000 | 18 (25) | 7.4 µs | 2.3 µs | 14.7 µs (≈ 556 matches) | 979.9 µs | **67×** |
| 500 000 | 19 (26) | 8.2 µs | 2.7 µs | 26.7 µs (≈ 1 389 matches) | 1 947.4 µs | **73×** |
| 1 000 000 | 20 (28) | 12.1 µs | 4.7 µs | 62.3 µs (≈ 2 778 matches) | 4 883.8 µs | **78×** |

Doubling the data adds about one level to the tree, while the linear scan doubles. A plain BST fed the same sorted timestamps would be 1 000 000 levels deep. Results are checked against the scan on every run (`resultsMatch: true`).

#### Graph, BFS and DFS

```csharp
// Api/Mesh/GraphTraversal.cs – BFS confined to one subnet, skipping nodes that are down
var queue = new Queue<MeshVertex>();
depth[root.Index] = 0; queue.Enqueue(root);
while (queue.TryDequeue(out var v))
    foreach (var link in v.Links)                       // adjacency list: O(degree)
    {
        var next = link.Other(v);
        if (depth[next.Index] >= 0 || !canEnter(next)) continue;   // seen, down, or outside the subnet
        depth[next.Index] = depth[v.Index] + 1;
        via[next.Index] = link;
        queue.Enqueue(next);
    }
```

BFS visits every vertex and edge once: O(V + E). For each powered device the search could **not** reach, the service picks the neighbour that is closest (in hops) to the gateway and gives the advice *"Zone 2 router cannot reach it inside Zone 2. Hop across to Soil probe 141 (Zone 1), which reaches the gateway in 9 hop(s)"*. That answers the brief's question "identify which ones are down / where to hop in that case". The iterative DFS labels connected components, and Tarjan's low-link DFS marks articulation points: failing one of those always isolates part of the mesh, which a unit test checks.

#### Minimum spanning tree: Prim and Kruskal

```csharp
// Api/Mesh/SpanningTrees.cs – Prim's algorithm grown from the gateway
while (heap.TryPop(out var c))                 // cheapest candidate link: O(log E)
{
    if (inTree[c.To]) continue;                // stale entry (lazy deletion)
    var link = graph.Edges[c.Edge];
    inTree[c.To] = true;
    parent[c.To] = link;                       // this node's uplink toward the gateway
    total += link.EnergyMicroJoules;
    AddFrontier(graph.Vertices[c.To]);         // push its links to nodes not yet in the tree
}
```

Prim runs in O(E log V). Kruskal sorts the links once and lets the `DisjointSet` reject any link that would close a cycle: O(E log E). Union by rank and path compression make each union–find step effectively constant. Both are exposed through the **MST algorithm** toggle. The "Under the hood" panel shows the first links each one accepts, and a cross-check confirms both reach the same total energy, which they must because the weight of a minimum spanning tree is unique. Down nodes are simply excluded from the frontier, so a rebuild after a failure *is* the self-healing. The diff between the old and new trees is recorded as a **healing event**: which sensors changed uplink, which became isolated, which reconnected.

### 12.3 Self-healing and the Part 1 heartbeat

A node is **down** when an operator takes it offline (**⚠ Simulate failure** / **⚡ Fail busiest relay**). It is also down when the **Part 1 heartbeat** sees it miss three publish intervals: `MeshMonitor` checks every 2 s, so the simulator's random dropouts take nodes out of the mesh and bring them back without anyone on the page. Each change rebuilds the tree in about a millisecond and logs a healing event. The **🩹 Self-healing log** shows the event, and a re-routed sensor's old route is drawn as a pink dotted line under its new route.

### 12.4 MST routes vs Dijkstra: the trade-off, measured

Every tracked route shows the MST path **and** Dijkstra's cheapest single path. They optimise different things, and the dashboard reports both totals (*Under the hood → Spanning tree → Per round*):

| Per reporting round, all 244 nodes | MST | Dijkstra shortest-path tree |
| --- | ---: | ---: |
| **With in-network aggregation**: each relay merges what it forwards into one frame (Part 1's `TelemetryPacket<T>` `+` operator), so a round costs the tree's total weight | **49.9 mJ** (minimum possible) | 51.0 mJ |
| **Without aggregation**: every packet relayed separately along its own route | 414.3 mJ | **164.5 mJ** |

Smart-X relays aggregate, so the **MST is the most power-efficient routing backbone**. It is also the structure the brief asks for. The cost is longer chains: 9.2 hops on average against 3–6 for Dijkstra, which raises latency. The Part 3 report discusses when a production mesh (RPL, Zigbee) would prefer the shortest-path tree instead.

### 12.5 How to test Part 3

Start the API and client (§3) and open <http://localhost:5100/topology>. The same checks run from a terminal with `./scripts/mesh-demo.sh`.

| What to prove | Steps | Expected |
| --- | --- | --- |
| **Graph built** | Open the page. | 245 nodes (240 sensors, 4 routers, gateway), ≈ 591 links, 1 DFS component; teal lines are the MST, purple dashes the PoE backbone. |
| **Track a route by MAC** | Click any node, or type a MAC/name (e.g. `A1:2F:00:00:28:48`) in **🛰 Route tracker** → **Track**. | Yellow animated route to the gateway with energy per hop; Dijkstra's path and the stretch are listed below. The URL becomes `/topology/{mac}` so it can be bookmarked. |
| **Prim vs Kruskal** | Switch **MST algorithm**; open **🧮 Under the hood → Show how … built the tree**. | Same total energy, ✔ identical; different acceptance order, timing and operation counts. |
| **Self-healing** | Click **⚡ Fail busiest relay** (usually a zone router). | Toast *Mesh healed: N re-routed*; a new entry in **🩹 Self-healing log** lists each sensor's old → new uplink; the tracked node shows *↻ re-routed* and its old path in pink. **♻ Restore all** reverses it. |
| **BFS per subnet / where to hop** | With a router failed, choose that zone in **🔎 Subnet reachability** → **Run BFS**. | *0 / N reached* from the (down) router, every powered sensor listed under *Where cut-off devices can hop* with a neighbour in another zone. With the gateway selected, all powered nodes are reached and listed per hop level. |
| **Critical relays (DFS)** | Fail a node next to a chain end, then look at *Critical relays*. | Articulation points appear with a dashed yellow ring; failing one isolates nodes (amber). |
| **Historical range query** | Select a sensor, click **3 days ago 14:00–16:00** (or set *From/To*). | 25 readings at 5-minute spacing, min/avg/max, page of rows with deltas, and *visited ≈ 15 of ≈ 1 770 nodes, tree height 12 (AVL bound 15)*. **7 days** draws the hourly band chart from 168 O(log n) aggregates. |
| **Scaling** | **🧮 Under the hood → AVL benchmark → 1 000 000 readings → Run**. | ≈ 70–80× faster than the linear scan; tree height ≈ 20. |
| **Heartbeat integration** | Leave the page open for a minute. | Silent sensors from the Part 1 simulator turn grey, the mesh heals around them and logs *"… went silent (Part 1 heartbeat)"*. |

### 12.6 Part 3 API reference

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/mesh?algorithm=Prim\|Kruskal` | Nodes (position, status, hops, uplink, downstream count, critical flag), links (RSSI, energy, in-tree), statistics, recent healing events. |
| GET | `/mesh/route/{mac}` | Track a sensor: MST path, Dijkstra path, stretch, previous path, explanation. `404` for an unknown MAC. |
| GET | `/mesh/reachability?subnet=Zone 2&root={mac}` | BFS from the gateway (default) or a subnet's router: levels, down, cut-off with hop advice. `400` for an unknown subnet. |
| GET | `/mesh/spanning-tree?algorithm=&steps=` | How the MST was built: first steps, totals, timing, operations, cross-check with the other algorithm. |
| GET | `/mesh/healing?take=` | Recent self-healing events. |
| POST | `/mesh/nodes/{mac}/fail` · `/mesh/nodes/{mac}/restore` | Simulate a hardware failure / bring the node back (`400` for the gateway). |
| POST | `/mesh/fail-busiest` · `/mesh/restore-all` | Fail the relay with the most downstream traffic / restore every failed node. |
| GET | `/history/{mac}?from=&to=&skip=&take=&order=desc\|asc&bucketMinutes=&compare=` | AVL range query (ISO 8601 times, up to 500 rows per page, `400` if `from > to`). |
| GET | `/history/stats` | Readings, trees, tallest tree, rotations, evictions. |
| GET | `/history/benchmark?points=&queries=&windowHours=` | AVL vs linear scan on up to 2 000 000 synthetic readings. |
| POST | `/suggestions/feedback` | Feedback fix: `{ ruleKey, verdict: Accepted\|Dismissed }`. |

```bash
curl "http://localhost:5200/api/mesh/route/A1:2F:00:00:28:48"
curl -X POST "http://localhost:5200/api/mesh/fail-busiest"
curl "http://localhost:5200/api/mesh/reachability?subnet=Zone%202"
curl -G "http://localhost:5200/api/history/A1:2F:00:00:28:48" \
     --data-urlencode "from=2026-11-10T14:00:00+02:00" --data-urlencode "to=2026-11-10T16:00:00+02:00"
```

### 12.7 Configuration added in Part 3

| Key | Default | Meaning |
| --- | --- | --- |
| `History:ArchiveDays` | `7` | Days of archived history indexed at start-up (the last 24 h come from the Part 1 jagged batches). |
| `History:ArchiveIntervalMinutes` | `5` | Spacing of archived readings per sensor. |
| `History:MaxPointsPerSensor` | `10000` | Retention cap per AVL tree; the oldest reading is evicted with an O(log n) delete-min. |
| `Mesh:UseHeartbeat` | `true` | Treat sensors the Part 1 heartbeat considers silent as down. |
| `Mesh:RefreshMilliseconds` | `2000` | How often `MeshMonitor` re-checks the heartbeat and registry. |
| `Predictive:HalfLifeHours` | `6` | Feedback fix: how quickly learned operator behaviour decays. |

### 12.8 Files added or changed in Part 3

```
src/SmartX.Shared/Collections/AvlTree.cs, BinaryMinHeap.cs, DisjointSet.cs        NEW  custom data structures
src/SmartX.Shared/Mesh/MeshModels.cs, RouteModels.cs                             NEW  topology DTOs
src/SmartX.Shared/History/HistoryModels.cs                                       NEW  history DTOs
src/SmartX.Api/Mesh/RadioModel.cs, MeshGraph.cs, MeshTopologyBuilder.cs          NEW  the graph
src/SmartX.Api/Mesh/GraphTraversal.cs, SpanningTrees.cs                          NEW  BFS, DFS, Tarjan, Prim, Kruskal, Dijkstra
src/SmartX.Api/Mesh/MeshNetworkService*.cs, MeshMonitor.cs                       NEW  live mesh, self-healing, queries
src/SmartX.Api/History/*.cs                                                      NEW  HistoryKey, TelemetryHistoryIndex, archive seeder, benchmark
src/SmartX.Api/Endpoints/MeshEndpoints.cs, HistoryEndpoints.cs                   NEW  /api/mesh, /api/history
src/SmartX.Api/Services/DecayingCounter.cs, PredictiveEngine.Feedback.cs         NEW  feedback fix (decay, pruning, feedback)
src/SmartX.Api/Services/GatewayState.cs, PredictiveEngine*.cs, Program.cs, …     EDIT live packets into the AVL index, engine fix, wiring
src/SmartX.Client/Pages/Topology.razor, Components/Mesh/*.razor                  NEW  topology dashboard
src/SmartX.Client/Services/UiState.cs, GatewayApiClient.cs                       NEW/EDIT state across pillars, Part 3 calls
src/SmartX.Client/Layout/MainLayout.razor, Pages/Home.razor, CommandStream.razor EDIT pillar 3 enabled, badges, feedback buttons
src/SmartX.Client/wwwroot/css/part3.css                                          NEW  Part 3 styles
tests/SmartX.Tests/*                                                             NEW  xUnit suite
scripts/mesh-demo.sh, docs/Part3_Demo_Script.md, docs/FEEDBACK_UPDATES.md        NEW
```

---

## 13. Automated tests

`tests/SmartX.Tests` (xUnit, 34 tests) runs with `dotnet test SmartX.slnx`. Each data structure is checked against a trusted reference:

| Area | What the tests prove |
| --- | --- |
| `AvlTree` | 100 000 sorted inserts stay within the AVL height bound; 20 000 random inserts/deletes match `SortedDictionary` exactly and keep every invariant (`Validate()` checks order, balance, cached height and size); paging in both directions matches LINQ; deep paging visits < 200 nodes; range aggregates match LINQ and visit O(log n) nodes; rank/select/delete-min. |
| `BinaryMinHeap`, `DisjointSet` | 10 000 random pushes pop in sorted order and keep the heap property; union–find rejects a cycle. |
| Graph algorithms | BFS depths and blocking; DFS islands; articulation points on a chain and a ring; Prim and Kruskal equal a **brute-force** minimum over every spanning subset; routing around a down node; Dijkstra never costs more than the tree path; radio model bounds. |
| `MeshNetworkService` | Seeded fleet starts fully connected; Prim = Kruskal; routes start at the sensor and end at the gateway; failing the busiest relay re-routes nodes and restoring undoes it; BFS per subnet gives hop advice; critical nodes really are single points of failure; MST is cheapest per aggregated round, Dijkstra per un-aggregated round. |
| `TelemetryHistoryIndex` | A 14:00–16:00 window returns exactly 25 readings newest-first; paging, deltas (including across page edges), hourly buckets covering every reading once; retention eviction; benchmark results equal the linear scan. |
| Predictive engine (feedback fix) | Still learns after two episodes; two-day-old behaviour decays below the threshold and is pruned; dismiss snoozes and lowers confidence; accept reinforces. |

---

## 14. Updates made from lecturer feedback

Part 1 received 100/100 and Part 2 97/100. The two Part 2 deductions and every fix made since are recorded in **[`docs/FEEDBACK_UPDATES.md`](docs/FEEDBACK_UPDATES.md)**. In summary:

* **Predictive engine** ("counts never decay and are never pruned … there is no operator feedback"). Counts are now `DecayingCounter`s with a configurable half-life. A prune pass removes weak patterns and caps memory. Operators accept or **✕ Dismiss** each suggestion, which reinforces or halves the rule, snoozes it and scales future confidence. Covered by three unit tests.
* **Main menu & pillar state** (29/30). Filters, pause state and selected sensors survive switching pillars (`UiState`). The sidebar shows live badges, and all three pillars link to each other: a sensor profile → its mesh route and history; the topology page → the sensor profile.

---

Smart-X PoE · Muhammed Suliman · Emeris University (The Independent Institute of Education) · 2026
