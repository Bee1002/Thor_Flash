using Protocol.Thor.Library.Communication;
using Protocol.Thor.Library.PIT;
using Protocol.Thor.Library.Protocols;

namespace Protocol.Thor.Library;

public sealed class OdinSession : IDisposable {
    private readonly IHandler _handler;
    private Odin? _odin;
    private PitData? _devicePit;

    public OdinSession(IHandler handler) => _handler = handler;

    public IHandler Handler => _handler;
    public Odin? Odin => _odin;
    public PitData? DevicePit => _devicePit;
    public bool IsUsbConnected => _handler.IsConnected();
    public bool IsOdinActive => _odin != null;

    public static bool IsPlatformSupported => USB.TryGetHandler(out _);

    public static IHandler CreateHandler() {
        if (!USB.TryGetHandler(out var handler)) {
            var detail = USB.GetHandlerError();
            throw new PlatformNotSupportedException(detail != null
                ? $"USB no disponible: {detail}"
                : $"Plataforma no soportada. Soportado: {USB.GetSupported()}");
        }
        return handler;
    }

    public void Connect(string deviceId) {
        if (_handler.IsConnected())
            throw new InvalidOperationException("Ya hay una conexión USB activa.");
        _handler.Initialize(deviceId);
        _devicePit = null;
    }

    public void BeginOdin() {
        if (!_handler.IsConnected())
            throw new InvalidOperationException("Conecta primero al dispositivo USB.");

        Exception? last = null;
        var canSwitchIface = _handler is Platform.WindowsUsbHandler;
        var maxAttempts = canSwitchIface ? 12 : 2;

        for (var attempt = 0; attempt < maxAttempts; attempt++) {
            try {
                StartOdinSession();
                return;
            } catch (Exception ex) when (IsTransientOdinFailure(ex)) {
                last = ex;
                _odin = null;

                if (canSwitchIface && _handler.TryNextOdinInterface())
                    continue;

                if (attempt == 0) {
                    _handler.PrepareForOdin();
                    Thread.Sleep(300);
                    continue;
                }

                break;
            }
        }

        var link = _handler.GetLinkDescription();
        var detail = link != null ? $" Último enlace: {link}." : "";
        throw new InvalidOperationException(
            (last?.Message ?? "No se pudo iniciar sesión Odin.") + detail, last);
    }

    private void StartOdinSession() {
        _odin = new Odin(_handler);
        _odin.Handshake();
        _odin.BeginSession();
        _devicePit = null;
    }

    private static bool IsTransientOdinFailure(Exception ex) {
        var msg = ex.ToString();
        return msg.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Handshake", StringComparison.OrdinalIgnoreCase);
    }

    public Odin RequireOdin() =>
        _odin ?? throw new InvalidOperationException("Inicia una sesión Odin primero.");

    public Odin.VersionStruct? BootloaderVersion => _odin?.Version;

    public void SetFlashOptions(bool efsClear) {
        if (_odin != null)
            _odin.EfsClear = efsClear;
    }

    public string DiagnoseUsb(string? deviceId = null) => _handler.Diagnose(deviceId);

    public byte[] DumpDevicePit() => RequireOdin().DumpPIT();

    public PitData GetOrLoadDevicePit(IProgress<PitDumpProgress>? progress = null) {
        RequireOdin();
        try {
            return _devicePit ??= OdinOperations.LoadDevicePit(_odin!, progress);
        } catch {
            _devicePit = null;
            throw;
        }
    }

    public void InvalidatePitCache() => _devicePit = null;

    public void EndOdinSession(bool tryShutdown = false) {
        if (_odin == null)
            return;
        try {
            if (tryShutdown)
                _odin.Shutdown();
            else
                _odin.EndSession();
        } catch {
            try {
                _odin.EndSession();
            } catch {
                /* ignorar al cerrar */
            }
        } finally {
            _odin = null;
            _devicePit = null;
        }
    }

    /// <summary>Reinicio tras flash (Thor: fin de sesión + reboot, como Odin «Auto Reboot»).</summary>
    public void RebootAfterFlash() {
        var odin = RequireOdin();
        try {
            odin.EndSession();
        } catch {
            /* ignorar */
        }

        odin.Reboot();
        _odin = null;
        _devicePit = null;
    }

    /// <summary>Reinicio manual (Thor: fin de sesión + reboot).</summary>
    public void RebootDevice() {
        var odin = RequireOdin();
        try {
            odin.EndSession();
        } catch {
            /* ignorar */
        }

        odin.Reboot();
        _odin = null;
        _devicePit = null;
    }

    /// <summary>Reinicio a modo Download (Thor: fin de sesión + rebootToOdin).</summary>
    public void RebootToOdinDevice() {
        var odin = RequireOdin();
        try {
            odin.EndSession();
        } catch {
            /* ignorar */
        }

        try {
            odin.RebootToOdin();
        } catch {
            odin.Reboot();
        }

        _odin = null;
        _devicePit = null;
    }

    public void DisconnectUsb() {
        EndOdinSession();
        if (_handler.IsConnected())
            _handler.Disconnect();
    }

    public void Dispose() => DisconnectUsb();
}
