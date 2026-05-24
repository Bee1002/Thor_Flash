using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using Protocol.Thor.Library;
using Protocol.Thor.Library.Communication;
using Protocol.Thor.Library.PIT;

namespace Thor_Flash;

public partial class MainWindow : Window {
    private readonly OdinSession? _session;
    private readonly DispatcherTimer? _refreshTimer;
    private readonly ObservableCollection<FlashPartitionItem> _partitionItems = [];
    private CancellationTokenSource? _operationCts;
    private bool _busy;

    public MainWindow() {
        InitializeComponent();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new UiLogSink(AppendLog))
            .CreateLogger();

        PartitionsGrid.ItemsSource = _partitionItems;

        try {
            if (!OdinSession.IsPlatformSupported) {
                var err = USB.GetHandlerError();
                AppendLog(err != null ? $"ERROR USB: {err}" : "ERROR: Plataforma no soportada.");
                StatusText.Text = "USB no disponible";
                return;
            }

            var session = new OdinSession(OdinSession.CreateHandler());
            _session = session;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _refreshTimer = timer;
            timer.Tick += (_, _) => RefreshDevices(silent: true);
            Loaded += OnLoaded;
            Closed += (_, _) => {
                _operationCts?.Cancel();
                timer.Stop();
                session.Dispose();
            };
        } catch (Exception ex) {
            AppendLog($"Inicio fallido: {ex.Message}");
            StatusText.Text = "Error al iniciar";
            MessageBox.Show(ex.Message, "Thor_Flash", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) {
        if (_session == null || _refreshTimer == null)
            return;
        try {
            AppendLog("Thor_Flash — flasheo Odin para Windows (WPF + USB WinUSB).");
            AppendLog("Motor: Protocol.Thor.Library (Thor_Flash.Core).");
            AppendLog("USB handler v5 (flash NAND 10 min, PIT progreso, ZLP 1024B).");
            AppendLog(_session.Handler.GetNotes());
            await LoadUsbIdsAsync();
            RefreshDevices();
            _refreshTimer.Start();
        } catch (Exception ex) {
            AppendLog($"Carga inicial: {ex.Message}");
            Log.Debug(ex, "OnLoaded");
            MessageBox.Show(ex.Message, "Thor_Flash", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LoadUsbIdsAsync() {
        try {
            var state = await Lookup.Initialize();
            AppendLog(state switch {
                Lookup.InitState.Downloaded => "usb.ids descargado.",
                Lookup.InitState.Cache => "usb.ids cargado desde caché.",
                _ => "usb.ids no disponible."
            });
        } catch (Exception ex) {
            AppendLog($"usb.ids: {ex.Message}");
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void DriverHelpButton_Click(object sender, RoutedEventArgs e) {
        var path = Path.Combine(AppContext.BaseDirectory, "docs", "Instalar-WinUSB-Samsung.md");
        if (File.Exists(path)) {
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = path,
                    UseShellExecute = true
                });
            } catch {
                AppendLog($"Abre manualmente: {path}");
            }
        } else {
            AppendLog("Guía: Zadig → List All Devices → SAMSUNG 04E8:685D → interfaz CDC 0x0A → WinUSB");
        }
        AppendLog("Tu dispositivo: VID 04E8 PID 685D (Download). El driver Samsung debe cambiarse a WinUSB.");
    }

    private void DiagnoseButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null) return;
        try {
            var id = DeviceCombo.SelectedItem is DeviceInfo d ? d.Identifier : null;
            AppendLog(_session.DiagnoseUsb(id));
        } catch (Exception ex) {
            AppendLog($"Diagnóstico: {ex.Message}");
        }
    }

    private void RefreshDevices(bool silent = false) {
        if (_session == null || _session.IsUsbConnected || _busy)
            return;
        try {
            var devices = _session.Handler.GetDevices();
            DeviceCombo.ItemsSource = devices;
            DeviceCombo.IsEnabled = devices.Count > 0;
            if (devices.Count > 0)
                DeviceCombo.SelectedIndex = 0;
            if (!silent)
                AppendLog(devices.Count == 0
                    ? "No hay dispositivos Samsung (04E8)."
                    : $"Encontrados {devices.Count} dispositivo(s).");
        } catch (Exception ex) {
            AppendLog($"Enumerar: {ex.Message}");
        }
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null) return;
        if (DeviceCombo.SelectedItem is not DeviceInfo device) {
            AppendLog("Selecciona un dispositivo.");
            return;
        }

        try {
            _session.Connect(device.Identifier);
            AppendLog($"USB: {device.DisplayName}");
            if (_session.Handler.GetLinkDescription() is { } link)
                AppendLog($"Enlace Odin: {link}");
            UpdateConnectionUi();
        } catch (Exception ex) {
            foreach (var line in ex.Message.Split('\n', '\r'))
                if (!string.IsNullOrWhiteSpace(line))
                    AppendLog(line.Trim());
            Log.Debug(ex, "Connect");
        }
    }

