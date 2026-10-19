#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# Smart-X Part 2 – device simulator / smoke test.
# Hits the API exactly like a fleet of ESP32 nodes would, then prints the
# evidence that each data structure did its job.
#
#   ./scripts/simulate-devices.sh                    # API on http://localhost:5200
#   ./scripts/simulate-devices.sh http://localhost:8080   # API in Docker
# -----------------------------------------------------------------------------
set -euo pipefail
export LC_ALL=C   # force "48.2" not "48,2" on locales with a decimal comma, so the JSON stays valid
API="${1:-http://localhost:5200}"

json_post() { curl -s -X POST "$API$1" -H 'content-type: application/json' -d "$2"; echo; }

first_mac() {   # first registered sensor MAC of a category (Environmental | PowerConsumption)
  curl -s "$API/api/sensors?category=$1&pageSize=1" | grep -o '"macAddress":"[^"]*"' | head -1 | cut -d'"' -f4
}

echo "== Health"; curl -s "$API/api/health"; echo
SOIL=$(first_mac Environmental)
METER=$(first_mac PowerConsumption)
echo "Soil probe: $SOIL   Power meter: $METER"
[ -n "$SOIL" ] && [ -n "$METER" ] || { echo "Could not find seeded sensors – is the API running?"; exit 1; }

echo; echo "== 1. Burst of 300 normal readings → FIFO Queue<T> (drained at 32/s, so a backlog builds)"
batch="["
for _ in $(seq 1 300); do
  value=$(awk -v r="$RANDOM" 'BEGIN { printf "%.1f", 47.5 + (r % 35) / 10 }')
  batch+="{\"macAddress\":\"$SOIL\",\"kind\":\"Float\",\"value\":$value},"
done
batch="${batch%,}]"
json_post /api/stream/telemetry/batch "$batch"

echo; echo "== 2. Critical power spike → PriorityQueue (jumps the backlog)"
json_post /api/stream/telemetry "{\"macAddress\":\"$METER\",\"kind\":\"Int\",\"value\":2600}"

echo; echo "== 3. Same spike again → suppressed by the active-fault HashSet"
json_post /api/stream/telemetry "{\"macAddress\":\"$METER\",\"kind\":\"Int\",\"value\":2650}"

sleep 1
echo; echo "== 4. Processed alerts – note queuedAheadAtProcess (packets overtaken) and latencyMs"
curl -s "$API/api/alerts/recent?take=3"; echo

echo; echo "== 5. Queue depths right now"
curl -s "$API/api/stream/queue"; echo

echo; echo "== 6. Active faults (HashSet)"
curl -s "$API/api/devices/faults"; echo

echo; echo "== 7. Normal reading clears the threshold fault"
json_post /api/stream/telemetry "{\"macAddress\":\"$METER\",\"kind\":\"Int\",\"value\":1230}"
echo "(wait for the FIFO backlog to drain, then GET $API/api/devices/faults again)"

echo; echo "== 8. Bulk override + undo (Stack<T>)"
json_post /api/commands/bulk '{"kind":"IrrigationValve","command":"TurnOff","reason":"Shut down all valves"}'
sleep 1
curl -s -X POST "$API/api/commands/undo"; echo

echo; echo "== 9. Timeline for the soil probe (SortedDictionary, oldest → newest)"
curl -s "$API/api/stream/timeline?mac=$SOIL&take=5"; echo
