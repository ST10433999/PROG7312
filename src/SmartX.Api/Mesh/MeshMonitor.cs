namespace SmartX.Api.Mesh;

/// <summary>
/// Part 3 — background service that keeps the mesh in step with the rest of the gateway. Every couple of seconds it
/// re-reads the Part 1 heartbeat (nodes that stopped publishing) and the sensor registry (nodes registered or deleted),
/// so the network heals itself even when nobody has the topology page open.
/// </summary>
public sealed class MeshMonitor(MeshNetworkService mesh, ILogger<MeshMonitor> logger, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mesh.UseHeartbeat = config.GetValue("Mesh:UseHeartbeat", true);
        var interval = TimeSpan.FromMilliseconds(Math.Max(250, config.GetValue("Mesh:RefreshMilliseconds", 2000)));
        using var timer = new PeriodicTimer(interval);

        try
        {
            do
            {
                try { mesh.Refresh(); }
                catch (Exception ex) { logger.LogError(ex, "Mesh refresh failed."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
