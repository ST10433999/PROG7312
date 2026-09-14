using SmartX.Shared.Models;

namespace SmartX.Shared.Topology;

/// <summary>
/// Recursive validation of the multi-tier deployment tree.
/// <para>
/// Two recursive algorithms live here:
/// <list type="number">
///   <item><see cref="ValidateTree"/> walks the whole hierarchy depth-first, carrying the <b>effective</b>
///         configuration (parent constraints merged with the current tier) down the call stack and
///         returning aggregate counts (sensors, watts) back up it.</item>
///   <item><see cref="ValidatePlacement"/> descends a single path (Facility A → Zone 1 → Sub-Zone B) to
///         confirm a node may be configured there.</item>
/// </list>
/// Base cases: a node with no children (leaf) or an exhausted path. A depth guard protects against
/// cyclic or absurdly deep trees so the algorithm can never overflow the stack.
/// </para>
/// </summary>
public static class TopologyValidator
{
    public const int MaxSupportedDepth = 64;

    // ------------------------------------------------------------------
    // 1. Whole-tree validation
    // ------------------------------------------------------------------
    public static TopologyValidationResult ValidateTree(DeploymentNode root, IReadOnlyDictionary<string, SensorProfile>? sensors = null)
    {
        var result = new TopologyValidationResult();
        var inherited = new EffectiveConfig();
        Walk(root, root.Name, 0, inherited, result, sensors ?? new Dictionary<string, SensorProfile>());
        return result;
    }

    private sealed record EffectiveConfig(
        HashSet<SensorCategory>? Allowed = null,
        int PowerBudget = 0,
        int MaxSensors = 0,
        int MinInterval = 0);

    private sealed record Rollup(int Sensors, int Watts);

    private static Rollup Walk(
        DeploymentNode node, string path, int depth, EffectiveConfig inherited,
        TopologyValidationResult result, IReadOnlyDictionary<string, SensorProfile> sensors)
    {
        result.NodesVisited++;
        result.MaxDepth = Math.Max(result.MaxDepth, depth);
        result.Trace.Add($"{new string(' ', depth * 2)}↳ {node.Kind} '{node.Name}'");

        // Guard base case – refuse to recurse past a sane depth.
        if (depth > MaxSupportedDepth)
        {
            result.Issues.Add(new ValidationIssue { Path = path, Rule = "MaxDepth", Severity = Severity.Critical,
                Message = $"Hierarchy deeper than {MaxSupportedDepth} tiers – possible cycle." });
            return new Rollup(0, 0);
        }

        // Merge this tier's config with what was inherited. A child may only tighten constraints.
        var effective = Merge(inherited, node.Config, path, result);

        // Count sensors attached directly to this tier.
        var sensorsHere = 0;
        var wattsHere = 0;
        foreach (var mac in node.SensorMacs)
        {
            sensorsHere++;
            result.SensorsFound++;
            if (!sensors.TryGetValue(mac, out var profile))
            {
                result.Issues.Add(new ValidationIssue { Path = $"{path} → {mac}", Rule = "UnknownSensor", Severity = Severity.Warn,
                    Message = "Sensor referenced by topology but not registered." });
                continue;
            }
            if (effective.Allowed is { Count: > 0 } && !effective.Allowed.Contains(profile.Category))
                result.Issues.Add(new ValidationIssue { Path = $"{path} → {mac}", Rule = "CategoryNotAllowed", Severity = Severity.High,
                    Message = $"{profile.Category} sensors are not permitted under '{node.Name}'." });
            if (effective.MinInterval > 0 && profile.PublishIntervalSeconds < effective.MinInterval)
                result.Issues.Add(new ValidationIssue { Path = $"{path} → {mac}", Rule = "IntervalTooFast", Severity = Severity.Warn,
                    Message = $"Publishes every {profile.PublishIntervalSeconds}s; tier minimum is {effective.MinInterval}s." });
            if (profile.Category == SensorCategory.PowerConsumption) wattsHere += 150; // nominal rated load per meter
        }

        // Base case: leaf – nothing further to descend into.
        if (node.Children.Count == 0)
            return new Rollup(sensorsHere, wattsHere);

        // Recursive case: descend into every child and roll totals back up.
        var total = new Rollup(sensorsHere, wattsHere);
        foreach (var child in node.Children)
        {
            var childRollup = Walk(child, $"{path} → {child.Name}", depth + 1, effective, result, sensors);
            total = new Rollup(total.Sensors + childRollup.Sensors, total.Watts + childRollup.Watts);
        }

        if (effective.MaxSensors > 0 && total.Sensors > effective.MaxSensors)
            result.Issues.Add(new ValidationIssue { Path = path, Rule = "SensorQuota", Severity = Severity.High,
                Message = $"{total.Sensors} sensors under '{node.Name}' exceeds quota of {effective.MaxSensors}." });
        if (effective.PowerBudget > 0 && total.Watts > effective.PowerBudget)
            result.Issues.Add(new ValidationIssue { Path = path, Rule = "PowerBudget", Severity = Severity.High,
                Message = $"Estimated {total.Watts} W under '{node.Name}' exceeds budget of {effective.PowerBudget} W." });

        return total;
    }

