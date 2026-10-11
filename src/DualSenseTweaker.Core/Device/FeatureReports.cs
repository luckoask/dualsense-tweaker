using DualSenseTweaker.Core.Diagnostics;
using HidSharp;

namespace DualSenseTweaker.Core.Device;

internal static class FeatureReports
{
    /// <summary>
    /// Reads a feature report. HidD_GetFeature needs a buffer at least as long as the report;
    /// the declared maximum length always qualifies, the exact size is a fallback for drivers
    /// that reject oversized buffers.
    /// </summary>
    public static byte[]? TryGet(HidStream stream, int maxFeatureLength, byte reportId, int reportSize)
    {
        Exception? last = null;
        foreach (int length in new[] { Math.Max(maxFeatureLength, reportSize), reportSize }.Distinct())
        {
            var buffer = new byte[length];
            buffer[0] = reportId;
            try
            {
                stream.GetFeature(buffer);
                return buffer;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                last = ex;
            }
        }
        Log.Warn(L.Format($"Feature report 0x{reportId:X2} sa nepodarilo prečítať: {last?.Message}"));
        return null;
    }

    /// <summary>Pairing info (0x09) stores the MAC address little-endian in bytes 1–6.</summary>
    public static string? ParseMacAddress(ReadOnlySpan<byte> report)
    {
        if (report.Length < 7 || report[0] != DualSenseProtocol.FeaturePairingInfo)
            return null;
        var mac = report.Slice(1, 6).ToArray();
        if (mac.All(b => b == 0))
            return null;
        Array.Reverse(mac);
        return string.Join(':', mac.Select(b => b.ToString("X2")));
    }
}
