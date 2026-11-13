# Smart-X: updates made from lecturer feedback (Parts 1 & 2 → final PoE)

This ledger records every change made in response to the lecturer's marked rubrics, plus the other defects found and fixed since each submission. Each entry names the code that changed, how to see the change, and the test that guards it.

| Submission | Mark | Deductions |
| --- | --- | --- |
| Part 1: Formative 1 (PROG7312 Part 1 – 2026) | **100 / 100** | none |
| Part 2: Formative 2 (PROG7312 Part 2 – 2026) | **97 / 100** | Predictive Action and Suggestion Engine 28/30 (written comment below); Main Menu Navigation and Active Pillar State 29/30 (no written comment) |

---

## A. Changes made in direct response to lecturer feedback

### A1. Predictive engine: "Counts never decay …" (Part 2, 28/30)

> *Criterion feedback:* "Counts never decay and are never pruned. Therefore, old behaviour counts as much as recent behaviour. There is no operator feedback."

| | Before (Part 2) | After (final PoE) |
| --- | --- | --- |
| Storage | `Dictionary<string,int>` occurrence and pair counts, incremented forever | `Dictionary<string, DecayingCounter>`: each value is stored with its last-update time and read as `value · 0.5^(elapsed / half-life)` |
| Effect | A pattern from last month weighed the same as one from this morning | Recent behaviour dominates; the half-life is configurable (`Predictive:HalfLifeHours`, default 6 h) |
| Rule threshold | raw count ≥ 2 | recency-weighted support ≥ 1.5 (two recent occurrences, or more older ones) |
| Rationale text | "in 2 of 2 previous occurrences" | "weighted support 1.9 of 1.9; recent behaviour counts most, half-life 6 h" plus "seen N× · recency-weighted W" on the card |

* **Code:** `src/SmartX.Api/Services/DecayingCounter.cs` (new), `PredictiveEngine.cs`, `PredictiveEngine.Suggestions.cs`, `Program.cs`, `appsettings.json`.
* **Test:** `PredictiveEngineFeedbackTests.Old_behaviour_decays_and_is_pruned`. Two-day-old behaviour (8 half-lives) no longer produces a suggestion.

### A2. Predictive engine: "… and are never pruned" (Part 2, 28/30)

* A prune pass runs at most once a minute (and on demand through `Prune()`). It:
  * removes condition→action pairs whose decayed weight is below 0.05,
  * removes conditions with no remaining pairs that are no longer active,
  * keeps at most 25 actions per condition and 1 000 conditions, evicting the weakest first,
  * drops expired snoozes and faded feedback.
* Memory and suggestion time therefore stay bounded however long the gateway runs. `GET /api/suggestions/stats` now reports `prunedPairs` and `prunedConditions`.
* **Code:** `PredictiveEngine.cs` → `Prune`, `PruneIfDue`; `PredictiveEngine.Feedback.cs` → `PruneFeedbackUnsafe`.
* **Test:** `Old_behaviour_decays_and_is_pruned` asserts `Prune()` removes the faded pattern.

### A3. Predictive engine: "There is no operator feedback" (Part 2, 28/30)

* Every suggestion now carries a stable `RuleKey`. The dashboard shows two buttons on each card:
  * **Run / Show** (accept): reinforces the rule (+1 weight) and marks it handled for the current occurrence. It also still performs the action.
  * **✕ Dismiss**: halves the rule's weight, hides the suggestion for 30 minutes, and confirms with a toast.
