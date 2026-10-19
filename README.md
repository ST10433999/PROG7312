# PROG7312 · Smart-X Data Ingestion & Validation Gateway (Parts 1 & 2)

> Portfolio of Evidence — *The Smart-X IoT Mesh Ecosystem*
> Backend: **ASP.NET Core Minimal API on .NET 10** · Frontend: **Blazor WebAssembly** · Shared domain library in C#

Smart-X is a hybrid IoT ecosystem in which thousands of ESP32 nodes publish multi-typed telemetry — `float` soil moisture, `int` wattage, `bool` valve states — to a central gateway. Part 1 delivers the **ingestion and validation gateway**: sensor registration, typed telemetry ingestion, encrypted media/log attachments, and a live **Node Pulse Board** with anomaly triage (the engagement strategy chosen in the Task 1 research report, included as [`docs/PROG7312_POE_Part1_Research_Report.pdf`](docs/PROG7312_POE_Part1_Research_Report.pdf)).

---

## Contents

1. [Architecture](#1-architecture)
2. [Prerequisites](#2-prerequisites)
3. [Quick start (local)](#3-quick-start-local)
4. [Quick start (Docker)](#4-quick-start-docker)
5. [Using the gateway](#5-using-the-gateway)
6. [API reference](#6-api-reference)
7. [How the brief's technical requirements are met](#7-how-the-briefs-technical-requirements-are-met)
8. [Configuration](#8-configuration)
9. [Project structure](#9-project-structure)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Architecture

```
┌──────────────────────────────┐   HTTP/JSON (async)   ┌──────────────────────────────────┐
│  SmartX.Client               │ ────────────────────▶ │  SmartX.Api  (.NET 10 Minimal API)│
│  Blazor WebAssembly          │ ◀──────────────────── │                                  │
│  · Landing menu (3 pillars)  │   multipart uploads   │  · GatewayState (in-memory)       │
│  · Register sensor (validated│                       │  · NodeChannel<T> ring buffers    │
│    with shared annotations)  │                       │  · HistoricalBatchStore<T>[][]    │
│  · Node Pulse Board + triage │                       │  · TopologyValidator (recursive)  │
│  · Attachments (InputFile)   │                       │  · AttachmentService (AES-256)    │
│  · Data-structures evidence  │                       │  · TelemetrySimulator (ESP32 fleet)│
└──────────────┬───────────────┘                       └──────────────┬───────────────────┘
               │            ┌─────────────────────────┐               │
               └───────────▶│  SmartX.Shared          │◀──────────────┘
                            │  TelemetryPacket<T>,    │
                            │  TelemetryOps<T>, models│
                            │  DTOs, DeploymentNode   │
                            └─────────────────────────┘
```

* **`SmartX.Shared`** — class library referenced by both ends. Holds `TelemetryPacket<T>`, operator overloading, the custom `TelemetryRingBuffer<T>`, the jagged-array `HistoricalBatchStore<T>`, the deployment tree and the recursive `TopologyValidator`, plus every DTO and its DataAnnotations (so the **same validation rules run in the browser and on the server**).
* **`SmartX.Api`** — Minimal API. Seeds 240 mock sensors and 24 hours of historical batches at start-up, then a `BackgroundService` plays the ESP32 fleet, publishing packets every few seconds with occasional spikes and dropouts.
* **`SmartX.Client`** — Blazor WASM SPA. All API calls are `async`/`await` through a typed `GatewayApiClient`; the Pulse Board polls the API every 2 s with `PeriodicTimer` and cancels cleanly on navigation.

---

## 2. Prerequisites

| Tool | Version | Check |
|------|---------|-------|
| .NET SDK | **10.0** or later | `dotnet --version` → `10.0.xxx` |
| A modern browser | Chrome / Edge / Firefox / Safari | WebAssembly support |
| *(optional)* Docker Desktop / Engine | 24+ with Compose v2 | `docker compose version` |
| *(optional)* Git | any | `git --version` |

Install the .NET 10 SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>.

---

## 3. Quick start (local)

```bash
# 1. Clone
git clone https://github.com/ST10433999/PROG7312.git SmartX
cd SmartX

# 2. Restore dependencies (NuGet) for all three projects
dotnet restore

# 3. Compile the whole solution
dotnet build --no-restore
```

Then open **two terminals**:

**Terminal A — boot the .NET API**

```bash
dotnet run --project src/SmartX.Api
# → Now listening on: http://localhost:5200
# → Seeded 240 sensors, 3xxx/2xxx/8xx historical packets in 24 jagged batches.
# → Telemetry simulator started (tick 500 ms).
```

Sanity check: <http://localhost:5200/api/health> should return JSON with `"status": "ok"`.
The OpenAPI document is at <http://localhost:5200/openapi/v1.json>.

**Terminal B — run the Blazor client**

```bash
dotnet run --project src/SmartX.Client
# → Now listening on: http://localhost:5100
```

Open **<http://localhost:5100>**. The sidebar shows a green dot with *API online · net10.0.x* once the client has reached the API.

> **Ports.** The API listens on **5200** and the client on **5100** (see each project's `Properties/launchSettings.json`). The client reads the API address from `src/SmartX.Client/wwwroot/appsettings.json` (`ApiBaseUrl`). If you change the API port, update that file and add the client origin to `Cors:AllowedOrigins` in `src/SmartX.Api/appsettings.json`.

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

The client container rewrites `appsettings.json` at start-up from the `API_BASE_URL` environment variable (default `http://localhost:8080/`), so the browser talks to the API published on the host. Stop with `docker compose down` (add `-v` to drop the attachments volume).

To run only the API in Docker and the client from `dotnet run`, set `Cors__AllowedOrigins__1=http://localhost:5100` (already included in the API image).

---

## 5. Using the gateway

| Page | Route | What to do |
|------|-------|-----------|
| **Gateway home** | `/` | Landing menu with the three architectural pillars. *Sensor Data Ingestion* is active; *Command Stream* (Part 2) and *Mesh Topology* (final PoE) are visibly disabled. |
| **Sensor Ingestion** | `/ingestion` | The **Node Pulse Board**. Every registered node is a tile with a 60-sample sparkline, colour-coded *Healthy / Drift / Spike / Silent*, sorted worst-first. Filter by state or search by MAC/name/zone. Click a tile to open the drawer: simulate a reading (try `90` on a soil probe or `2600` on a meter to trigger a spike), or report an issue into the **triage feed**. Acknowledge items with **Ack**; *Sensor disconnect* items auto-resolve when the node returns. |
| **Register sensor** | `/ingestion/register` | Form with MAC (`AA:BB:CC:DD:EE:FF`), name, category (→ payload type), publish interval, facility → zone → sub-zone → node ID. Invalid input is flagged before submission; the API re-validates and runs the **recursive placement check** (e.g. power meters are not allowed in Zone 1). *Fill with sample* generates a valid record; *Check placement only* shows the recursion trace. |
| **Sensor registry** | `/ingestion/sensors` | Paged, searchable table of profiles. |
| **Sensor profile** | `/ingestion/sensors/{id}` | Profile, live window statistics (μ, σ, z), 24-hour history, and the **attachment uploader**: drop up to 10 config/photo/log files; they stream as `multipart/form-data`, are AES-256-encrypted on disk and can be downloaded (decrypted on the fly) or deleted. |
| **Data structures** | `/ingestion/data-structures` | Live evidence: operator overloading on two real meter packets, the ragged shape of the jagged batch arrays and the timed flatten into `List<T>`, the `double[24,3]` hourly grid, and a button that runs the recursive tree validator with its full trace. |

---

## 6. API reference

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
| **Startup interface** | `Client/Pages/Home.razor` | Three pillars, two disabled. |
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
├─ SmartX.slnx
├─ docker-compose.yml
├─ docker/                      nginx.conf, client-entrypoint.sh
├─ README.md
└─ src/
   ├─ SmartX.Shared/            TelemetryPacket<T>, TelemetryOps<T>, TelemetryRingBuffer<T>,
   │                            HistoricalBatchStore<T>, DeploymentNode, TopologyValidator, DTOs
   ├─ SmartX.Api/               Program.cs, Endpoints/, Services/, Dockerfile, appsettings.json
   └─ SmartX.Client/            Program.cs, Pages/, Components/, Layout/, Services/, wwwroot/, Dockerfile
```

---

## 10. Troubleshooting

| Symptom | Fix |
|---------|-----|
| Sidebar says **API unreachable** | Start the API first (`dotnet run --project src/SmartX.Api`) and confirm <http://localhost:5200/api/health>. |
| Browser console shows a **CORS** error | The client origin must be listed under `Cors:AllowedOrigins` in the API's `appsettings.json` (or `Cors__AllowedOrigins__N` env var). |
| `dotnet` says the SDK is missing | Install .NET **10** (`dotnet --list-sdks` must show `10.0.x`); `global.json` is not pinned, any 10.x works. |
| Port already in use | Change `applicationUrl` in the relevant `Properties/launchSettings.json`, then update `ApiBaseUrl` / CORS as above. |
| Upload rejected | Only `.json .yaml .yml .cfg .ini .txt .log .csv .png .jpg .jpeg .webp .pdf .bin`, ≤ 25 MB per file, ≤ 10 files per request. |
| Docker: client loads but no data | The browser must reach the API on the host: set `API_BASE_URL` on the client service to the API's public URL and add that client origin to the API's CORS list. |

---

Part 1 of the Smart-X PoE · Muhammed Suliman · Emeris University (The Independent Institute of Education) · 2026


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
| Predictive engine | `Dictionary<string, Dictionary<string,int>>` pair counts + `Queue<ConditionEvent>` sliding window | `PredictiveEngine` | **🧠 Suggested actions & insights** panel; suggested devices get a ★ and move to the top of the table. |

### 11.2 How the predictive engine works

The engine mines **sequential association rules** over a 10-minute sliding window:

1. *Conditions* are processed critical alerts – e.g. `Soil probe 000: Critical moisture drop: 12% (below 20%)`, a sensor disconnect, or an operator E-STOP.
2. *Actions* are what the operator does next – issuing a command on a device, or searching for a device on the dashboard (searches are reported to `POST /api/suggestions/search` after an 800 ms debounce and count only when they identify exactly one device).
3. Each action is credited once to every condition in the window. When a condition fires again, every action that has followed it at least **twice** is proposed with
   `confidence = times the action followed the condition ÷ earlier occurrences of the condition`.
4. Insights are added on top: devices or sensors with unresolved faults, and nodes that raised three or more alerts in the window.

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
