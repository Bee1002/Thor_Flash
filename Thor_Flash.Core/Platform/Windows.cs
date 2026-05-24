using System.Text;
using LibUsbDotNet;
using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Serilog;
using Protocol.Thor.Library.Communication;
using Protocol.Thor.Library;

namespace Protocol.Thor.Library.Platform;

public sealed class WindowsUsbHandler : IHandler, IDisposable {
    private static UsbContext? _sharedContext;
    private static string? _initError;
    private static readonly object InitLock = new();

    private UsbDevice? _device;
    private UsbEndpointReader? _reader;
    private UsbEndpointWriter? _writer;
    private int _interfaceNumber;
    private int _alternateSetting;
    private bool _kernelDetached;
    private bool _connected;
    private bool _writeZlp = true;
    private int _outMaxPacketSize = 512;
    private readonly List<OdinUsbInterface> _odinInterfaces = [];
    private int _activeInterfaceIndex;

    public string GetNotes() {
        var baseNotes =
            "Windows (LibUsbDotNet):\n" +
            "1) Teléfono SOLO en modo Download/Odin (pantalla azul/verde con advertencia).\n" +
            "2) Cierra Samsung Odin, Smart Switch y cualquier herramienta que use el puerto.\n" +
            "3) Si 'Conectar' falla: Administrador de dispositivos o Zadig → interfaz bulk/CDC → WinUSB.\n" +
            "4) En Zadig elige la interfaz con clase 0x0A (CDC Data), NO el módem 0x02.\n" +
            "5) Tras cambiar driver, desenchufa/reenchufa el cable y reinicia modo Download.";
        if (_initError != null)
            return baseNotes + "\n\nERROR libusb: " + _initError;
        return baseNotes;
    }

    public string Diagnose(string? deviceId = null) {
        EnsureUsbContext();
        var sb = new StringBuilder();
        sb.AppendLine("=== Diagnóstico USB ===");

        var found = false;
        foreach (var dev in _sharedContext!.List()) {
            if (dev.VendorId != USB.Vendor || dev is not UsbDevice usb)
                continue;
            var id = FormatId(usb);
            if (deviceId != null && id != deviceId)
                continue;
            found = true;
            try {
                sb.AppendLine(DescribeDevice(usb));
            } catch (Exception ex) {
                sb.AppendLine($"Error al analizar {id}: {ex.Message}");
            }
            sb.AppendLine();
        }

        if (!found)
            sb.AppendLine(deviceId == null
                ? "No hay dispositivos con VID 04E8 (Samsung). ¿Modo Download activo?"
                : $"No se encontró el dispositivo {deviceId}. Pulsa Actualizar.");
        sb.AppendLine(DriverHelpText());
        return sb.ToString();
    }

    public List<DeviceInfo> GetDevices() {
        EnsureUsbContext();
        var list = new List<DeviceInfo>();
        foreach (var dev in _sharedContext!.List()) {
            if (dev.VendorId != USB.Vendor || dev is not UsbDevice usb)
                continue;
            list.Add(new DeviceInfo {
                DisplayName =
                    $"{Lookup.GetDisplayName(usb.VendorId, usb.ProductId)} " +
                    $"[PID {usb.ProductId:X4}] [{FormatId(usb)}]",
                Identifier = FormatId(usb)
            });
        }

        return list;
    }

    public void Initialize(string? id, byte[]? direct = null) {
        if (direct != null) {
            ParseDescriptorOnly(direct);
            return;
        }

        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("ID or Direct should not be null");

        EnsureUsbContext();
        var listed = FindById(id)
            ?? throw new InvalidOperationException("Device disconnected or not found?");

        if (listed.Clone() is not UsbDevice device)
            throw new InvalidOperationException("No se pudo obtener un handle estable del dispositivo.");

        _device = device;
        _writeZlp = true;

        try {
            OpenUsbDevice(device);
            _odinInterfaces.Clear();
            _odinInterfaces.AddRange(EnumerateOdinInterfaces(device));
            if (_odinInterfaces.Count == 0)
                throw new InvalidOperationException(
                    "No se encontraron interfaces bulk Odin. Ejecuta Diagnóstico USB.");

            if (!ActivateInterfaceAt(0))
                throw new InvalidOperationException("No se pudo activar la interfaz USB Odin.");

            _connected = true;
            Log.Debug("USB OK: {Link} ({Count} interfaz(es) bulk)",
                GetLinkDescription(), _odinInterfaces.Count);
        } catch {
            CleanupDevice();
            throw;
        }
    }

