using Protocol.Thor.Library.Communication;
using Protocol.Thor.Library.PIT;
using Protocol.Thor.Library.Protocols;

namespace Protocol.Thor.Library;

/// <summary>
/// Flash NAND por puerto COM — layout LOKE de Odin_Flash (distinto al bulk USB de Thor).
/// </summary>
/// <remarks>
/// Reservado para un futuro <see cref="Communication.IHandler"/> serial.
/// Thor_Flash Windows usa <see cref="Protocols.Odin.FlashPartition"/> vía WinUSB;
/// no llama a esta clase. Requiere cablear <see cref="Protocols.Odin.ApplyHandlerFlashProfile"/>
/// al iniciar sesión si se añade un handler COM.
/// </remarks>
public static class SerialFlashOperations {
    private const int CmdSession = 0x64;
    private const int CmdFlash = 0x66;
    private const int FlashPartAckTimeoutMs = 120000;
    private const int FlashNandTimeoutMs = 600000;
    private const int RegisteredPacketSize = 1048576;

    public static void LokeInitializeForFlash(Odin odin, long totalFileSize) {
        if (totalFileSize <= 0)
            return;

        var handler = odin.Handler;
        var response = SendLoke(odin, CmdSession, 0, binaryType: 5);
        var variant = (response.ReadInt(4) & 0xFFFF0000) >> 16;

        if (variant == 5)
            SendLoke(odin, CmdSession, 12, readResponse: false);

        if (variant == 2)
            SendLoke(odin, CmdSession, 2);

        if (variant is 3 or 4 or 5)
            SendLoke(odin, CmdSession, 5, binaryType: RegisteredPacketSize);

        var lo = (uint)(totalFileSize & 0xFFFFFFFF);
        var hi = (int)(totalFileSize >> 32);
        SendLoke(odin, CmdSession, 2, binaryType: lo, sizeWritten: hi);

        if (variant == 4) {
            SendLoke(odin, 0x69, 0);
            SendLoke(odin, 0x69, 1);
            SendLoke(odin, 0x69, 2);
        }
    }

    public static void FlashPartition(
        Odin odin,
        Stream stream,
        PitEntry entry,
        long size,
        Action<Odin.FlashProgressInfo> progress) {
        var maxSession = entry.DeviceType is 1 or 2 or 8 ? 31457280 : 104857600;
        var sessionCount = (int)(size / maxSession);
        if (size % maxSession != 0)
            sessionCount++;

        var totalSent = 0L;
        for (var sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++) {
            var sessionLen = (int)Math.Min(maxSession, size - totalSent);
            var isLast = sessionIndex == sessionCount - 1;
            SendSession(odin, stream, entry, sessionLen, isLast, sessionIndex, sessionCount, totalSent, size, progress);
            totalSent += sessionLen;
        }
    }

    private static void SendSession(
        Odin odin,
        Stream stream,
        PitEntry entry,
        int sessionLength,
        bool isLastSession,
        int sessionIndex,
        int sessionCount,
        long bytesBeforeSession,
        long partitionSize,
        Action<Odin.FlashProgressInfo> progress) {
        var handler = odin.Handler;
        var chunkBytes = odin.SerialFlashChunkBytes;
        var ackDelay = odin.SerialFlashAckDelayMs;

        handler.PrepareNandSession();
        SendLoke(odin, CmdFlash, 0);

        var reserved = Calculate128K(sessionLength);
        SendLoke(odin, CmdFlash, 2, binaryType: reserved);
        handler.DiscardNandPayloadPrefix();

        progress(new Odin.FlashProgressInfo(
            sessionIndex, sessionCount, bytesBeforeSession, partitionSize,
            Odin.FlashProgressInfo.StateEnum.Sending));

        var flashBuffer = new byte[chunkBytes];
        var sent = 0;
        while (sent < sessionLength) {
            Array.Clear(flashBuffer, 0, flashBuffer.Length);
            var toRead = Math.Min(flashBuffer.Length, sessionLength - sent);
            ReadExactly(stream, flashBuffer, toRead);
            handler.BulkWrite(flashBuffer, FlashPartAckTimeoutMs);
            if (ackDelay > 0)
                Thread.Sleep(ackDelay);
            ReadNandAck(odin, $"NAND data ACK {entry.Partition} +{sent}");
            sent += toRead;
            progress(new Odin.FlashProgressInfo(
                sessionIndex, sessionCount,
                bytesBeforeSession + sent, partitionSize,
                Odin.FlashProgressInfo.StateEnum.Sending));
        }

        progress(new Odin.FlashProgressInfo(
            sessionIndex, sessionCount,
            bytesBeforeSession + sessionLength, partitionSize,
            Odin.FlashProgressInfo.StateEnum.Flashing));

        if (entry.BinaryType == 1) {
            WriteFinishLoke(odin, entry, sessionLength, isLastSession,
                identifier: isLastSession ? 1 : 0,
                sessionEnd: entry.DeviceType,
                efsClear: entry.PartitionId);
        } else {
            WriteFinishLoke(odin, entry, sessionLength, isLastSession,
                deviceId: entry.DeviceType,
                identifier: entry.PartitionId,
                sessionEnd: isLastSession ? 1 : 0);
        }
    }

