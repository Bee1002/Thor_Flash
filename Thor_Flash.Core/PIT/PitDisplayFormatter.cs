using System.Text;

namespace Protocol.Thor.Library.PIT;

public static class PitDisplayFormatter {
    static readonly HashSet<string> SensitivePartitions = new(StringComparer.OrdinalIgnoreCase) {
        "efs", "sec_efs", "frp", "persist", "proinfo", "nvram", "nvdata", "nvcfg",
        "steady", "keyrefuge", "keydata", "seccfg", "protect1", "protect2", "sec1",
    };

    static readonly Dictionary<string, string> PartitionHints = new(StringComparer.OrdinalIgnoreCase) {
        ["userdata"] = "Datos de usuario / almacenamiento interno",
        ["super"] = "Sistema Android (particiones dinámicas)",
        ["efs"] = "Red / calibración — no flashear sin firmware completo consciente",
        ["sec_efs"] = "EFS seguro — no flashear sin firmware completo consciente",
        ["frp"] = "Protección FRP",
        ["boot"] = "Kernel / arranque",
        ["recovery"] = "Recovery",
        ["vbmeta"] = "Verified boot metadata",
        ["pit"] = "Tabla de particiones en el dispositivo",
    };

    public static string Format(PitData data, PitDisplayOptions? options = null) {
        options ??= PitDisplayOptions.TechnicianDefault;
        var sb = new StringBuilder();
        AppendSummary(sb, data, options);

        var mapper = data.Mapper;
        var startLabel = mapper.BlockSize;
        var countLabel = mapper.BlockCount;
        var shown = 0;

        for (var i = 0; i < data.Entries.Count; i++) {
            var e = data.Entries[i];
            var flashable = !string.IsNullOrWhiteSpace(e.FileName);
            if (!options.ShowAllEntries && !flashable)
                continue;

            shown++;
            var sensitive = SensitivePartitions.Contains(e.Partition);
            sb.AppendLine($"--- {e.Partition} (id {e.PartitionId}){(sensitive ? "  [Sensible]" : "")} ---");

            if (flashable)
                sb.AppendLine($"  Archivo Odin: {e.FileName.Trim()}");
            else
                sb.AppendLine("  Archivo Odin: (sin archivo — no suele ir en .tar AP)");

            sb.AppendLine($"  {startLabel}: {e.BlockSize} · {countLabel}: {e.BlockCount}");

            if (!string.IsNullOrWhiteSpace(e.DeltaName))
                sb.AppendLine($"  Delta: {e.DeltaName.Trim()}");

            if (PartitionHints.TryGetValue(e.Partition, out var hint))
                sb.AppendLine($"  Nota: {hint}");

            if (options.ShowAdvancedDetails) {
                sb.AppendLine($"  BinaryType: {mapper.BinaryType.GetMapping(e.BinaryType + 1)} ({e.BinaryType})");
                sb.AppendLine($"  DeviceType: {mapper.DeviceType.GetMapping(e.DeviceType + 1)} ({e.DeviceType})");
            }

            sb.AppendLine();
        }

        if (shown == 0)
            sb.AppendLine("(Ninguna entrada coincide con el filtro actual.)");

        return sb.ToString();
    }

    static void AppendSummary(StringBuilder sb, PitData data, PitDisplayOptions options) {
        var flashable = data.Entries.Count(e => !string.IsNullOrWhiteSpace(e.FileName));
        var sensitive = data.Entries
            .Where(e => SensitivePartitions.Contains(e.Partition))
            .Select(e => e.Partition)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tableVersion = data.IsNewVersion ? "v2" : "v1";
        sb.AppendLine("=== Resumen PIT ===");
        sb.AppendLine($"Proyecto: {data.Project} · {data.Unknown} · Tabla {tableVersion}");
        sb.AppendLine($"Entradas: {data.Entries.Count} · Flasheables (con archivo Odin): {flashable}");

        if (options.ShowAllEntries)
            sb.AppendLine($"Mostrando: todas ({data.Entries.Count})");
        else
            sb.AppendLine($"Mostrando: solo flasheables ({flashable}) — activa «Ver todas» para el resto");

        if (sensitive.Count > 0) {
            var list = sensitive.Count <= 8
                ? string.Join(", ", sensitive)
                : string.Join(", ", sensitive.Take(8)) + $" (+{sensitive.Count - 8} más)";
            sb.AppendLine($"Particiones sensibles: {list}");
        }

        sb.AppendLine();
    }
}