    private async void BeginOdinButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null) return;
        await RunBusyAsync("Esperando LOKE (handshake Odin)…", async ct => {
            try {
                await Task.Run(() => _session.BeginOdin(), ct);
            } catch (Exception ex) {
                var hint = ex.Message.Contains("Handshake", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("LOKE", StringComparison.OrdinalIgnoreCase)
                    ? " Reinicia modo Download, Conectar e Iniciar Odin (una vez). Revisa Zadig interfaz CDC 0x0A."
                    : "";
                throw new InvalidOperationException(ex.Message + hint, ex);
            }

            var v = _session.BootloaderVersion!.Value;
            await Dispatcher.InvokeAsync(() => {
                OdinInfoText.Text =
                    $"Bootloader Odin v{v.Version} · Unknown1={v.Unknown1} · Unknown2={v.Unknown2}";
                if (_session.Handler.GetLinkDescription() is { } link)
                    AppendLog($"Sesión Odin activa (BL v{v.Version}) — {link}");
                else
                    AppendLog($"Sesión Odin activa (BL v{v.Version})");
                SyncOdinOptionsFromUi();
                UpdateConnectionUi();
            });
            _ = PreloadDevicePitAsync();
        });
    }

    private void EndOdinButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null) return;
        try {
            _session.EndOdinSession(tryShutdown: false);
            OdinInfoText.Text = "";
            AppendLog("Sesión Odin finalizada.");
            UpdateConnectionUi();
        } catch (Exception ex) {
            AppendLog($"Fin sesión: {ex.Message}");
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null) return;
        try {
            _session.DisconnectUsb();
            OdinInfoText.Text = "";
            _partitionItems.Clear();
            AppendLog("Desconectado. Reinicia el teléfono en Download para reconectar.");
            UpdateConnectionUi();
            RefreshDevices();
        } catch (Exception ex) {
            AppendLog($"Desconectar: {ex.Message}");
        }
    }

    private void OdinOptionChanged(object sender, RoutedEventArgs e) {
        _session?.SetFlashOptions(EfsClearCheck.IsChecked == true);
    }

    private void SyncOdinOptionsFromUi() {
        _session?.SetFlashOptions(EfsClearCheck.IsChecked == true);
    }

    private void BrowseTarFolder_Click(object sender, RoutedEventArgs e) {
        var dlg = new OpenFolderDialog { Title = "Carpeta con firmware Odin (.tar / .tar.md5)" };
        if (dlg.ShowDialog() == true)
            TarFolderBox.Text = dlg.FolderName;
    }

    private void BrowseTarFile_Click(object sender, RoutedEventArgs e) {
        var dlg = new OpenFileDialog {
            Title = "Paquete Odin",
            Filter = "Odin tar|*.tar;*.tar.md5|Todos|*.*"
        };
        if (dlg.ShowDialog() == true)
            TarFolderBox.Text = dlg.FileName;
    }

    private IProgress<PitDumpProgress> CreatePitProgress() =>
        new Progress<PitDumpProgress>(p => {
            Dispatcher.InvokeAsync(() => {
                if (p.TotalBlocks > 0)
                    FlashProgress.Value = Math.Min(100, p.BlockIndex * 100.0 / p.TotalBlocks);
                FlashStatusText.Text = p.Message;
            });
            if (!string.IsNullOrWhiteSpace(p.Message))
                AppendLog(p.Message);
        });

    private async Task PreloadDevicePitAsync() {
        if (_session == null || !_session.IsOdinActive) return;
        try {
            var progress = CreatePitProgress();
            var pit = await Task.Run(() => _session.GetOrLoadDevicePit(progress));
            await Dispatcher.InvokeAsync(() => {
                BindPitEntries(pit.Entries);
                if (_session?.BootloaderVersion is { } v) {
                    OdinInfoText.Text =
                        $"Bootloader Odin v{v.Version} · chip/proyecto {pit.Project} · {pit.Entries.Count} particiones";
                }
            });
            AppendLog($"PIT listo: {pit.Entries.Count} particiones (proyecto {pit.Project}).");
        } catch (Exception ex) {
            _session.InvalidatePitCache();
            AppendLog($"PIT en segundo plano: {ex.Message}");
            if (IsUsbSessionDead(ex))
                AppendLog("Sesión USB inestable tras PIT. Fin sesión → Desconectar → reinicia Download.");
        }
    }

    private static bool IsUsbSessionDead(Exception ex) {
        var msg = ex.ToString();
        return msg.Contains("Bulk write failed: Io", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Bulk read failed: Io", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Bulk write failed: Pipe", StringComparison.OrdinalIgnoreCase);
    }

    private async void ScanTarButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        var source = TarFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(source)
            || (!File.Exists(source) && !Directory.Exists(source))) {
            AppendLog("Indica una carpeta o un archivo .tar / .tar.md5 válido.");
            return;
        }

        await RunBusyAsync("Escaneando firmware…", async ct => {
            var progress = CreatePitProgress();
            var pit = await Task.Run(() => _session.GetOrLoadDevicePit(progress), ct);
            var scan = await Task.Run(() => OdinOperations.ScanFirmwareSource(source, pit), ct);
            await Dispatcher.InvokeAsync(() => {
                _partitionItems.Clear();
                foreach (var item in scan.Matches)
                    _partitionItems.Add(item);
            });

            if (scan.Matches.Count == 0) {
                AppendLog("No se encontraron imágenes coincidentes con el PIT.");
                if (!string.IsNullOrEmpty(scan.Diagnostics))
                    foreach (var line in scan.Diagnostics.Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line))
                            AppendLog(line.TrimEnd());
            } else {
                AppendLog($"Encontradas {scan.Matches.Count} imagen(es) en {scan.TarPaths.Count} archivo(s) .tar.");
            }
        });
    }

    private void SelectAllPartitions_Click(object sender, RoutedEventArgs e) {
        foreach (var item in _partitionItems)
            item.Selected = true;
    }

    private void SelectNoPartitions_Click(object sender, RoutedEventArgs e) {
        foreach (var item in _partitionItems)
            item.Selected = false;
    }

    private async void FlashTarButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        var selected = _partitionItems.Where(x => x.Selected).ToList();
        if (selected.Count == 0) {
            AppendLog("Marca al menos una partición.");
            return;
        }

        var msg = $"¿Flashear {selected.Count} imagen(es)?\n\n" +
                  string.Join("\n", selected.Take(8).Select(x => $"• {x.DisplayLabel}")) +
                  (selected.Count > 8 ? $"\n… y {selected.Count - 8} más" : "");
        if (MessageBox.Show(msg, "Confirmar flash", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SyncOdinOptionsFromUi();
        string? lastFlashLog = null;
        var progress = new Progress<FlashProgressReport>(r => {
            UpdateFlashProgress(r);
            FlashStatusText.Text = r.Message;
            if (!string.IsNullOrEmpty(r.Message)
                && r.Message != lastFlashLog
                && (r.Message.StartsWith("Flasheando", StringComparison.Ordinal)
                    || r.Message.StartsWith("Escribiendo", StringComparison.Ordinal)
                    || r.Message.StartsWith("Enviando", StringComparison.Ordinal)
                    || r.Message.StartsWith("Iniciando flash", StringComparison.Ordinal))) {
                lastFlashLog = r.Message;
                AppendLog(r.Message);
            }
        });

        await RunBusyAsync("Flasheando…", async ct => {
            await Task.Run(() => OdinOperations.FlashSelected(
                _session.RequireOdin(), selected, progress, ct), ct);
            AppendLog("Flash de paquetes Odin completado.");
            await TryAutoRebootAfterFlashAsync(ct);
        });
    }

    private void BrowseSingleFile_Click(object sender, RoutedEventArgs e) {
        var dlg = new OpenFileDialog {
            Title = "Imagen a flashear",
            Filter = "Imágenes|*.img;*.bin;*.lz4;*.mbn;*.tar;*.md5|Todos|*.*"
        };
        if (dlg.ShowDialog() == true)
            SingleFileBox.Text = dlg.FileName;
    }

    private async void FlashFileButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        var path = SingleFileBox.Text.Trim();
        if (!File.Exists(path)) {
            AppendLog("Archivo no válido.");
            return;
        }

        PitEntry entry;
        if (PartitionCombo.SelectedItem is PitEntry chosen)
            entry = chosen;
        else {
            PitData pit;
            try {
                pit = _session.DevicePit ?? await Task.Run(() => _session.GetOrLoadDevicePit(CreatePitProgress()));
                await Dispatcher.InvokeAsync(() => {
                    PartitionCombo.ItemsSource = pit.Entries;
                    PartitionCombo.IsEnabled = true;
                });
            } catch (Exception ex) {
                AppendLog($"PIT: {ex.Message}");
                return;
            }

            var name = Path.GetFileName(path);
            var matched = OdinOperations.FindPitEntryByFileName(pit, name);
            entry = matched
                ?? pit.Entries.FirstOrDefault()
                ?? throw new InvalidOperationException("No hay entradas PIT.");
            if (matched == null)
                AppendLog($"Aviso: «{name}» no coincide con el PIT; usando partición {entry.Partition}.");
        }

        if (MessageBox.Show($"¿Flashear en partición {entry.Partition}?", "Confirmar",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SyncOdinOptionsFromUi();
        var progress = new Progress<FlashProgressReport>(r => {
            UpdateFlashProgress(r);
            FlashStatusText.Text = r.Message;
        });

        await RunBusyAsync("Flasheando archivo…", async ct => {
            await Task.Run(() => OdinOperations.FlashSingleFile(
                _session.RequireOdin(), path, entry, progress, ct), ct);
            AppendLog($"Archivo flasheado en {entry.Partition}.");
            await TryAutoRebootAfterFlashAsync(ct);
        });
    }

    private async void DumpPitButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        var dlg = new SaveFileDialog {
            Title = "Guardar PIT",
            Filter = "PIT|*.pit|Todos|*.*",
            FileName = "device.pit"
        };
        if (dlg.ShowDialog() != true) return;

        await RunBusyAsync("Volcando PIT…", async ct => {
            var buf = await Task.Run(() => _session.DumpDevicePit(), ct);
            await File.WriteAllBytesAsync(dlg.FileName, buf, ct);
            _session.InvalidatePitCache();
            AppendLog($"PIT guardado: {dlg.FileName}");
        });
    }

    private async void PrintPitDeviceButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        await RunBusyAsync("Leyendo PIT…", async ct => {
            var progress = CreatePitProgress();
            var pit = await Task.Run(() => _session.GetOrLoadDevicePit(progress), ct);
            var text = OdinOperations.FormatPit(pit);
            await Dispatcher.InvokeAsync(() => PitViewBox.Text = text);
            AppendLog($"PIT: {pit.Entries.Count} entradas, proyecto {pit.Project}");
            await Dispatcher.InvokeAsync(() => BindPitEntries(pit.Entries));
        });
    }

    private void BindPitEntries(IList<PitEntry> entries) {
        PartitionCombo.ItemsSource = entries;
        PartitionCombo.IsEnabled = !_busy && entries.Count > 0;
        ErasePartitionCombo.ItemsSource = entries;
        ErasePartitionCombo.IsEnabled = !_busy && entries.Count > 0;
        if (PartitionCombo.SelectedItem == null && entries.Count > 0)
            PartitionCombo.SelectedIndex = 0;
        if (ErasePartitionCombo.SelectedItem == null && entries.Count > 0)
            ErasePartitionCombo.SelectedIndex = 0;
    }

    private async void FlashPitButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        var dlg = new OpenFileDialog { Filter = "PIT|*.pit|Todos|*.*", Title = "Flashear PIT al dispositivo" };
        if (dlg.ShowDialog() != true) return;
        if (MessageBox.Show($"¿Flashear PIT desde {Path.GetFileName(dlg.FileName)}?",
                "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Flasheando PIT…", async ct => {
            await Task.Run(() => OdinOperations.FlashPitFile(_session.RequireOdin(), dlg.FileName), ct);
            _session.InvalidatePitCache();
            AppendLog("PIT flasheado en el dispositivo.");
        });
    }

    private async void SetRegionButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        var code = RegionCodeBox.Text.Trim().ToUpperInvariant();
        if (code.Length != 3) {
            AppendLog("Código de región: exactamente 3 letras.");
            return;
        }

        await RunBusyAsync("Aplicando región…", async ct => {
            await Task.Run(() => OdinOperations.SetRegionCode(_session.RequireOdin(), code), ct);
            AppendLog($"Código de región aplicado: {code}");
        });
    }

    private async void ErasePartitionButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        if (ErasePartitionCombo.SelectedItem is not PitEntry entry) {
            AppendLog("Selecciona una partición del PIT.");
            return;
        }

        long size;
        try {
            size = OdinOperations.GetPartitionSizeBytes(entry);
        } catch (Exception ex) {
            AppendLog(ex.Message);
            return;
        }

        if (MessageBox.Show($"¿Borrar permanentemente «{entry.Partition}» ({size:N0} bytes)?",
                "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var progress = new Progress<FlashProgressReport>(r => {
            UpdateFlashProgress(r);
            FlashStatusText.Text = r.Message;
        });

        await RunBusyAsync("Borrando partición…", async ct => {
            await Task.Run(() => OdinOperations.ErasePartition(
                _session.RequireOdin(), entry, progress, ct), ct);
            AppendLog($"Partición borrada: {entry.Partition}.");
        });
    }

    private void PrintPitFileButton_Click(object sender, RoutedEventArgs e) {
        var dlg = new OpenFileDialog { Filter = "PIT|*.pit|Todos|*.*" };
        if (dlg.ShowDialog() != true) return;
        try {
            var pit = new PitData(dlg.FileName);
            PitViewBox.Text = OdinOperations.FormatPit(pit);
            AppendLog($"PIT cargado desde archivo ({pit.Entries.Count} entradas).");
        } catch (Exception ex) {
            AppendLog($"PIT inválido: {ex.Message}");
        }
    }

    private async void FactoryResetButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        if (MessageBox.Show("¿Borrar partición userdata (factory reset)? Puede tardar varios minutos.",
                "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Borrando userdata…", async ct => {
            await Task.Run(() => _session.RequireOdin().EraseUserData(), ct);
            AppendLog("Userdata borrada.");
        });
    }

    private async void RebootButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        await RunBusyAsync("Reiniciando…", async ct => {
            await Task.Run(() => _session.RebootDevice(), ct);
            await Dispatcher.InvokeAsync(() => OdinInfoText.Text = "");
            AppendLog("Comando de reinicio enviado.");
        });
    }

    private async void RebootOdinButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        await RunBusyAsync("Reiniciando a Download…", async ct => {
            await Task.Run(() => _session.RebootToOdinDevice(), ct);
            await Dispatcher.InvokeAsync(() => OdinInfoText.Text = "");
            AppendLog("Reinicio a modo Odin enviado.");
        });
    }

    private async Task TryAutoRebootAfterFlashAsync(CancellationToken ct) {
        if (_session == null || AutoRebootCheck.IsChecked != true)
            return;

        try {
            AppendLog("Enviando reinicio automático…");
            await Task.Run(() => _session.RebootAfterFlash(), ct);
            await Dispatcher.InvokeAsync(() => OdinInfoText.Text = "");
            AppendLog("Reinicio automático enviado. Espera a que el teléfono arranque; no uses los botones físicos en Download.");
        } catch (Exception ex) {
            AppendLog($"Reinicio automático: {ex.Message}. Usa la pestaña «Reinicio» si el teléfono sigue en Download.");
        }
    }

    private void CancelFlashButton_Click(object sender, RoutedEventArgs e) {
        _operationCts?.Cancel();
        AppendLog("Cancelación solicitada…");
    }

    private async Task RunBusyAsync(string status, Func<CancellationToken, Task> work) {
        if (_busy) return;
        _busy = true;
        _operationCts = new CancellationTokenSource();
        SetUiEnabled(false);
        CancelFlashButton.IsEnabled = true;
        StatusText.Text = status;
        FlashProgress.Value = 0;
        FlashPartitionProgress.Value = 0;
        try {
            await work(_operationCts.Token);
            StatusText.Text = "Listo";
        } catch (OperationCanceledException) {
            AppendLog("Operación cancelada.");
            StatusText.Text = "Cancelado";
        } catch (Exception ex) {
            AppendLog($"Error: {ex.Message}");
            if (ex.Message.Contains("Auth", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("OEM", StringComparison.OrdinalIgnoreCase)) {
                AppendLog("Auth/OEM: incluye BL en el lote si flasheas bootloader (automático); OEM Unlock en opciones desarrollador; firmware del mismo modelo/región.");
            }
            if (IsUsbSessionDead(ex))
                AppendLog("Desconecta, reinicia modo Download y vuelve a conectar (sesión USB caducada).");
            Log.Debug(ex, "Operation");
            StatusText.Text = "Error";
        } finally {
            _operationCts?.Dispose();
            _operationCts = null;
            _busy = false;
            CancelFlashButton.IsEnabled = false;
            SetUiEnabled(true);
            UpdateConnectionUi();
            FlashProgress.Value = 0;
            FlashPartitionProgress.Value = 0;
        }
    }

    private void UpdateConnectionUi() {
        if (_session == null) return;
        var usb = _session.IsUsbConnected;
        var odin = _session.IsOdinActive;

        DeviceCombo.IsEnabled = !usb && !_busy;
        RefreshButton.IsEnabled = !_busy;
        DiagnoseButton.IsEnabled = !_busy;
        ConnectButton.IsEnabled = !usb && !_busy;
        BeginOdinButton.IsEnabled = usb && !odin && !_busy;
        EndOdinButton.IsEnabled = odin && !_busy;
        DisconnectButton.IsEnabled = usb && !_busy;

        FlashTab.IsEnabled = odin && !_busy;
        SingleFileTab.IsEnabled = odin && !_busy;
        PitTab.IsEnabled = odin && !_busy;
        AdvancedTab.IsEnabled = odin && !_busy;
        RebootTab.IsEnabled = odin && !_busy;
        ScanTarButton.IsEnabled = odin && !_busy;
        FlashTarButton.IsEnabled = odin && !_busy;
        FlashFileButton.IsEnabled = odin && !_busy;
        DumpPitButton.IsEnabled = odin && !_busy;
        PrintPitDeviceButton.IsEnabled = odin && !_busy;
        FactoryResetButton.IsEnabled = odin && !_busy;
        FlashPitButton.IsEnabled = odin && !_busy;
        SetRegionButton.IsEnabled = odin && !_busy;
        ErasePartitionButton.IsEnabled = odin && !_busy;

        if (odin && _session.DevicePit != null) {
            PartitionCombo.ItemsSource = _session.DevicePit.Entries;
            PartitionCombo.IsEnabled = !_busy;
        } else if (!odin) {
            PartitionCombo.ItemsSource = null;
            PartitionCombo.IsEnabled = false;
            ErasePartitionCombo.ItemsSource = null;
            ErasePartitionCombo.IsEnabled = false;
        }

        StatusText.Text = odin ? "Sesión Odin activa" : usb ? "USB conectado — inicia Odin" : "Desconectado";
    }

    private void SetUiEnabled(bool _) => UpdateConnectionUi();

    private void UpdateFlashProgress(FlashProgressReport r) {
        if (r.TotalBytes > 0)
            FlashProgress.Value = Math.Clamp(r.SentBytes * 100.0 / r.TotalBytes, 0, 100);
        if (r.PartitionTotalBytes > 0)
            FlashPartitionProgress.Value = Math.Clamp(
                r.PartitionSentBytes * 100.0 / r.PartitionTotalBytes, 0, 100);
        else
            FlashPartitionProgress.Value = 0;
    }

    private void AppendLog(string line) {
        if (!Dispatcher.CheckAccess()) {
            Dispatcher.Invoke(() => AppendLog(line));
            return;
        }
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private sealed class UiLogSink(Action<string> write) : Serilog.Core.ILogEventSink {
        public void Emit(Serilog.Events.LogEvent logEvent) {
            if (logEvent.Level < Serilog.Events.LogEventLevel.Information)
                return;
            write(logEvent.RenderMessage());
        }
    }
}
