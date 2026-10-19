namespace SmartX.Api.Endpoints;

// -------------------------------------------------------------------------------------------------
// Part 2 — HTTP surface for the Real-Time Command Stream. Program.cs calls this once; each group
// lives in its own file:
//   /api/stream       StreamEndpoints      telemetry from devices (FIFO / PriorityQueue routing) + timeline
//   /api/commands     CommandEndpoints     manual overrides, bulk overrides, undo, history
//   /api/alerts       AlertEndpoints       critical-alert PriorityQueue
//   /api/devices      DeviceEndpoints      device registry (Dictionary) and active faults (HashSet)
//   /api/suggestions  SuggestionEndpoints  predictive engine
// -------------------------------------------------------------------------------------------------

public static class CommandStreamEndpoints
{
    public static RouteGroupBuilder MapCommandStreamEndpoints(this RouteGroupBuilder api)
    {
        api.MapStreamEndpoints();
        api.MapCommandEndpoints();
        api.MapAlertEndpoints();
        api.MapDeviceEndpoints();
        api.MapSuggestionEndpoints();
        return api;
    }
}
