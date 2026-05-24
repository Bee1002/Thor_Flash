using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Protocol.Thor.Library.Communication;

namespace Protocol.Thor.Library;

/// <summary>Lee información del dispositivo (DVIF) vía LOKE — compatible con bulk USB y COM.</summary>
public static class DeviceDvif {
    public sealed class DeviceIdentity {
        public string? Model { get; init; }
        public string? UniqueId { get; init; }
        public int? CapaGb { get; init; }
        public string? Vendor { get; init; }
        public string? FirmwareVersion { get; init; }
        public string? ProductId { get; init; }
        public string? Provision { get; init; }
        public string? SalesCode { get; init; }
        public string? BuildNumber { get; init; }
        public string? DidNumber { get; init; }
        public string? TmuNumber { get; init; }

        public static DeviceIdentity FromDictionary(IReadOnlyDictionary<string, string> raw) {
            int? capa = null;
            if (raw.TryGetValue("capa", out var capaStr)
                && int.TryParse(capaStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                capa = c;

            return new DeviceIdentity {
                CapaGb = capa,
                ProductId = Get(raw, "product"),
                Model = Get(raw, "model"),
                FirmwareVersion = Get(raw, "fwver"),
                Vendor = Get(raw, "vendor"),
                SalesCode = Get(raw, "sales"),
                BuildNumber = Get(raw, "ver"),
                DidNumber = Get(raw, "did"),
                UniqueId = Get(raw, "un"),
                TmuNumber = Get(raw, "tmu_temp"),
                Provision = Get(raw, "prov"),
            };
        }

        private static string? Get(IReadOnlyDictionary<string, string> raw, string key) =>
            raw.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    }

    public static IReadOnlyDictionary<string, string> TryReadRaw(IHandler handler) {
        if (!handler.SupportsDeviceInfoQuery)
            return new Dictionary<string, string>();

        try {
            handler.PrepareForOdin();
            handler.BulkWrite(Encoding.ASCII.GetBytes("DVIF"));
            Thread.Sleep(400);

            var sb = new StringBuilder();
            for (var i = 0; i < 8; i++) {
                var chunk = handler.BulkRead(4096, out var n, 1500);
                if (n > 0)
                    sb.Append(Encoding.ASCII.GetString(chunk, 0, n));
                else if (sb.Length > 0)
                    break;
                else
                    Thread.Sleep(80);
            }

            return ParseResponse(sb.ToString());
        } catch {
            return new Dictionary<string, string>();
        }
    }

    public static DeviceIdentity TryRead(IHandler handler) =>
        DeviceIdentity.FromDictionary(TryReadRaw(handler));

    internal static IReadOnlyDictionary<string, string> ParseResponse(string read) {
        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(read))
            return info;

        foreach (var item in read.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = Regex.Split(item.Replace("#", "").Replace("@", ""), "=");
            if (parts.Length < 2)
                continue;
            var key = parts[0].Trim();
            var value = parts[1].Trim();
            if (key.Length == 0 || value.Length == 0)
                continue;
            info[key.ToLowerInvariant()] = value;
        }

        return info;
    }
}
