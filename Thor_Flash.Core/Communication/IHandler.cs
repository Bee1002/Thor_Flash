namespace Protocol.Thor.Library.Communication; 

public interface IHandler {
    public List<DeviceInfo> GetDevices();
    public void Initialize(string? id, byte[]? direct = null);
    public bool IsConnected();
    public void Disconnect();
    public void BulkWrite(byte[] buf, int timeout = 5000, bool zlp = false);
    public byte[] BulkRead(int amount, out int read, int timeout = 5000);
    public void SendZLP();
    public void ReadZLP();

    /// <summary>Vaciar datos pendientes antes del handshake LOKE.</summary>
    void PrepareForOdin() { }

    /// <summary>Descripción del enlace USB activo (interfaz/endpoints).</summary>
    string? GetLinkDescription() => null;

    /// <summary>Informe de diagnóstico USB (implementado en Windows).</summary>
    string Diagnose(string? deviceId = null) =>
        "Diagnóstico USB no disponible en esta plataforma.";

    /// <summary>Tras fallo LOKE, probar otra interfaz bulk (solo Windows).</summary>
    bool TryNextOdinInterface() => false;

    /// <summary>Entre particiones del lote NAND.</summary>
    void OnFlashPartitionBoundary() { }

    /// <summary>Antes del lote NAND completo.</summary>
    void PrepareForFlashBatch() { ReadZLP(); }

    /// <summary>Consulta DVIF (WinUSB Thor no soporta DVIF sin romper LOKE).</summary>
    bool SupportsDeviceInfoQuery => false;

    byte[] BulkReadExact(int amount, int timeout = 5000) {
        var buf = new byte[amount];
        var offset = 0;
        var started = Environment.TickCount64;
        var idleSpins = 0;
        while (offset < amount) {
            var elapsed = (int)(Environment.TickCount64 - started);
            var remaining = timeout - elapsed;
            if (remaining <= 0)
                throw new ApplicationException("Bulk read failed: Timeout");
            var chunk = BulkRead(amount - offset, out var n, remaining);
            if (n > 0) {
                Buffer.BlockCopy(chunk, 0, buf, offset, n);
                offset += n;
                idleSpins = 0;
            } else {
                idleSpins++;
                if (idleSpins > 200)
                    throw new ApplicationException("Bulk read failed: Timeout");
                Thread.Sleep(10);
            }
        }
        return buf;
    }
}