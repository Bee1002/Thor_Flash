using System.Runtime.InteropServices;
using Protocol.Thor.Library.Platform;

namespace Protocol.Thor.Library.Communication;

public static class USB {
    public const int Vendor = 0x04E8;

    private static IHandler? _windowsHandler;
    private static Exception? _handlerError;

    public static bool TryGetHandler(out IHandler handler) {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            handler = null!;
            return false;
        }

        if (_handlerError != null) {
            handler = null!;
            return false;
        }

        try {
            handler = _windowsHandler ??= new WindowsUsbHandler();
            return true;
        } catch (Exception ex) {
            _handlerError = ex;
            handler = null!;
            return false;
        }
    }

    public static string? GetHandlerError() => _handlerError?.Message;

    public static string GetSupported() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "(ninguna en este build)";

    /// <summary>Desconecta y libera libusb al cerrar la aplicación (evita crash en el finalizer).</summary>
    public static void Shutdown() {
        if (_windowsHandler is WindowsUsbHandler win) {
            win.Disconnect();
            WindowsUsbHandler.ReleaseSharedContext();
        }
        _windowsHandler = null;
    }
}