    private static EffectiveConfig Merge(EffectiveConfig parent, TierConfig own, string path, TopologyValidationResult result)
    {
        HashSet<SensorCategory>? allowed = parent.Allowed;
        if (own.AllowedCategories.Count > 0)
        {
            var mine = own.AllowedCategories.ToHashSet();
            if (parent.Allowed is { Count: > 0 } && !mine.IsSubsetOf(parent.Allowed))
                result.Issues.Add(new ValidationIssue { Path = path, Rule = "ConfigWidensParent", Severity = Severity.High,
                    Message = "Tier allows categories its parent forbids." });
            allowed = mine;
        }

        var budget = own.PowerBudgetWatts > 0 ? own.PowerBudgetWatts : parent.PowerBudget;
        if (parent.PowerBudget > 0 && own.PowerBudgetWatts > parent.PowerBudget)
            result.Issues.Add(new ValidationIssue { Path = path, Rule = "BudgetExceedsParent", Severity = Severity.Warn,
                Message = $"Tier budget {own.PowerBudgetWatts} W exceeds parent budget {parent.PowerBudget} W – clamped." });
        if (parent.PowerBudget > 0) budget = Math.Min(budget, parent.PowerBudget);

        var maxSensors = own.MaxSensors > 0 ? own.MaxSensors : parent.MaxSensors;
        if (parent.MaxSensors > 0) maxSensors = Math.Min(maxSensors, parent.MaxSensors);

        var minInterval = Math.Max(own.MinPublishIntervalSeconds, parent.MinInterval);

        return new EffectiveConfig(allowed, budget, maxSensors, minInterval);
    }

    // ------------------------------------------------------------------
    // 2. Single-path placement validation
    // ------------------------------------------------------------------
    public static TopologyValidationResult ValidatePlacement(DeploymentNode root, NodePlacementRequest request)
    {
        var result = new TopologyValidationResult();
        if (request.Path.Count == 0 || !string.Equals(request.Path[0], root.Name, StringComparison.OrdinalIgnoreCase))
        {
            result.Issues.Add(new ValidationIssue { Path = string.Join(" → ", request.Path), Rule = "UnknownRoot", Severity = Severity.Critical,
                Message = $"Path must start at facility '{root.Name}'." });
            return result;
        }
        Descend(root, request, 0, new EffectiveConfig(), root.Name, result);
        return result;
    }

