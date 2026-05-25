using System.ComponentModel;
using System.Formats.Tar;
using System.Runtime.CompilerServices;
using System.Text;
using K4os.Compression.LZ4.Streams;
using Serilog;
using Protocol.Thor.Library.PIT;
using Protocol.Thor.Library.Protocols;

namespace Protocol.Thor.Library;

public sealed class FlashPartitionItem : INotifyPropertyChanged {
    public required string TarPath { get; init; }
    public required string FileName { get; init; }
    public required PitEntry PitEntry { get; init; }

    private bool _selected = true;
    public bool Selected {
        get => _selected;
        set {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string DisplayLabel =>
        $"{Path.GetFileName(TarPath)} :: {FileName} → {PitEntry.Partition}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FlashProgressReport {
    public string Message { get; init; } = "";
    /// <summary>Imagen recién flasheada con éxito (para log estilo Odin).</summary>
    public string? CompletedFlashFile { get; init; }
    /// <summary>Bytes enviados del lote completo (BL+AP+CP+CSC).</summary>
    public long SentBytes { get; init; }
    /// <summary>Bytes totales del lote completo.</summary>
    public long TotalBytes { get; init; }
    /// <summary>Bytes de la imagen/partición que se flashea ahora (0–100% en barra superior).</summary>
    public long PartitionSentBytes { get; init; }
    /// <summary>Tamaño de la imagen/partición actual.</summary>
    public long PartitionTotalBytes { get; init; }
    public int SequenceIndex { get; init; }
    public int TotalSequences { get; init; }
}

public sealed class PitDumpProgress {
    public string Message { get; init; } = "";
    public int BlockIndex { get; init; }
    public int TotalBlocks { get; init; }
}

public sealed class TarScanResult {
    public required List<FlashPartitionItem> Matches { get; init; }
    public required IReadOnlyList<string> TarPaths { get; init; }
    public string? Diagnostics { get; init; }
}

public static class OdinOperations {
    public static PitData LoadDevicePit(Odin odin, IProgress<PitDumpProgress>? progress = null) =>
        new(odin.DumpPIT(progress));

    public static bool IsTarArchivePath(string path) =>
        path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> ResolveTarPaths(string path) {
        IEnumerable<string> candidates;
        if (File.Exists(path)) {
            candidates = IsTarArchivePath(path) ? [path] : [];
        } else if (Directory.Exists(path)) {
            candidates = Directory.EnumerateFiles(path).Where(IsTarArchivePath);
        } else {
            yield break;
        }

        foreach (var file in candidates
                     .OrderBy(GetOdinTarSlotOrder)
                     .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
            yield return file;
    }

    /// <summary>Orden Odin oficial: BL → AP → CP → HOME_CSC → CSC.</summary>
    public static int GetOdinTarSlotOrder(string tarPath) {
        var name = Path.GetFileName(tarPath);
        if (MatchesTarSlotPrefix(name, "BL")) return 0;
        if (MatchesTarSlotPrefix(name, "AP")) return 1;
        if (MatchesTarSlotPrefix(name, "CP")) return 2;
        if (MatchesTarSlotPrefix(name, "HOME_CSC") || MatchesTarSlotPrefix(name, "HOME-CSC")) return 3;
        if (MatchesTarSlotPrefix(name, "CSC")) return 4;
        return 10;
    }

    public static string GetOdinTarSlotLabel(string tarPath) =>
        GetOdinTarSlotOrder(tarPath) switch {
            0 => "BL",
            1 => "AP",
            2 => "CP",
            3 => "HOME_CSC",
            4 => "CSC",
            _ => "?"
        };

    private static bool MatchesTarSlotPrefix(string fileName, string prefix) =>
        fileName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains('_' + prefix + '_', StringComparison.OrdinalIgnoreCase);

    /// <summary>Dentro de BL, preloader/lk antes que el resto.</summary>
    private static int GetPartitionFlashOrder(PitEntry entry, string fileName) {
        var norm = NormalizeTarMemberName(fileName);
        var part = entry.Partition;
        if (norm.Contains("preloader", StringComparison.OrdinalIgnoreCase)
            || part.Equals("bootloader", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (norm.StartsWith("lk", StringComparison.OrdinalIgnoreCase)
            || part.Equals("lk", StringComparison.OrdinalIgnoreCase)
            || part.StartsWith("lk", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (entry.BinaryType == 1)
            return 50;
        return 10;
    }

    /// <summary>Nombre del miembro TAR para flash (conserva .lz4).</summary>
    public static string GetTarMemberStorageName(string name) {
        var n = name.Replace('\\', '/').Trim();
        while (n.StartsWith("./", StringComparison.Ordinal))
            n = n[2..];
        return n;
    }

    /// <summary>Quita sufijos .lz4 anidados (p. ej. boot.img.lz4 → boot.img) — solo emparejamiento PIT.</summary>
    public static string NormalizeTarMemberName(string name) {
        var n = GetTarMemberStorageName(name);
        if (n.Contains('/'))
            return n;
        while (n.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase))
            n = n[..^4];
        return n;
    }

    public static bool IsTarRootMember(string entryName) {
        var n = NormalizeTarMemberName(entryName);
        return !string.IsNullOrEmpty(n) && !n.Contains('/');
    }

    public static bool TarMemberNamesMatch(string left, string right) {
        left = NormalizeTarMemberName(left);
        right = NormalizeTarMemberName(right);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        var leftBase = Path.GetFileNameWithoutExtension(left);
        var rightBase = Path.GetFileNameWithoutExtension(right);
        if (string.IsNullOrEmpty(leftBase) || string.IsNullOrEmpty(rightBase))
            return false;
        if (string.Equals(leftBase, rightBase, StringComparison.OrdinalIgnoreCase))
            return true;

        // Samsung BL: PIT «preloader.img» ↔ TAR «preloader_XXXX.bin»
        if (leftBase.StartsWith("preloader", StringComparison.OrdinalIgnoreCase)
            && rightBase.StartsWith("preloader", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static PitEntry? TryMatchPitEntry(PitData pit, string tarMemberName) {
        var normalized = NormalizeTarMemberName(tarMemberName);
        PitEntry? loose = null;

        foreach (var entry in pit.Entries) {
            if (string.IsNullOrWhiteSpace(entry.FileName))
                continue;

            if (TarMemberNamesMatch(entry.FileName, tarMemberName))
                return entry;

            var pitBase = Path.GetFileNameWithoutExtension(NormalizeTarMemberName(entry.FileName));
            var tarBase = Path.GetFileNameWithoutExtension(normalized);
            if (!string.IsNullOrEmpty(pitBase)
                && string.Equals(pitBase, tarBase, StringComparison.OrdinalIgnoreCase))
                loose ??= entry;
        }

        return loose;
    }

    public static string FormatPit(PitData data, PitDisplayOptions? options = null) =>
        PitDisplayFormatter.Format(data, options);

    public static TarScanResult ScanFirmwareSource(string path, PitData pit) {
        var tarPaths = ResolveTarPaths(path).ToList();
        if (tarPaths.Count == 0) {
            var hint = File.Exists(path)
                ? "La ruta es un archivo que no es .tar / .tar.md5."
                : Directory.Exists(path)
                    ? "La carpeta no contiene archivos .tar ni .tar.md5."
                    : "Ruta no encontrada.";
            return new TarScanResult {
                Matches = [],
                TarPaths = tarPaths,
                Diagnostics = hint
            };
        }

        var result = new List<FlashPartitionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tarPath in tarPaths) {
            using var tar = File.OpenRead(tarPath);
            using var reader = new TarReader(tar);
            while (reader.GetNextEntry() is { } entry) {
                if (!IsTarRootMember(entry.Name))
                    continue;
                var pitEntry = TryMatchPitEntry(pit, entry.Name);
                if (pitEntry == null)
                    continue;

                var key = $"{tarPath}|{entry.Name}|{pitEntry.PartitionId}";
                if (!seen.Add(key))
                    continue;

                result.Add(new FlashPartitionItem {
                    TarPath = tarPath,
                    FileName = entry.Name,
                    PitEntry = pitEntry,
                    Selected = true
                });
            }
        }

        return new TarScanResult {
            Matches = result,
            TarPaths = tarPaths,
            Diagnostics = result.Count == 0
                ? BuildScanDiagnostics(tarPaths, pit)
                : null
        };
    }

    public static string BuildScanDiagnostics(IReadOnlyList<string> tarPaths, PitData pit) {
        var sb = new StringBuilder();
        sb.AppendLine("Diagnóstico escaneo (sin coincidencias PIT ↔ .tar):");
        sb.AppendLine($"  Archivos .tar: {tarPaths.Count}");
        foreach (var tarPath in tarPaths) {
            sb.AppendLine($"  • {Path.GetFileName(tarPath)}");
            var members = new List<string>();
            try {
                using var tar = File.OpenRead(tarPath);
                using var reader = new TarReader(tar);
                while (reader.GetNextEntry() is { } entry) {
                    if (!IsTarRootMember(entry.Name))
                        continue;
                    members.Add(NormalizeTarMemberName(entry.Name));
                }
            } catch (Exception ex) {
                sb.AppendLine($"    (no legible: {ex.Message})");
                continue;
            }

            if (members.Count == 0) {
                sb.AppendLine("    (vacío o solo subcarpetas)");
                continue;
            }

            foreach (var name in members.Take(24))
                sb.AppendLine($"    - {name}");
            if (members.Count > 24)
                sb.AppendLine($"    … y {members.Count - 24} más");
        }

        sb.AppendLine("  Nombres esperados según PIT (columna Archivo):");
        var expected = pit.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.FileName))
            .Select(e => $"{e.FileName} → {e.Partition}")
            .Distinct()
            .Take(32)
            .ToList();
        if (expected.Count == 0)
            sb.AppendLine("    (el PIT no define nombres de archivo)");
        else
            foreach (var line in expected)
                sb.AppendLine($"    - {line}");

        sb.AppendLine("  Los miembros del .tar deben coincidir con esos nombres (mayúsculas/minúsculas y .lz4 se toleran).");
        sb.AppendLine("  Usa «Ver PIT (dispositivo)» o flashea por «Archivo suelto» si solo tienes una imagen.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Bytes reales que LOKE contabiliza (Odin_Flash <c>RawSize</c>): descomprimido para .lz4, tamaño TAR en resto.
    /// </summary>
    public static long GetTarEntryFlashBytes(string tarPath, string fileName) {
        if (!TryGetTarEntryDataOffset(tarPath, fileName, out var dataOffset, out var tarSize))
            throw new InvalidOperationException(
                $"No se encontró «{fileName}» en {Path.GetFileName(tarPath)}.");

        if (fileName.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase))
            return ReadLz4DecompressedSize(tarPath, dataOffset, fileName);

        return tarSize;
    }

    static long ReadLz4DecompressedSize(string path, long dataOffset, string contextName) {
        using var fs = File.OpenRead(path);
        fs.Position = dataOffset;
        using var lz4 = LZ4Stream.Decode(fs);
        var size = lz4.Length;
        if (size <= 0)
            throw new InvalidOperationException(
                $"No se pudo obtener el tamaño descomprimido de {contextName}.");
        return size;
    }

    /// <summary>
    /// Localiza el payload de un miembro TAR (header USTAR 512 B + datos alineados).
    /// Igual que Odin_Flash / build WORKING — no usar TarReader.Position (offset incorrecto).
    /// </summary>
    static bool TryGetTarEntryDataOffset(
        string tarPath,
        string fileName,
        out long dataOffset,
        out long dataSize) {
        dataOffset = 0;
        dataSize = 0;

        using var fs = File.OpenRead(tarPath);
        var offset = 0L;
        var header = new byte[512];

        while (fs.Read(header, 0, 512) == 512) {
            if (header[0] == 0)
                break;

            var name = ParseTarName(header);
            if (name.EndsWith('/')) {
                offset += 512;
                fs.Position = offset;
                continue;
            }

            var fileSize = ParseTarOctalField(header, 124, 12);
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) {
                dataOffset = offset + 512;
                dataSize = fileSize;
                return true;
            }

            offset += 512 + TarDataPaddedSize(fileSize);
            fs.Position = offset;
        }

        return false;
    }

    static long TarDataPaddedSize(long size) =>
        ((size + 511) / 512) * 512;

    static string ParseTarName(ReadOnlySpan<byte> header) {
        var end = header[..100].IndexOf((byte)0);
        if (end < 0)
            end = 100;
        return Encoding.ASCII.GetString(header[..end]).TrimEnd('\0', ' ');
    }

    static long ParseTarOctalField(ReadOnlySpan<byte> header, int offset, int length) {
        long value = 0;
        for (var i = 0; i < length; i++) {
            var b = header[offset + i];
            if (b is 0 or (byte)' ')
                break;
            if (b is < (byte)'0' or > (byte)'7')
                continue;
            value = value * 8 + (b - '0');
        }

        return value;
    }

    public static long SumSelectedBytes(IEnumerable<FlashPartitionItem> items) {
        long total = 0;
        foreach (var item in items.Where(x => x.Selected))
            total += GetTarEntryFlashBytes(item.TarPath, item.FileName);
        return total;
    }

    public static void FlashSelected(
        Odin odin,
        IEnumerable<FlashPartitionItem> items,
        IProgress<FlashProgressReport>? progress = null,
        CancellationToken cancellationToken = default) {
        var selected = items.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("No hay particiones seleccionadas.");

        var orderedGroups = selected
            .GroupBy(x => x.TarPath)
            .OrderBy(g => GetOdinTarSlotOrder(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalBytes = SumSelectedBytes(selected);
        if (orderedGroups.Any(g => GetOdinTarSlotOrder(g.Key) == 0))
            odin.BootloaderUpdate = true;
        odin.Handler.PrepareForOdin();
        odin.Handler.PrepareForFlashBatch();
        Log.Information(
            "Flash lote: BootloaderUpdate={BlUpdate}, EfsClear={EfsClear}, ResetFlashCount={Reset}",
            odin.BootloaderUpdate, odin.EfsClear, odin.ResetFlashCount);
        odin.InitializeFlashTotal(totalBytes);
        var slotOrder = string.Join(" → ",
            orderedGroups.Select(g => $"{GetOdinTarSlotLabel(g.Key)} ({Path.GetFileName(g.Key)})"));
        progress?.Report(new FlashProgressReport {
            Message = $"Iniciando flash de {selected.Count} imagen(es), {totalBytes:N0} bytes… Orden: {slotOrder}",
            TotalBytes = totalBytes
        });

        long bytesCompleted = 0;
        foreach (var group in orderedGroups) {
            var orderedItems = group
                .OrderBy(x => GetPartitionFlashOrder(x.PitEntry, x.FileName))
                .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var item in orderedItems) {
                cancellationToken.ThrowIfCancellationRequested();
                odin.Handler.OnFlashPartitionBoundary();
                bytesCompleted += FlashTarEntry(
                    odin, group.Key, item.FileName, item.PitEntry,
                    totalBytes, bytesCompleted, progress, cancellationToken);
            }
        }

        odin.ResetFlashCount = true;
        odin.SendResetFlashCount();

        progress?.Report(new FlashProgressReport {
            Message = "Flash completado.",
            SentBytes = totalBytes,
            TotalBytes = totalBytes,
            PartitionSentBytes = 0,
            PartitionTotalBytes = 0
        });
    }

    /// <summary>
    /// Progreso global del lote (BL+AP+CP+CSC): cada partición aporta su fracción al total.
    /// Usamos secuencias Odin, no los bytes USB alineados (Thor los infla y rompe el 0–100%).
    /// </summary>
    private static long MapPartitionSentBytes(Odin.FlashProgressInfo info, long partitionBytes) {
        if (partitionBytes <= 0 || info.TotalSequences <= 0)
            return 0;

        var sequencesDone = info.State == Odin.FlashProgressInfo.StateEnum.Flashing
            ? info.SequenceIndex + 1
            : info.SequenceIndex;
        sequencesDone = Math.Min(sequencesDone, info.TotalSequences);

        return (long)(partitionBytes * (sequencesDone / (double)info.TotalSequences));
    }

    private static long MapBatchSentBytes(
        Odin.FlashProgressInfo info,
        long partitionBytes,
        long bytesCompletedBefore,
        long batchTotalBytes) {
        if (batchTotalBytes <= 0)
            return 0;
        if (partitionBytes <= 0 || info.TotalSequences <= 0)
            return Math.Min(bytesCompletedBefore, batchTotalBytes);

        var sequencesDone = info.State == Odin.FlashProgressInfo.StateEnum.Flashing
            ? info.SequenceIndex + 1
            : info.SequenceIndex;
        sequencesDone = Math.Min(sequencesDone, info.TotalSequences);

        var partitionSent = (long)(partitionBytes * (sequencesDone / (double)info.TotalSequences));
        return Math.Min(bytesCompletedBefore + partitionSent, batchTotalBytes);
    }

    private static string FormatProgressMessage(
        string detail,
        long batchSent,
        long batchTotal,
        long partitionSent = 0,
        long partitionTotal = 0) {
        var parts = new List<string> { detail };
        if (partitionTotal > 0) {
            var partPct = Math.Clamp(partitionSent * 100.0 / partitionTotal, 0, 100);
            parts.Add($"{partPct:F0}% imagen");
        }

        if (batchTotal > 0) {
            var batchPct = Math.Clamp(batchSent * 100.0 / batchTotal, 0, 100);
            parts.Add($"{batchPct:F1}% lote total");
        }

        return string.Join(" — ", parts);
    }

    private static FlashProgressReport MakeFlashReport(
        string detail,
        Odin.FlashProgressInfo? info,
        long partitionBytes,
        long bytesCompletedBefore,
        long batchTotalBytes,
        int sequenceIndex = 0,
        int totalSequences = 0) {
        long partitionSent = 0;
        long batchSent = bytesCompletedBefore;
        if (info != null && partitionBytes > 0) {
            partitionSent = MapPartitionSentBytes(info, partitionBytes);
            batchSent = MapBatchSentBytes(info, partitionBytes, bytesCompletedBefore, batchTotalBytes);
        }

        return new FlashProgressReport {
            Message = FormatProgressMessage(detail, batchSent, batchTotalBytes, partitionSent, partitionBytes),
            SentBytes = batchSent,
            TotalBytes = batchTotalBytes,
            PartitionSentBytes = partitionSent,
            PartitionTotalBytes = partitionBytes,
            SequenceIndex = info?.SequenceIndex ?? sequenceIndex,
            TotalSequences = info?.TotalSequences ?? totalSequences
        };
    }

    private static long FlashTarEntry(
        Odin odin,
        string tarPath,
        string fileName,
        PitEntry pitEntry,
        long batchTotalBytes,
        long bytesCompletedBefore,
        IProgress<FlashProgressReport>? progress,
        CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTarEntryDataOffset(tarPath, fileName, out var dataOffset, out var tarSize))
            throw new InvalidOperationException(
                $"No se encontró «{fileName}» en {Path.GetFileName(tarPath)}.");

        var partitionBytes = fileName.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase)
            ? ReadLz4DecompressedSize(tarPath, dataOffset, fileName)
            : tarSize;

        using var fs = File.OpenRead(tarPath);
        fs.Position = dataOffset;
        Stream stream = fs;
        Stream? lz4Stream = null;
        if (fileName.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase)) {
            lz4Stream = LZ4Stream.Decode(fs);
            stream = lz4Stream;
        }

        try {
            progress?.Report(MakeFlashReport(
                $"Flasheando {fileName} → {pitEntry.Partition}…",
                null, partitionBytes, bytesCompletedBefore, batchTotalBytes));
            odin.FlashPartition(stream, pitEntry, info => {
                var detail = info.State == Odin.FlashProgressInfo.StateEnum.Flashing
                    ? $"Escribiendo {pitEntry.Partition} (seq {info.SequenceIndex + 1}/{info.TotalSequences})"
                    : $"Enviando {fileName} (seq {info.SequenceIndex + 1}/{info.TotalSequences})";
                progress?.Report(MakeFlashReport(
                    detail, info, partitionBytes, bytesCompletedBefore, batchTotalBytes));
            }, partitionBytes);
            progress?.Report(new FlashProgressReport {
                CompletedFlashFile = fileName,
                SentBytes = Math.Min(bytesCompletedBefore + partitionBytes, batchTotalBytes),
                TotalBytes = batchTotalBytes,
                PartitionSentBytes = partitionBytes,
                PartitionTotalBytes = partitionBytes
            });
        } finally {
            lz4Stream?.Dispose();
        }

        return partitionBytes;
    }

    public static void FlashPitFile(Odin odin, string pitFilePath) {
        var buf = File.ReadAllBytes(pitFilePath);
        _ = new PitData(buf);
        odin.FlashPIT(buf);
    }
}