* Both verdicts feed a smoothed acceptance factor `(accepted + 2) / (accepted + dismissed + 2)` built from decayed counts. A rule the operators keep dismissing loses confidence, and recovers as the dismissals age.
* New endpoint `POST /api/suggestions/feedback { ruleKey, verdict }`, validated with the shared DataAnnotations (`400` for an empty key).
* **Code:** `PredictiveEngine.Feedback.cs` (new), `SuggestionModels.cs` (`RuleKey`, `EffectiveSupport`, `SuggestionVerdict`, request/result DTOs), `SuggestionEndpoints.cs`, `GatewayApiClient.SendSuggestionFeedbackAsync`, `CommandStream.razor` (buttons), `part3.css`.
* **Test:** `PredictiveEngineFeedbackTests.Dismiss_snoozes_and_lowers_confidence_accept_reinforces`.
* **How to see it:** on `/command-stream`, click **⚡ Power spike**. The active-fault insight appears with **View** and **✕ Dismiss**. Dismiss it and the toast reads *"Dismissed – hidden for 30 minutes and weighted down."*

### A4. Main menu navigation and pillar state (Part 2, 29/30, no written comment)

No comment was given for the lost mark. The rubric's top band asks for routing that *"seamlessly preserves application states while shifting between telemetry modules"*. That was the one thing the Part 2 menu did not do: Blazor disposed each page on navigation, so filters, search text, the paused flag and the selected sensor reset every time the operator switched pillars. Changes:

| Change | Code |
| --- | --- |
| A `UiState` singleton (one per browser tab) remembers the Command Stream filter, search, pause, timeline sensor and simulator sensor, and the topology subnet, MST algorithm, link toggle, BFS scope and history range. Pages restore it on load and save it on dispose. | `Client/Services/UiState.cs`, `CommandStream.razor`, `Topology.razor`, `HistoryLog.razor` |
| A page left **paused** still loads its data once when you come back (previously it stayed on "Loading…"). | `CommandStream.razor` → `PollAsync` |
| The **selected sensor follows the operator** across pillars. Opening a sensor profile sets it; the topology page opens on it; the sidebar keeps a shortcut to it. | `SensorDetail.razor`, `MainLayout.razor`, `Topology.razor` |
| **Cross-pillar links:** sensor profile → *🕸 Mesh route & history*; topology route panel → *Sensor profile →*. | `SensorDetail.razor`, `RoutePanel.razor` |
| **Live sidebar badges** (pending critical alerts on Command Stream, mesh nodes down on Mesh Topology) refresh with the health poll. | `MainLayout.razor`, `/api/health` (`meshDown`, `meshOnline`, `historyReadings`) |
| The third pillar is enabled on the home page and in the menu, and the home page shows live counts for all three. | `Home.razor`, `MainLayout.razor` |

**How to see it:** on `/command-stream`, choose **In alert** and **⏸ Pause**, go to **Mesh Topology**, then come back: the filter and pause are still set and the data loads.

---

## B. Defects found and fixed after submission (continuous improvement)

These were found by compiling and running the code on the .NET 10 SDK and by end-to-end testing. They were not raised by the lecturer.