    public string? GetLinkDescription() =>
        _odinInterfaces.Count > 0 && _activeInterfaceIndex < _odinInterfaces.Count
            ? _odinInterfaces[_activeInterfaceIndex].Describe()
            : null;

    public bool TryNextOdinInterface() {
        if (!_connected || _activeInterfaceIndex + 1 >= _odinInterfaces.Count)
            return false;
        Log.Debug("Probando siguiente interfaz Odin…");
        return ActivateInterfaceAt(_activeInterfaceIndex + 1);
    }

    public bool IsConnected() => _connected;

    public void Disconnect() => Dispose();

    public void BulkWrite(byte[] buf, int timeout = 5000, bool zlp = false) {
        EnsureConnected();
        var err = WriteOnce(buf, timeout);
        if (IsRecoverableTransferError(err)) {
            TryRecoverEndpoints();
            err = WriteOnce(buf, timeout);
        }

        if (err != Error.Success)
            throw new ApplicationException($"Bulk write failed: {err}");

        if (_writeZlp && !zlp && buf.Length > 0 && buf.Length % _outMaxPacketSize == 0) {
            try {
                SendZLP();
            } catch {
                _writeZlp = false;
            }
        }
    }

    private Error WriteOnce(byte[] buf, int timeout) {
        var err = _writer!.Write(buf, timeout, out var transferLength);
        if (err != Error.Success)
            return err;
        if (transferLength != buf.Length)
            throw new ApplicationException($"Bulk write short: {transferLength}/{buf.Length}");
        return Error.Success;
    }

    public void PrepareForOdin() {
        if (!_connected || _reader == null)
            return;
        try {
            _reader.ReadFlush();
        } catch (Exception ex) {
            Log.Debug(ex, "ReadFlush antes de Odin");
        }
        Thread.Sleep(50);
    }

    public byte[] BulkRead(int amount, out int read, int timeout = 5000) {
        EnsureConnected();
        if (amount == 0) {
            var dummy = Array.Empty<byte>();
            var zlpErr = _reader!.Read(dummy, timeout, out read);
            if (zlpErr != Error.Success)
                throw new ApplicationException($"Bulk read (ZLP) failed: {zlpErr}");
            return Array.Empty<byte>();
        }

        var buf = new byte[amount];
        var err = ReadOnce(buf, timeout, out read);
        if (IsRecoverableTransferError(err)) {
            TryRecoverEndpoints();
            err = ReadOnce(buf, timeout, out read);
        }

        if (err == Error.Timeout && read > 0) {
            var partial = new byte[read];
            Buffer.BlockCopy(buf, 0, partial, 0, read);
            return partial;
        }

        if (err != Error.Success)
            throw new ApplicationException($"Bulk read failed: {err}");
        var arr = new byte[read];
        Buffer.BlockCopy(buf, 0, arr, 0, read);
        return arr;
    }

    private Error ReadOnce(byte[] buf, int timeout, out int read) =>
        _reader!.Read(buf, timeout, out read);

    private static bool IsRecoverableTransferError(Error err) =>
        err is Error.Io or Error.Pipe or Error.Overflow;

    private void TryRecoverEndpoints() {
        try {
            _reader?.ClearHalt();
            _writer?.ClearHalt();
            _reader?.ReadFlush();
        } catch (Exception ex) {
            Log.Debug(ex, "TryRecoverEndpoints");
        }
    }

    public void SendZLP() {
        EnsureConnected();
        BulkWrite(Array.Empty<byte>(), 100, zlp: true);
    }

    public void ReadZLP() {
        EnsureConnected();
        BulkRead(0, out _, 100);
    }

    public void Dispose() => CleanupDevice();

    /// <summary>Libera el contexto libusb compartido. Llamar al cerrar la aplicación.</summary>
    public static void ReleaseSharedContext() {
        lock (InitLock) {
            if (_sharedContext == null)
                return;
            var ctx = _sharedContext;
            _sharedContext = null;
            try {
                ctx.Dispose();
            } catch (Exception ex) {
                Log.Debug(ex, "UsbContext.Dispose");
            }
        }
    }