    private static void Descend(DeploymentNode node, NodePlacementRequest request, int index,
        EffectiveConfig inherited, string path, TopologyValidationResult result)
    {
        result.NodesVisited++;
        result.MaxDepth = index;
        result.Trace.Add($"{new string(' ', index * 2)}↳ checking {node.Kind} '{node.Name}'");
        var effective = Merge(inherited, node.Config, path, result);

        // Base case: we have reached the tier the node is to be placed in.
        if (index == request.Path.Count - 1)
        {
            if (effective.Allowed is { Count: > 0 } && !effective.Allowed.Contains(request.Category))
                result.Issues.Add(new ValidationIssue { Path = path, Rule = "CategoryNotAllowed", Severity = Severity.High,
                    Message = $"{request.Category} sensors are not permitted in '{node.Name}'." });
            if (effective.MinInterval > 0 && request.PublishIntervalSeconds < effective.MinInterval)
                result.Issues.Add(new ValidationIssue { Path = path, Rule = "IntervalTooFast", Severity = Severity.Warn,
                    Message = $"Interval {request.PublishIntervalSeconds}s is below tier minimum {effective.MinInterval}s." });
            if (effective.MaxSensors > 0 && node.SensorMacs.Count + 1 > effective.MaxSensors)
                result.Issues.Add(new ValidationIssue { Path = path, Rule = "SensorQuota", Severity = Severity.High,
                    Message = $"'{node.Name}' already holds {node.SensorMacs.Count}/{effective.MaxSensors} sensors." });
            if (effective.PowerBudget > 0 && request.RatedWatts > effective.PowerBudget)
                result.Issues.Add(new ValidationIssue { Path = path, Rule = "PowerBudget", Severity = Severity.High,
                    Message = $"Rated {request.RatedWatts} W exceeds tier budget {effective.PowerBudget} W." });
            result.Trace.Add($"{new string(' ', index * 2)}  ✓ base case reached – node may be placed here" );
            return;
        }

        // Recursive case: find the next tier on the path and descend.
        var nextName = request.Path[index + 1];
        var next = node.Children.FirstOrDefault(c => string.Equals(c.Name, nextName, StringComparison.OrdinalIgnoreCase));
        if (next is null)
        {
            // The remaining tiers do not exist yet (registration will create them). A new tier inherits the
            // constraints in force here, so validate the node against THIS tier's effective config rather than
            // letting an unknown sub-zone bypass the parent's rules.
            var remaining = string.Join(" → ", request.Path.Skip(index + 1));
            result.Trace.Add($"{new string(' ', index * 2)}  · '{remaining}' does not exist yet – will be created under '{node.Name}', inheriting its constraints");
            result.Issues.Add(new ValidationIssue { Path = $"{path} → {remaining}", Rule = "TierWillBeCreated", Severity = Severity.Info,
                Message = $"'{nextName}' is a new tier under '{node.Name}'." });
            var truncated = new NodePlacementRequest
            {
                MacAddress = request.MacAddress, Category = request.Category, RatedWatts = request.RatedWatts,
                PublishIntervalSeconds = request.PublishIntervalSeconds,
                Path = request.Path.Take(index + 1).Append(nextName).ToList()
            };
            Descend(new DeploymentNode(nextName, "Tier"), truncated, index + 1, effective, $"{path} → {nextName}", result);
            return;
        }
        Descend(next, request, index + 1, effective, $"{path} → {next.Name}", result);
    }

    // ------------------------------------------------------------------
    // 3. Helpers (also recursive)
    // ------------------------------------------------------------------

    /// <summary>Finds the path (root → node) that contains the given MAC, or <c>null</c>.</summary>
    public static List<string>? FindPath(DeploymentNode node, string mac)
    {
        if (node.SensorMacs.Contains(mac, StringComparer.OrdinalIgnoreCase))
            return new List<string> { node.Name };                                // base case: found
        foreach (var child in node.Children)
        {
            var sub = FindPath(child, mac);                                       // recursive case
            if (sub is not null) { sub.Insert(0, node.Name); return sub; }
        }
        return null;                                                               // base case: not here
    }

    /// <summary>Ensures every tier on the path exists, creating missing tiers, and attaches the MAC at the leaf.</summary>
    public static DeploymentNode EnsurePath(DeploymentNode node, IReadOnlyList<string> path, int index, string mac)
    {
        if (index >= path.Count - 1)
        {
            if (!node.SensorMacs.Contains(mac, StringComparer.OrdinalIgnoreCase)) node.SensorMacs.Add(mac);
            return node;
        }
        var nextName = path[index + 1];
        var next = node.Children.FirstOrDefault(c => string.Equals(c.Name, nextName, StringComparison.OrdinalIgnoreCase));
        if (next is null)
        {
            next = new DeploymentNode(nextName, KindForDepth(index + 1));
            node.Children.Add(next);
        }
        return EnsurePath(next, path, index + 1, mac);
    }

    public static int CountSensors(DeploymentNode node) =>
        node.SensorMacs.Count + node.Children.Sum(CountSensors);

    private static string KindForDepth(int depth) => depth switch
    {
        0 => "Facility", 1 => "Zone", 2 => "SubZone", 3 => "Rack", _ => "Tier"
    };
}