| # | Area | Defect | Fix | Guard |
| --- | --- | --- | --- | --- |
| B1 | Part 2 simulator script | On machines whose locale uses a decimal comma, `awk` printed `48,2` and the burst JSON was invalid (HTTP 400). | `export LC_ALL=C` in `scripts/simulate-devices.sh` | Script run in the .NET 10 check |
| B2 | Part 2 seed data | Every valve started OFF, so *Shut down all valves* + Undo changed nothing visible. | Valves, fans and grow lights seed as ON (a running facility). | Undo restores `OFF → ON` (checked in the run log) |
| B3 | Part 2 undo record | Undo labelled every restore as the inverse command even when the restored state was different. | The undo record uses the command that produces the restored state (`CommandFor`). | History page |
| B4 | Part 1/3 history | Deleting a sensor left its history behind. | `DELETE /api/sensors/{id}` also removes the sensor's AVL tree. | `SensorEndpoints.cs` |
| B5 | Part 3 history timings | The first range query of a session reported JIT time (≈ 900 µs). | JIT warm-up after seeding; `AggressiveOptimization` on the hot tree methods; best-of-three timing. | Query cost line now shows single-digit to tens of µs |
| B6 | Part 3 SVG | Razor treats `<text>` as its own tag when it starts a code block, so SVG labels vanished. Attribute values like `M@F(x)` were parsed as e-mail addresses. | SVG `<text>` wrapped in `<g>`; path data built in C#; numbers formatted with `CultureInfo.InvariantCulture`. | Visual check |
| B7 | Part 3 map | Sensor dots (≈ 4 px) were hard to click. | Invisible 2.4 m hit circle around each sensor. | Visual check |
| B8 | Part 3 history | The *Order* drop-down bound a `bool` to `"true"/"false"` options and rendered blank. | Bound to a `desc`/`asc` string. | Visual check |
| B9 | Part 3 routing | Long MST chains looked wrong next to Dijkstra's shorter path. | Both are shown with an explanation. The dashboard reports per-round energy with and without aggregation: MST 49.9 mJ vs SPT 51.0 mJ aggregated, and the reverse without aggregation. | `Mst_is_cheapest_per_round_with_aggregation_and_dijkstra_without` |
| B10 | Part 3 radio model doc | The doc comment claimed two short hops always beat one long hop. That is false within radio range for these constants. | Comment corrected; test asserts the actual behaviour. | `Radio_model_costs_grow_with_distance_and_range_is_enforced` |
| B11 | API text on non-English locales | On a computer with a decimal-comma locale, route explanations printed "49,9 mJ". | The API runs with the invariant culture, so server-generated text always uses '.' decimals. | `Program.cs` |
| B12 | Part 3 route tracker | A route of 28 hops after a router failure made the panel very tall. | Long routes show the first 5 and last 3 hops, with a "… N more hops …" button to expand. | Visual check |
| B13 | Part 3 route tracker | The "route before healing" was taken from the single most recent healing event, so an unrelated heartbeat event a few seconds later erased a router failure's re-route evidence. | The previous route is remembered per node (own uplink or any relay above it) and shown with the time it changed. | `MeshNetworkService.RememberChangedRoutes`; test `Failing_the_busiest_relay_heals_the_mesh_and_restore_undoes_it` |
| B14 | Part 3 history log | The oldest row on each page had no Δ, because its previous reading sat on the neighbouring page. | The query fetches one neighbouring reading just off the page for the delta. | test `Newest_first_pages_have_a_delta_on_every_row_including_the_page_edge` |
| B15 | Feedback endpoint | Any string was accepted as a rule key, so a client could grow the feedback table without bound. | Only keys the engine issues (`rule\|…`, `insight\|…`, `fault\|…`) are accepted (400 otherwise); the table is capped at 2 000 entries, weakest evicted. | test `Dismiss_snoozes_and_lowers_confidence_accept_reinforces` |
| B16 | Topology page | Following the sidebar's last-sensor link while already on `/topology/{mac}` changed the URL but not the selection (Blazor reuses the page). | The page follows its route parameter in `OnParametersSetAsync`. | Browser check |
| B17 | Mesh builder | A zone typed in a different case through the API (`zone 1`) created a second, empty router. | Zone names are matched case-insensitively. | Code review |
| B18 | History log | "Last 24 h" stayed anchored to the moment the page opened when another sensor was selected later. | Relative ranges are recomputed whenever the selected sensor changes. | Browser check |

---

## C. Summary of structural improvements across the PoE

* **Part 1 → 2:** the ingestion pipeline gained a FIFO / priority split, undoable commands and sorted timelines without changing Part 1 contracts.
* **Part 2 → 3:**
  * Part 1's live packets now also feed per-sensor AVL trees, alongside the jagged-array batches.
  * Part 1's heartbeat drives the mesh's self-healing.
  * Part 1's `TelemetryPacket<T>` `+` aggregation is why the MST is the right routing objective.
  * Part 2's predictive engine now decays, prunes and learns from feedback.
* **Testing:** the project gained an xUnit suite. Every custom structure is checked against a reference implementation (`SortedDictionary`, LINQ, brute-force MST, a linear scan).
