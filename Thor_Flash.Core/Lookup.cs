namespace Protocol.Thor.Library;

public static class Lookup {
    private static string[]? _split;

    public enum InitState {
        Failed, Downloaded, Cache
    }

    static string UsbIdsPath =>
        Path.Combine(AppContext.BaseDirectory, "usb.ids");

    public static async Task<InitState> Initialize() {
        try {
            var path = UsbIdsPath;
            if (!File.Exists(path)) {
                using var client = new HttpClient();
                var usbIds1 = await client
                    .GetStringAsync("http://www.linux-usb.org/usb.ids");
                await File.WriteAllTextAsync(path, usbIds1);
                _split = usbIds1.Split("\n");
                return InitState.Downloaded;
            }

            var usbIds2 = await File.ReadAllTextAsync(path);
            _split = usbIds2.Split("\n");
            return InitState.Cache;
        } catch {
            return InitState.Failed;
        }
    }

    public static string GetDisplayName(int vendorId, int productId) {
        if (_split == null)
            return "Failed to load device name database";

        var name = LookupUsbIdsName(vendorId, productId);
        if (vendorId == Communication.USB.Vendor)
            return NormalizeSamsungDisplayName(productId, name);
        return name;
    }

    private static string LookupUsbIdsName(int vendorId, int productId) {
        var str = "";
        var found = false;
        foreach (var line in _split!) {
            if (!found) {
                if (line.StartsWith(vendorId.ToString("x4"))) found = true;
            } else if (line.StartsWith($"\t{productId:x4}")) {
                str += $"{line[7..]}";
            }
        }

        if (!found)
            return "Unable to find device in database";
        return str;
    }

    /// <summary>
    /// usb.ids suele listar PIDs Odin antiguos (p. ej. Galaxy S II). Samsung reutiliza esos PIDs
    /// en Download para Exynos, MediaTek, Qualcomm, etc. Solo afecta al nombre en UI.
    /// </summary>
    private static string NormalizeSamsungDisplayName(int productId, string usbIdsName) {
        if (usbIdsName.Contains("Download mode", StringComparison.OrdinalIgnoreCase)
            || productId == 0x685D)
            return "Samsung Download/Odin mode";

        if (usbIdsName.Contains("Galaxy S II", StringComparison.OrdinalIgnoreCase)) {
            return productId switch {
                0x685B => "Samsung (modo almacenamiento)",
                0x685E => "Samsung (depuración USB)",
                _ => "Samsung Phone"
            };
        }

        return usbIdsName;
    }
}