    private static void EnsureUsbContext() {
        if (_sharedContext != null)
            return;
        lock (InitLock) {
            if (_sharedContext != null)
                return;
            try {
                _sharedContext = new UsbContext();
            } catch (Exception ex) {
                _initError = ex.Message;
                Log.Error(ex, "No se pudo inicializar libusb");
                throw new InvalidOperationException(
                    "No se pudo cargar libusb-1.0. Comprueba que libusb-1.0.dll está junto al ejecutable.", ex);
            }
        }
    }

    private void EnsureConnected() {
        if (!_connected)
            throw new InvalidOperationException("Not connected to a device!");
    }

    private bool ActivateInterfaceAt(int index) {
        if (_device == null || index < 0 || index >= _odinInterfaces.Count)
            return false;

        ReleaseActiveInterface();

        var iface = _odinInterfaces[index];
        _interfaceNumber = iface.Number;
        _alternateSetting = iface.Alternate;
        _activeInterfaceIndex = index;

        try {
            if (_device.IsKernelDriverActive(_interfaceNumber)) {
                _device.DetachKernelDriver(_interfaceNumber);
                _kernelDetached = true;
            }
        } catch (Exception ex) {
            Log.Debug(ex, "DetachKernelDriver");
        }

        try {
            _device.ClaimInterface(_interfaceNumber);
        } catch (UsbException ex) {
            throw new ApplicationException(
                $"No se pudo reclamar {iface.Describe()} ({ex.ErrorCode}). " +
                DriverHelpText(), ex);
        }

        if (_alternateSetting != 0)
            _device.SetAltInterface(_alternateSetting);

        _reader = _device.OpenEndpointReader(ToReadId(iface.ReadEp), 1024 * 1024, EndpointType.Bulk);
        _writer = _device.OpenEndpointWriter(ToWriteId(iface.WriteEp), EndpointType.Bulk);
        try {
            _outMaxPacketSize = _device.GetMaxPacketSize(iface.WriteEp);
            if (_outMaxPacketSize <= 0)
                _outMaxPacketSize = 512;
        } catch {
            _outMaxPacketSize = 512;
        }

        try {
            _reader.ClearHalt();
            _writer.ClearHalt();
        } catch (Exception ex) {
            Log.Debug(ex, "ClearHalt");
        }

        PrepareForOdin();
        return true;
    }

    private void ReleaseActiveInterface() {
        _reader = null;
        _writer = null;

        if (_device?.IsOpen != true)
            return;

        try {
            if (_kernelDetached) {
                try {
                    _device.AttachKernelDriver(_interfaceNumber);
                } catch (Exception ex) {
                    Log.Debug(ex, "AttachKernelDriver");
                }
                _kernelDetached = false;
            }

            _device.ReleaseInterface(_interfaceNumber);
        } catch (Exception ex) {
            Log.Debug(ex, "ReleaseInterface");
        }
    }

    private void CleanupDevice() {
        _connected = false;
        _writeZlp = false;
        _odinInterfaces.Clear();
        _activeInterfaceIndex = 0;
        ReleaseActiveInterface();
        _reader = null;
        _writer = null;

        if (_device != null) {
            try {
                if (_device.IsOpen)
                    _device.Close();
            } catch (Exception ex) {
                Log.Debug(ex, "Al cerrar dispositivo USB");
            }

            _device.Dispose();
            _device = null;
        }

        _kernelDetached = false;
    }

    private static string FormatId(UsbDevice dev) =>
        $"{dev.BusNumber:D3}:{dev.Address:D3}";