    private static void WriteFinishLoke(
        Odin odin, PitEntry entry, int sessionLength, bool isLastSession,
        int deviceId = 0, int identifier = 0, int sessionEnd = 0, int efsClear = 0) {
        var buf = BuildLokeBuffer(
            CmdFlash, 3, entry.BinaryType, sessionLength,
            deviceId, identifier, sessionEnd, efsClear,
            odin.BootloaderUpdate ? 1 : 0);
        var h = odin.Handler;
        h.BulkWrite(buf, FlashNandTimeoutMs);
        var response = h.BulkRead(8, out var read, FlashNandTimeoutMs);
        if (read != 8)
            throw new InvalidDataException(
                $"EndSequenceFlash: respuesta incompleta ({read}/8 bytes).");
        response.OdinFailCheck("EndSequenceFlash", end: true);
    }

    private static byte[] SendLoke(
        Odin odin,
        int cmd,
        int seq,
        long binaryType = 0,
        int sizeWritten = 0,
        int deviceId = 0,
        int identifier = 0,
        int sessionEnd = 0,
        int efsClear = 0,
        int bootUpdate = 0,
        int timeoutMs = FlashPartAckTimeoutMs,
        bool readResponse = true,
        string? context = null) {
        var buf = BuildLokeBuffer(cmd, seq, binaryType, sizeWritten, deviceId, identifier, sessionEnd, efsClear, bootUpdate);
        var h = odin.Handler;
        h.BulkWrite(buf, timeoutMs);
        if (!readResponse)
            return Array.Empty<byte>();

        var response = h.BulkRead(8, out var read, timeoutMs);
        if (read != 8)
            throw new InvalidDataException(
                $"LOKE 0x{cmd:X2}/0x{seq:X2}: respuesta incompleta ({read}/8 bytes).");
        response.OdinFailCheck(context ?? $"LOKE 0x{cmd:X2}/0x{seq:X2}");
        return response;
    }

    private static byte[] BuildLokeBuffer(
        int cmd, int seq, long binaryType, int sizeWritten,
        int deviceId, int identifier, int sessionEnd, int efsClear, int bootUpdate) {
        var buf = new byte[1024];
        buf.WriteInt(cmd, 0);
        buf.WriteInt(seq, 4);
        if (cmd == 0x64) {
            if (seq is 0 or 5)
                buf.WriteLong(binaryType, 8);
            else if (sizeWritten != 0 || binaryType != 0) {
                buf.WriteInt((int)binaryType, 8);
                buf.WriteInt(sizeWritten, 12);
            }
        } else {
            buf.WriteInt((int)binaryType, 8);
            buf.WriteInt(sizeWritten, 12);
        }
        buf.WriteInt(0, 16);
        buf.WriteInt(deviceId, 20);
        buf.WriteInt(identifier, 24);
        buf.WriteInt(sessionEnd, 28);
        buf.WriteInt(efsClear, 32);
        buf.WriteInt(bootUpdate, 36);
        return buf;
    }

    private static void ReadNandAck(Odin odin, string context, int timeoutMs = FlashPartAckTimeoutMs) {
        var resp = odin.Handler.BulkRead(8, out var n, timeoutMs);
        if (n != 8)
            throw new InvalidDataException($"{context}: respuesta incompleta ({n}/8 bytes).");
        resp.OdinFailCheck(context);
    }

    private static int Calculate128K(int sessionLen) {
        const int align = 1 << 17;
        if (sessionLen <= 0) return 0;
        return ((sessionLen - 1) / align + 1) * align;
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count) {
        var total = 0;
        while (total < count) {
            var read = stream.Read(buffer, total, count - total);
            if (read <= 0)
                throw new EndOfStreamException(
                    $"Fin de stream inesperado (esperados {count} bytes, leídos {total}).");
            total += read;
        }
    }
}
