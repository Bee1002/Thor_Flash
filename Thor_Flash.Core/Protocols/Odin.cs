using System.Text;
using Serilog;
using Protocol.Thor.Library;
using Protocol.Thor.Library.Communication;
using Protocol.Thor.Library.PIT;

namespace Protocol.Thor.Library.Protocols; 

public class Odin {
    private const int HandshakeTimeoutMs = 30000;
    private const int SessionStartTimeoutMs = 30000;
    private const int PitBlockSize = 500;
    private const int PitTimeoutMs = 120000;
    /// <summary>ACK LOKE durante inicio de flash (SetTotalBytes, RequestFileFlash…).</summary>
    private const int FlashCmdTimeoutMs = 120000;

    public struct VersionStruct {
        public byte Unknown1;
        public byte Unknown2;
        public short Version;
    }
    
    private IHandler _handler;
    private int FlashTimeout;
    private int FlashPartAckTimeout = 120000;
    private int FlashPacketSize;
    private int FlashSequence;
    public bool ResetFlashCount;
    public VersionStruct Version;
    public bool BootloaderUpdate;
    public bool EfsClear;

    public IHandler Handler => _handler;

    public Odin(IHandler handler)
        => _handler = handler;

    public void Handshake() {
        _handler.PrepareForOdin();
        try {
            _handler.BulkWrite(Encoding.ASCII.GetBytes("ODIN"));
            var buf = _handler.BulkReadExact(4, HandshakeTimeoutMs);
            var str = Encoding.ASCII.GetString(buf);
            if (str != "LOKE") throw new InvalidDataException(
                $"Handshake: esperado LOKE, recibido «{str}»");
        } catch (ApplicationException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException(
                "Handshake LOKE: el teléfono no respondió a tiempo. " +
                "Desconecta, reinicia modo Download (apaga+volumen abajo+USB), Conectar e Iniciar Odin de nuevo. " +
                "Si acabas de cerrar una sesión Odin, es obligatorio reiniciar el teléfono.", ex);
        }
    }

    // Begin session region, 0x64
    public void BeginSession() {
        var buf = new byte[1024];
        buf.WriteInt(0x64, 0);
        buf.WriteInt(0x00, 4);
        // Write the proto version to be the max value
        // So it would basically catch-all BL versions
        buf.WriteInt(int.MaxValue, 8);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out var read, SessionStartTimeoutMs);
        if (read != 8) throw new InvalidDataException(
            $"BeginSession: recibidos {read} bytes en lugar de 8!");
        buf.OdinFailCheck("BeginSession");
        Version = new VersionStruct {
            Unknown1 = buf[4],
            Unknown2 = buf[5],
            Version = BitConverter.ToInt16(new[] {
                buf[6], buf[7]
            })
        };
        var version = buf.ReadInt(4);
        Log.Debug("Bootloader version integer 0x{0:X8}", version);
        Log.Debug("Unknown1: {0}, Unknown2: {1}, Version: {2}",
            Version.Unknown1, Version.Unknown2, Version.Version);
        switch (Version.Version) {
            case 0 or 1:
                FlashTimeout = 120000;     // 2 min (dispositivos antiguos)
                FlashPacketSize = 131072;  // 128 KiB
                FlashSequence = 240;       // 30 MB
                break;
            case >= 2:
                FlashTimeout = 600000;     // 10 min — escritura NAND en WinUSB puede superar 2 min
                FlashPacketSize = 1048576; // 1 MiB
                FlashSequence = 30;        // 30 MiB
                break;
        }

        if (Version.Unknown1 != 0) {
            Log.Information("Unknown1 is not zero: {0:x2}", Version.Unknown1);
            Log.Information("Campo de versión desconocido — conviene reportarlo al desarrollador de Thor_Flash.");
        }
        
        if (Version.Unknown2 != 0) {
            Log.Information("Unknown2 is not zero: {0:x2}", Version.Unknown2);
            Log.Information("Campo de versión desconocido — conviene reportarlo al desarrollador de Thor_Flash.");
        }

