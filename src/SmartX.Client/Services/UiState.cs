using SmartX.Shared.Mesh;

namespace SmartX.Client.Services;

/// <summary>
/// Part 3 — UI state that survives moving between the three pillars.
/// <para>
/// Blazor disposes a page when the operator navigates away, which used to reset filters, the paused flag and
/// the selected sensor every time someone switched modules. This singleton (one per browser tab in WebAssembly)
/// stores those choices. Pages restore them on load and save them on dispose. The topology page and the
/// Part 1/2 pages also use <see cref="SelectedSensorMac"/> to hand the same sensor between modules.
/// </para>
/// </summary>
public sealed class UiState
{
    /// <summary>The sensor the operator last focused on in any module (sensor profile, command stream, topology).</summary>
    public string? SelectedSensorMac { get; set; }

    public CommandStreamUi CommandStream { get; } = new();
    public TopologyUi Topology { get; } = new();

    /// <summary>Raised when a page changes shared state, so the layout can refresh its badges.</summary>
    public event Action? Changed;
    public void NotifyChanged() => Changed?.Invoke();

    public sealed class CommandStreamUi
    {
        public string Filter { get; set; } = "All";
        public string Search { get; set; } = "";
        public bool Paused { get; set; }
        public string TimelineMac { get; set; } = "";
        public string SimulatorMac { get; set; } = "";
    }

    public sealed class TopologyUi
    {
        public string Subnet { get; set; } = "";
        public SpanningTreeAlgorithm Algorithm { get; set; } = SpanningTreeAlgorithm.Prim;
        public bool ShowAllLinks { get; set; } = true;
        public bool Paused { get; set; }
        public string ReachabilityScope { get; set; } = "";
        public DateTime? HistoryFrom { get; set; }
        public DateTime? HistoryTo { get; set; }
        public bool HistoryNewestFirst { get; set; } = true;
        public int HistoryPageSize { get; set; } = 25;
    }
}
