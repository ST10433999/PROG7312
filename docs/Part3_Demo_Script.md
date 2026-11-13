# Smart-X final PoE: video demo script (≈ 10 minutes)

Record the screen at 1080p with the browser at full width. Speak over each step; the quoted lines are suggestions, not a script to read word for word. Keep a terminal visible at the start for the build.

## 0. Build, test and boot (≈ 1 min)

```bash
dotnet --list-sdks                      # shows 10.0.x
dotnet restore SmartX.slnx
dotnet build SmartX.slnx --no-restore   # 0 Warning(s), 0 Error(s)
dotnet test SmartX.slnx --no-build      # Passed! – Failed: 0
dotnet run --project src/SmartX.Api     # terminal A
dotnet run --project src/SmartX.Client  # terminal B
```

> "The solution has the API, the Blazor client, a shared library and an xUnit project. All tests pass. On start-up the API indexes about 424 000 historical readings into 240 AVL trees and builds the mesh graph: 245 vertices and about 590 links."

## 1. Main menu: all three pillars (≈ 45 s)

Open <http://localhost:5100>. Point at the three active pillars and the live counts (mesh nodes online, indexed history).

> "All three pillars run in one interface. The sidebar badges show pending critical alerts and mesh nodes that are down."

Quickly show state preservation: on **Command Stream** choose **In alert** and **Pause**, go to **Mesh Topology**, come back. The filter and pause are still set.

> "This was my Part 2 feedback point: the pillar state now survives navigation."

## 2. The mesh graph (≈ 1 min)

Open **Mesh Topology**.

> "Each zone is a subnet with a border router (the purple squares) wired to the central gateway. Every ESP32 links by radio to its nearest neighbours, so the graph is an adjacency list, which suits a sparse mesh. Edge weights are the energy to send one frame: the first-order radio model, with a d-squared amplifier term."

Hover a link to show distance, RSSI and µJ. Toggle **show every radio link** to show only the teal MST.

## 3. MST: Prim vs Kruskal (≈ 1 min)

Switch **MST algorithm** to Kruskal and back. Open **Under the hood → Show how Prim built the tree**.

> "Prim grows the tree from the gateway using my binary min-heap. It takes the backbone links first, then always the cheapest radio link that reaches a new node. Kruskal sorts all links and uses union–find to reject cycles. Both reach the same total energy, which they must."

Point at **Per round**: MST 49.9 mJ vs the shortest-path tree 51.0 mJ with aggregation.

## 4. Track a sensor by MAC (≈ 1 min)

Type a MAC (e.g. `A1:2F:00:00:28:48`) in **Route tracker** → **Track**.

> "The yellow line is the route this sensor's telemetry takes along the MST, hop by hop, with the energy per link. Underneath is Dijkstra's cheapest single path. The MST route is longer, but with in-network aggregation the whole fleet spends the least energy per round. That is the latency-versus-energy trade-off."

## 5. Self-healing (≈ 1.5 min)

Click **⚡ Fail busiest relay**.

> "That took out a zone router, the node carrying the most traffic. The tree was rebuilt in about a millisecond. The healing log lists every sensor whose uplink changed. My tracked sensor shows 're-routed', with its old path in pink."

Open the healing event to show old → new uplinks. Mention that the Part 1 heartbeat also takes silent sensors out automatically (point at a grey node or a "went silent" event).

## 6. BFS: who is connected, and where to hop (≈ 1 min)

In **Subnet reachability**, select the failed router's zone → **Run BFS**.

> "Breadth-first search from that subnet's router, confined to the subnet. The router is down, so nothing is reached, and every powered sensor is listed with the neighbour it can hop through, often in the next zone."

Switch to **Gateway – whole mesh**: all powered nodes are reached, listed by hop level. Click **♻ Restore all**.

## 7. Historical log on the AVL tree (≈ 1.5 min)

With a soil probe selected, click **3 days ago 14:00–16:00**.

> "Exactly the brief's query: moisture between 14:00 and 16:00 for one node. 25 readings, min, average and max. The query visited about 15 of roughly 1 770 nodes in the tree, height 12, within the AVL bound of 15."

Click **7 days** to show the hourly band chart.

> "Each of the 168 hourly buckets is one O(log n) aggregate, because every node stores its subtree's size, sum, min and max."

Page with **Next →** and switch the order.

## 8. Scaling evidence (≈ 45 s)

**Under the hood → AVL benchmark → 1 000 000 readings → Run**.

> "One million readings arriving in time order. A plain BST would be a million levels deep. The AVL tree is 20. A 2-hour range query is about 70 to 80 times faster than scanning an unsorted list, and the results are identical."

## 9. Predictive engine feedback fix (≈ 45 s)

Back on **Command Stream**: click **⚡ Power spike**, then **✕ Dismiss** on the insight.

> "Part 2 feedback said counts never decayed, were never pruned, and there was no operator feedback. Counts now decay with a six-hour half-life, weak patterns are pruned, and Dismiss halves a rule's weight and snoozes it, while Run reinforces it."

## 10. Close (≈ 15 s)

Show the README sections 12–14 and `docs/FEEDBACK_UPDATES.md` on GitHub, and the commit history.