        if (Version.Version > 1) {
            Log.Debug("Sending file part size of {0}", FlashPacketSize);
            buf = new byte[1024];
            buf.WriteInt(0x64, 0);
            buf.WriteInt(0x05, 4);
            buf.WriteInt(FlashPacketSize, 8);
            _handler.BulkWrite(buf);
            buf = _handler.BulkRead(8, out read);
            if (read != 8) throw new InvalidDataException(
                $"Received {read} bytes instead of 8!");
            buf.OdinFailCheck("SendFilePartSize");
        }
    }

    public void SetTotalBytes(long total) {
        var buf = new byte[1024];
        buf.WriteInt(0x64, 0);
        buf.WriteInt(0x02, 4);
        // >4 GiB: dword bajo +8, dword alto +12 (Odin_Flash Cmd.cs / Heimdall PR #459).
        buf.WriteInt((int)(total & 0xFFFFFFFF), 8);
        buf.WriteInt((int)(total >> 32), 12);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out var read, FlashCmdTimeoutMs);
        if (read != 8) throw new InvalidDataException(
            $"Received {read} bytes instead of 8!");
        buf.OdinFailCheck("SetTotalBytes");
    }

    /// <summary>
    /// LOKE total del lote (Odin_Flash: suma <c>RawSize</c> descomprimido; lo/hi +8/+12 para &gt;4 GiB).
    /// BL v2: 0x64/0x02 con 0 antes del total. Una sola llamada al inicio.
    /// </summary>
    public void InitializeFlashTotal(long totalFileSize) {
        if (totalFileSize <= 0)
            return;
        if (Version.Version == 2)
            SetTotalBytes(0);
        SetTotalBytes(totalFileSize);
        Log.Debug(
            "InitializeFlashTotal: {Total:N0} bytes (lo=0x{Lo:X8} hi=0x{Hi:X8}, BL v{Bl})",
            totalFileSize,
            (uint)(totalFileSize & 0xFFFFFFFF),
            (int)(totalFileSize >> 32),
            Version.Version);
    }

    // End session region, 0x67
    public void EndSession() {
        var buf = new byte[1024];
        buf.WriteInt(0x67, 0);
        buf.WriteInt(0x00, 4);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out var read);
        if (read != 8) throw new InvalidDataException(
            $"Received {read} bytes instead of 8!");
        buf.OdinFailCheck("EndSession");
    }
    
    public void Reboot() {
        var buf = new byte[1024];
        buf.WriteInt(0x67, 0);
        buf.WriteInt(0x01, 4);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out var read);
        if (read != 8) throw new InvalidDataException(
            $"Received {read} bytes instead of 8!");
        buf.OdinFailCheck("Reboot");
    }
    
    public void Shutdown() {
        var buf = new byte[1024];
        buf.WriteInt(0x67, 0);
        buf.WriteInt(0x03, 4);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out var read);
        if (read != 8) throw new InvalidDataException(
            $"Received {read} bytes instead of 8!");
        buf.OdinFailCheck("Shutdown");
    }

    // PIT region, 0x65
    public byte[] DumpPIT(IProgress<PitDumpProgress>? progress = null) {
        progress?.Report(new PitDumpProgress {
            Message = "Solicitando PIT al bootloader (puede tardar en BL v3)…"
        });
        Log.Debug("PIT: solicitando volcado…");

        var buf = new byte[1024];
        buf.WriteInt(0x65, 0);
        buf.WriteInt(0x01, 4);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out var read, PitTimeoutMs);
        if (read != 8)
            throw new InvalidDataException($"RequestPitDump: recibidos {read} bytes, se esperaban 8.");
        buf.OdinFailCheck("RequestPitDump");
        var size = buf.ReadInt(4);
        if (size <= 0 || size > 16 * 1024 * 1024)
            throw new InvalidDataException($"Tamaño PIT inválido: {size} bytes");

        var blocks = (int)Math.Ceiling(size / (double)PitBlockSize);
        Log.Debug("PIT: {Size} bytes, {Blocks} bloques", size, blocks);
        progress?.Report(new PitDumpProgress {
            Message = $"PIT: {size:N0} bytes ({blocks} bloques)…",
            TotalBlocks = blocks
        });

        var pitBuf = new byte[size];
        for (var i = 0; i < blocks; i++) {
            progress?.Report(new PitDumpProgress {
                Message = $"PIT bloque {i + 1}/{blocks}…",
                BlockIndex = i + 1,
                TotalBlocks = blocks
            });

            buf = new byte[1024];
            buf.WriteInt(0x65, 0);
            buf.WriteInt(0x02, 4);
            buf.WriteInt(i, 8);
            _handler.BulkWrite(buf);

            var offset = i * PitBlockSize;
            var copyLen = Math.Min(PitBlockSize, size - offset);
            buf = _handler.BulkRead(PitBlockSize, out read, PitTimeoutMs);
            if (read < copyLen)
                throw new InvalidDataException(
                    $"PIT bloque {i + 1}/{blocks}: recibidos {read} bytes, se necesitan {copyLen}.");
            Array.Copy(buf, 0, pitBuf, offset, copyLen);
        }

        try {
            _handler.ReadZLP();
        } catch { /* ignorar */ }

        buf = new byte[1024];
        buf.WriteInt(0x65, 0);
        buf.WriteInt(0x03, 4);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out read, PitTimeoutMs);
        if (read != 8)
            throw new InvalidDataException($"EndPitDump: recibidos {read} bytes, se esperaban 8.");
        buf.OdinFailCheck("EndPitDump");

        progress?.Report(new PitDumpProgress { Message = "PIT leído correctamente." });
        Log.Debug("PIT: volcado completado ({Size} bytes)", size);
        return pitBuf;
    }
    
    public void FlashPIT(byte[] content) {
        // Request PIT flash
        var buf = new byte[1024];
        buf.WriteInt(0x65, 0);
        buf.WriteInt(0x00, 4);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out var read);
        if (read != 8) throw new InvalidDataException(
            $"Received {read} bytes instead of 8!");
        buf.OdinFailCheck("RequestPitFlash");
        
        // Begin PIT flash
        buf = new byte[1024];
        buf.WriteInt(0x65, 0);
        buf.WriteInt(0x02, 4);
        buf.WriteInt(content.Length, 8);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out read);
        if (read != 8) throw new InvalidDataException(
            $"Received {read} bytes instead of 8!");
        buf.OdinFailCheck("BeginPitFlash");
        
        // Send the PIT file
        _handler.BulkWrite(content);
        buf = _handler.BulkRead(8, out read, 120000);
        if (read != 8) throw new InvalidDataException(
            $"Received {read} bytes instead of 8!");
        buf.OdinFailCheck("SendPitFile");
        
        // End PIT flash
        buf = new byte[1024];
        buf.WriteInt(0x65, 0);
        buf.WriteInt(0x03, 4);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out read);
        if (read != 8) throw new InvalidDataException(
            $"Received {read} bytes instead of 8!");
        buf.OdinFailCheck("EndPitFlash");
    }

    public class FlashProgressInfo {
        public enum StateEnum {
            Sending, Flashing
        }
        
        public int SequenceIndex;
        public int TotalSequences;
        public StateEnum State;
        public long TotalBytes;
        public long SentBytes;

        public FlashProgressInfo(int index, int sequences, 
            long sent, long total, StateEnum state) {
            SequenceIndex = index; TotalSequences = sequences;
            SentBytes = sent; TotalBytes = total; State = state;
        }
    }
    
    // Flashing region, 0x66
    public void FlashPartition(Stream? stream, PitEntry entry, Action<FlashProgressInfo> progress, long length = -1) {
        if (length < 0) {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream), "Se requiere stream o length explícito.");
            length = stream.Length;
        }

        // Request file flash
        var buf = new byte[1024];
        buf.WriteInt(0x66, 0);
        buf.WriteInt(0x00, 4);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out var read, FlashCmdTimeoutMs);
        if (read != 8) throw new InvalidDataException(
            $"Received {read} bytes instead of 8!");
        buf.OdinFailCheck("RequestFileFlash");

        var totalBytes = 0;
        var sequence = FlashPacketSize * FlashSequence;
        var sequences = (int)(length / sequence);
        var lastSequence = (int)(length % sequence);
        if (lastSequence != 0) sequences++;
        else lastSequence = sequence;
        for (var i = 0; i < sequences; i++) {
            var last = i + 1 == sequences;
            var realSize = last ? lastSequence : sequence;
            var alignedSize = realSize;
            if (realSize % FlashPacketSize != 0)
                alignedSize += FlashPacketSize - realSize % FlashPacketSize;
            progress(new FlashProgressInfo(i, sequences, 
                totalBytes, length, FlashProgressInfo.StateEnum.Sending));

            // Request sequence flash
            buf = new byte[1024];
            buf.WriteInt(0x66, 0);
            buf.WriteInt(0x02, 4);
            buf.WriteInt(alignedSize, 8);
            _handler.BulkWrite(buf);
            buf = _handler.BulkRead(8, out read, FlashCmdTimeoutMs);
            if (read != 8) throw new InvalidDataException(
                $"Received {read} bytes instead of 8!");
            buf.OdinFailCheck($"RequestSequenceFlash/{i}");

            // Send file part
            var parts = alignedSize / FlashPacketSize;
            for (var j = 0; j < parts; j++) {
                buf = new byte[FlashPacketSize];
                stream?.Read(buf, 0, FlashPacketSize);
                _handler.BulkWrite(buf);
                buf = _handler.BulkRead(8, out read, FlashPartAckTimeout);
                if (read != 8) throw new InvalidDataException(
                    $"SendFilePart/{i}: recibidos {read} bytes, se esperaban 8.");
                buf.OdinFailCheck($"SendFilePart/{i}");
                var index = buf.ReadInt(4);
                if (index != j) throw new InvalidOperationException(
                    $"Expected index to be {j} but bootloader sent {index}!");
                totalBytes += FlashPacketSize;
                progress(new FlashProgressInfo(i, sequences, 
                    totalBytes, length, FlashProgressInfo.StateEnum.Sending));
            }
            
            progress(new FlashProgressInfo(i, sequences,
                totalBytes, length, FlashProgressInfo.StateEnum.Flashing));
            Log.Information(
                "Flash {Partition}: escribiendo secuencia {Seq}/{Total} en NAND (hasta {Timeout}s)…",
                entry.Partition, i + 1, sequences, FlashTimeout / 1000);

            // End file sequence flash
            if (entry.BinaryType == 1) {
                // Flash modem firmware
                buf = new byte[1024];
                buf.WriteInt(0x66, 0);
                buf.WriteInt(0x03, 4);
                buf.WriteInt(0x01, 8);
                buf.WriteInt(realSize, 12);
                buf.WriteInt(entry.BinaryType, 16);
                buf.WriteInt(entry.DeviceType, 20);
                buf.WriteInt(last ? 1 : 0, 24);
                _handler.BulkWrite(buf);
            } else {
                // Flash phone firmware
                buf = new byte[1024];
                buf.WriteInt(0x66, 0);
                buf.WriteInt(0x03, 4);
                buf.WriteInt(0x00, 8);
                buf.WriteInt(realSize, 12);
                buf.WriteInt(entry.BinaryType, 16);
                buf.WriteInt(entry.DeviceType, 20);
                buf.WriteInt(entry.PartitionId, 24);
                buf.WriteInt(last ? 1 : 0, 28);
                buf.WriteInt(EfsClear ? 1 : 0, 32);
                buf.WriteInt(BootloaderUpdate ? 1 : 0, 36);
                _handler.BulkWrite(buf);
            }
            
            try {
                buf = _handler.BulkRead(8, out read, FlashTimeout);
            } catch (ApplicationException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException(
                    $"Timeout escribiendo «{entry.Partition}» (secuencia {i + 1}/{sequences}). " +
                    "El teléfono sigue grabando o el USB es lento; prueba otro puerto/cable.", ex);
            }
            if (read != 8) throw new InvalidDataException(
                $"EndSequenceFlash/{i}: recibidos {read} bytes, se esperaban 8.");
            buf.OdinFailCheck($"EndSequenceFlash/{i}", true);
        }
    }

    /// <summary>F. Reset Time de Odin oficial — una vez al final del lote, no por partición.</summary>
    public void SendResetFlashCount() {
        if (!ResetFlashCount)
            return;

        var buf = new byte[1024];
        buf.WriteInt(0x64, 0);
        buf.WriteInt(0x01, 4);
        _handler.BulkWrite(buf);
        buf = _handler.BulkRead(8, out var read);
        if (read != 8) throw new InvalidDataException(
            $"Received {read} bytes instead of 8!");
        buf.OdinFailCheck("ResetFlashCount");
    }
}