    private static UsbDevice? FindById(string id) {
        var parts = id.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var bus)
            || !int.TryParse(parts[1], out var addr))
            throw new InvalidDataException($"Invalid device id '{id}', expected bus:address (e.g. 001:023).");

        foreach (var dev in _sharedContext!.List()) {
            if (dev is not UsbDevice usb || usb.VendorId != USB.Vendor)
                continue;
            if (usb.BusNumber == bus && usb.Address == addr)
                return usb;
        }

        return null;
    }

    private static List<OdinUsbInterface> EnumerateOdinInterfaces(UsbDevice device) {
        var list = new List<OdinUsbInterface>();
        foreach (var config in device.Configs) {
            foreach (var iface in config.Interfaces) {
                byte? readEp = null;
                byte? writeEp = null;
                foreach (var ep in iface.Endpoints) {
                    if (((EndpointType)(ep.Attributes & 0x03)) != EndpointType.Bulk)
                        continue;
                    if (ep.EndpointAddress > 0x80)
                        readEp = ep.EndpointAddress;
                    else
                        writeEp = ep.EndpointAddress;
                }

                if (!readEp.HasValue || !writeEp.HasValue)
                    continue;

                list.Add(new OdinUsbInterface(
                    iface.Number,
                    iface.AlternateSetting,
                    readEp.Value,
                    writeEp.Value,
                    iface.Class));
            }
        }

        return list
            .OrderByDescending(i => i.Class == ClassCode.Data)
            .ThenBy(i => i.Number)
            .ThenBy(i => i.Alternate)
            .ToList();
    }

    private static (byte Read, byte Write)? FindBulkEndpoints(UsbDevice device, out int interfaceNumber,
        out int alternate) {
        var list = EnumerateOdinInterfaces(device);
        interfaceNumber = 0;
        alternate = 0;
        if (list.Count == 0)
            return null;
        var first = list[0];
        interfaceNumber = first.Number;
        alternate = first.Alternate;
        return (first.ReadEp, first.WriteEp);
    }

    private static string DescribeDevice(UsbDevice usb) {
        var sb = new StringBuilder();
        sb.AppendLine($"Dispositivo: {FormatId(usb)}  VID={usb.VendorId:X4}  PID={usb.ProductId:X4}");

        if (usb.IsOpen)
            return sb.ToString() + DescribeOpenDevice(usb);

        sb.AppendLine("Estado: enumerado pero aún no abierto por la app.");
        sb.AppendLine("Prueba de apertura (como al pulsar Conectar):");

        if (usb.Clone() is not UsbDevice probe) {
            sb.AppendLine("  No se pudo clonar el handle USB.");
            return sb.ToString();
        }

        try {
            OpenUsbDevice(probe);
            sb.AppendLine("  → Apertura: OK");
            sb.Append(DescribeOpenDevice(probe, "  "));
            if (FindBulkEndpoints(probe, out var iface, out _) is { } ep)
                sb.AppendLine($"  → Interfaz Odin candidata: #{iface} (IN 0x{ep.Read:X2}, OUT 0x{ep.Write:X2})");
            else
                sb.AppendLine("  → No se encontró interfaz bulk CDC 0x0A.");
        } catch (UsbException ex) {
            sb.AppendLine($"  → Apertura: FALLÓ — {ex.ErrorCode} ({ExplainLibUsbError(ex.ErrorCode)})");
            sb.AppendLine("  → Solución: driver WinUSB con Zadig (interfaz 0x0A), ver ayuda abajo.");
        } catch (Exception ex) {
            sb.AppendLine($"  → Apertura: FALLÓ — {ex.Message}");
        } finally {
            try {
                if (probe.IsOpen)
                    probe.Close();
            } catch {
                /* ignorar */
            }
            probe.Dispose();
        }

        return sb.ToString();
    }

    private static string DescribeOpenDevice(UsbDevice usb, string indent = "") {
        var sb = new StringBuilder();
        sb.AppendLine($"{indent}Config activa: {usb.Configuration}");
        foreach (var config in usb.Configs) {
            sb.AppendLine($"{indent}  Config {config.ConfigurationValue}:");
            foreach (var iface in config.Interfaces) {
                sb.Append($"{indent}    Interfaz {iface.Number} alt {iface.AlternateSetting} " +
                          $"clase 0x{(byte)iface.Class:X2}");
                try {
                    if (usb.IsKernelDriverActive(iface.Number))
                        sb.Append(" [DRIVER KERNEL ACTIVO — bloquea libusb]");
                } catch (Exception ex) {
                    sb.Append($" [driver: {ex.Message}]");
                }
                sb.AppendLine();
                foreach (var ep in iface.Endpoints)
                    sb.AppendLine($"{indent}      EP 0x{ep.EndpointAddress:X2} " +
                                  $"tipo {(EndpointType)(ep.Attributes & 0x03)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>Abre el dispositivo y aplica configuración. Open() siempre antes que SetAutoDetach/Configs.</summary>
    private static void OpenUsbDevice(UsbDevice device) {
        if (device.IsOpen)
            return;

        try {
            if (!device.TryOpen())
                throw new ApplicationException(BuildOpenDeniedMessage(device));
        } catch (UsbException ex) {
            throw new ApplicationException(BuildOpenFailureMessage(device, ex), ex);
        }

        try {
            device.SetAutoDetachKernelDriver(true);
        } catch (Exception ex) {
            Log.Debug(ex, "SetAutoDetachKernelDriver");
        }

        try {
            if (device.Configs.Count > 0 && device.Configuration == 0)
                device.SetConfiguration(device.Configs[0].ConfigurationValue);
        } catch (Exception ex) {
            throw new ApplicationException(
                $"USB abierto pero falló la configuración: {ex.Message}", ex);
        }
    }

    private static string BuildOpenDeniedMessage(UsbDevice device) =>
        $"No se pudo abrir el USB (el driver de Windows bloquea el acceso).{Environment.NewLine}" +
        $"VID={device.VendorId:X4} PID={device.ProductId:X4} [{FormatId(device)}]{Environment.NewLine}" +
        DriverHelpText();

    private static string BuildOpenFailureMessage(UsbDevice device, UsbException ex) {
        var sb = new StringBuilder();
        sb.AppendLine($"No se pudo abrir el USB — {ex.ErrorCode} ({ExplainLibUsbError(ex.ErrorCode)}).");
        sb.AppendLine($"VID={device.VendorId:X4} PID={device.ProductId:X4} [{FormatId(device)}]");
        sb.AppendLine(DriverHelpText());
        sb.AppendLine("Ejecuta «Diagnóstico» para ver interfaces y la prueba de apertura.");
        return sb.ToString();
    }

    private static string ExplainLibUsbError(Error error) => error switch {
        Error.Access => "acceso denegado (driver de Windows bloqueando libusb)",
        Error.Busy => "dispositivo ocupado (cierra Odin/Smart Switch)",
        Error.NotFound => "no encontrado",
        Error.NoDevice => "desconectado",
        Error.NotSupported => "no soportado en este sistema",
        _ => error.ToString()
    };

    private static string DriverHelpText() =>
        "Driver Windows (tu PID 685D = modo Download OK, falta WinUSB):\n" +
        "• Zadig https://zadig.akeo.ie/ (admin) → Options → List All Devices\n" +
        "  → entradas SAMSUNG / CDC Composite con USB ID 04E8 685D\n" +
        "  → prueba Interface 0, 1 y 2 hasta ver CDC Data (0x0A) + Bulk\n" +
        "  → destino WinUSB (o libusbK) → Replace Driver\n" +
        "  → desenchufa, modo Download otra vez, Conectar en la app\n" +
        "• Guía completa: docs/Instalar-WinUSB-Samsung.md (junto al .exe)\n" +
        "• La INF de Microsoft con VID_0547 es OTRO dispositivo; no la uses.";

    private static void ParseDescriptorOnly(byte[] direct) {
        using var stream = new MemoryStream(direct);
        using var reader = new BinaryReader(stream);
        stream.Seek(1, SeekOrigin.Current);
        if (reader.ReadByte() != 0x01)
            throw new InvalidDataException("USB_DT_DEVICE assertion fail!");
        stream.Seek(6, SeekOrigin.Current);
        if (reader.ReadInt16() != USB.Vendor)
            throw new InvalidDataException("This is not a Samsung device!");
    }

    private static ReadEndpointID ToReadId(byte endpoint) {
        var id = endpoint & 0x0F;
        if (id is < 1 or > 15)
            throw new InvalidOperationException($"Endpoint IN inválido: 0x{endpoint:X2}");
        return (ReadEndpointID)endpoint;
    }

    private static WriteEndpointID ToWriteId(byte endpoint) {
        var id = endpoint & 0x0F;
        if (id is < 1 or > 15)
            throw new InvalidOperationException($"Endpoint OUT inválido: 0x{endpoint:X2}");
        return (WriteEndpointID)endpoint;
    }
}
