#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# Smart-X Part 3 – mesh routing & history smoke test.
# Exercises the graph (BFS, MST, self-healing) and the AVL history index exactly
# as the topology dashboard does, and prints the evidence.
#
#   ./scripts/mesh-demo.sh                        # API on http://localhost:5200
#   ./scripts/mesh-demo.sh http://localhost:8080  # API in Docker
# Needs curl and python3 (for pretty-printing).
# -----------------------------------------------------------------------------
set -euo pipefail
export LC_ALL=C
API="${1:-http://localhost:5200}/api"

pp() { python3 -c "import json,sys; d=json.load(sys.stdin); $1"; }

echo "== 1. Mesh graph and minimum spanning tree (Prim)"
curl -s "$API/mesh" | pp "s=d['stats']; print(f\"{s['vertices']} nodes, {s['links']} links, {s['online']} online, {s['components']} component(s)\"); print(f\"MST {s['treeLinks']} links, {s['treeEnergyMicroJoules']/1000:.1f} mJ (all links {s['allLinksEnergyMicroJoules']/1000:.1f} mJ), avg {s['averageHops']} hops, built in {s['computeMicroseconds']} us\")"

echo; echo "== 2. Kruskal must find the same total"
curl -s "$API/mesh/spanning-tree?algorithm=Kruskal&steps=3" | pp "print(f\"Kruskal {d['totalEnergyMicroJoules']} uJ vs Prim {d['otherAlgorithmEnergyMicroJoules']} uJ; first links: {[s['from']+' - '+s['to'] for s in d['firstSteps']]}\")"

SENSOR=$(curl -s "$API/sensors?category=Environmental&pageSize=1" | grep -o '"macAddress":"[^"]*"' | head -1 | cut -d'"' -f4)
echo; echo "== 3. Track sensor $SENSOR"
curl -s "$API/mesh/route/$SENSOR" | pp "print(' -> '.join(h['name'] for h in d['treePath'])); print(f\"MST {d['treePathEnergyMicroJoules']} uJ, Dijkstra {d['shortestPathEnergyMicroJoules']} uJ\")"

echo; echo "== 4. Fail the busiest relay → the mesh heals itself"
curl -s -X POST "$API/mesh/fail-busiest" | pp "h=d['healing']; print(f\"{d['name']} offline: {h['rerouted']} re-routed, {len(h['newlyIsolated'])} isolated\"); [print(f\"   {c['name']}: {c['oldNextHop']} -> {c['newNextHop']}\") for c in h['changes'][:5]]"

echo; echo "== 5. BFS confined to each subnet (cut-off devices get hop advice)"
for Z in "Zone%201" "Zone%202" "Zone%203" "Zone%204"; do
  curl -s "$API/mesh/reachability?subnet=$Z" | pp "print(f\"{d['rootName']:<24} reached {d['reached']}/{d['inScope']}, down {len(d['down'])}, cut off {len(d['cutOff'])}\" + (f\" e.g. {d['cutOff'][0]['advice']}\" if d['cutOff'] else ''))"
done

echo; echo "== 6. Restore everything"
curl -s -X POST "$API/mesh/restore-all" | pp "h=d['healing']; print('restored;', (h or {}).get('rerouted', 0), 'node(s) moved back to their best uplink')"

FROM=$(python3 -c "import datetime as t; d=(t.datetime.now(t.timezone.utc)-t.timedelta(days=3)).replace(hour=14,minute=0,second=0,microsecond=0); print(d.isoformat())")
TO=$(python3 -c "import datetime as t; d=(t.datetime.now(t.timezone.utc)-t.timedelta(days=3)).replace(hour=16,minute=0,second=0,microsecond=0); print(d.isoformat())")
echo; echo "== 7. AVL range query: $SENSOR between $FROM and $TO"
curl -s -G "$API/history/$SENSOR" --data-urlencode "from=$FROM" --data-urlencode "to=$TO" --data-urlencode "take=3" --data-urlencode "compare=true" \
  | pp "s=d['stats']; print(f\"{d['totalInRange']} readings, min {d['min']} avg {d['average']} max {d['max']}\"); print(f\"visited {s['nodesVisited']} of {s['seriesSize']} nodes (height {s['treeHeight']}, AVL bound {s['maxAvlHeight']}) in {s['treeMicroseconds']} us; linear scan {s['linearMicroseconds']} us\")"

echo; echo "== 8. Scaling: AVL vs linear scan over 1 000 000 readings"
curl -s "$API/history/benchmark?points=1000000&queries=200" \
  | pp "print(f\"AVL fetch {d['avlFetchMicroseconds']} us, count {d['avlCountMicroseconds']} us, min/avg/max {d['avlAggregateMicroseconds']} us vs linear {d['linearScanMicroseconds']} us → {d['speedup']}x; height {d['treeHeight']} (bound {d['maxAvlHeight']}); results match: {d['resultsMatch']}\")"
