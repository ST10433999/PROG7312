namespace SmartX.Api.Mesh;

/// <summary>
/// Part 3 — turns the distance between two ESP32 nodes into the two numbers the routing algorithms care about.
/// <para>
/// <b>Energy</b> uses the first-order radio model (Heinzelman, Chandrakasan and Balakrishnan, 2000). Sending k bits
/// over d metres costs E_tx = E_elec·k + ε_amp·k·d², and receiving them costs E_rx = E_elec·k. The amplifier term
/// makes long, weak links expensive. The electronics term makes every extra hop cost something too. A route
/// should therefore use strong links, and no more of them than it needs.
/// </para>
/// <para>
/// <b>Signal strength</b> uses the log-distance path-loss model, RSSI = P₀ − 10·n·log₁₀(d), with P₀ = −40 dBm at 1 m and an
/// indoor/greenhouse exponent n = 2.7. Links weaker than the receiver sensitivity are not created.
/// </para>
/// </summary>
public static class RadioModel
{
    /// <summary>One telemetry frame: 250 bytes.</summary>
    public const double PacketBits = 2_000;
    /// <summary>Electronics energy per bit, transmit or receive (50 nJ/bit).</summary>
    public const double ElectronicsJoulesPerBit = 50e-9;
    /// <summary>Amplifier energy per bit per m² (100 pJ/bit/m²).</summary>
    public const double AmplifierJoulesPerBitPerSquareMetre = 100e-12;
    /// <summary>Energy per frame on the wired PoE backbone between the central gateway and the zone routers.</summary>
    public const double BackboneMicroJoules = 5;

    public const double ReferenceRssi = -40;
    public const double PathLossExponent = 2.7;
    /// <summary>Link cut-off: −78 dBm leaves a fade margin above ESP32 Wi-Fi sensitivity and gives ≈25 m range with the values above.</summary>
    public const int SensitivityDbm = -78;

    /// <summary>Transmit + receive energy for one frame over <paramref name="metres"/>, in microjoules.</summary>
    public static double LinkEnergyMicroJoules(double metres)
    {
        var tx = ElectronicsJoulesPerBit * PacketBits + AmplifierJoulesPerBitPerSquareMetre * PacketBits * metres * metres;
        var rx = ElectronicsJoulesPerBit * PacketBits;
        return Math.Round((tx + rx) * 1e6, 2);
    }

    public static int Rssi(double metres) =>
        (int)Math.Round(ReferenceRssi - 10 * PathLossExponent * Math.Log10(Math.Max(metres, 1)));

    public static bool InRange(double metres) => Rssi(metres) >= SensitivityDbm;

    /// <summary>Largest distance that still clears the receiver sensitivity.</summary>
    public static double MaxRangeMetres => Math.Pow(10, (ReferenceRssi - SensitivityDbm) / (10 * PathLossExponent));
}
