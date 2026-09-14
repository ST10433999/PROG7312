using SmartX.Shared.Models;

namespace SmartX.Shared.Topology;

/// <summary>
/// One tier of the deployment hierarchy: Facility → Zone → Sub-Zone → Rack/Room → Sensor.
/// Each tier can carry its own configuration constraints, which are inherited (and may be tightened)
/// by every child. The tree is deliberately unbounded in depth so the recursive validator has to
/// cope with arbitrary nesting.
/// </summary>
public sealed class DeploymentNode
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Zone";           // Facility | Zone | SubZone | Rack | Sensor
    public TierConfig Config { get; set; } = new();
    public List<DeploymentNode> Children { get; set; } = new();
    public List<string> SensorMacs { get; set; } = new();

    public DeploymentNode() { }
    public DeploymentNode(string name, string kind, TierConfig? config = null, params DeploymentNode[] children)
    {
        Name = name; Kind = kind; Config = config ?? new TierConfig(); Children = children.ToList();
    }
}

public sealed class TierConfig
{
    /// <summary>Categories permitted below this tier. Empty = inherit from parent.</summary>
    public List<SensorCategory> AllowedCategories { get; set; } = new();
    /// <summary>Total power budget (W) for all PowerConsumption sensors under this tier. 0 = inherit.</summary>
    public int PowerBudgetWatts { get; set; }
    /// <summary>Maximum number of sensors under this tier. 0 = inherit.</summary>
    public int MaxSensors { get; set; }
    /// <summary>Minimum publish interval (s) allowed under this tier. 0 = inherit.</summary>
    public int MinPublishIntervalSeconds { get; set; }
}

public sealed class ValidationIssue
{
    public string Path { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Message { get; set; } = "";
    public Severity Severity { get; set; } = Severity.Warn;
}

public sealed class TopologyValidationResult
{
    public bool IsValid => Issues.All(i => i.Severity < Severity.High);
    public int NodesVisited { get; set; }
    public int MaxDepth { get; set; }
    public int SensorsFound { get; set; }
    public List<ValidationIssue> Issues { get; set; } = new();
    public List<string> Trace { get; set; } = new();
}

public sealed class NodePlacementRequest
{
    public string MacAddress { get; set; } = "";
    /// <summary>Path from root, e.g. ["Facility A", "Zone 1", "Sub-Zone B"].</summary>
    public List<string> Path { get; set; } = new();
    public SensorCategory Category { get; set; }
    public int PublishIntervalSeconds { get; set; } = 5;
    public int RatedWatts { get; set; }
}
