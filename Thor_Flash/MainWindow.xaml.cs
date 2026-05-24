using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using Protocol.Thor.Library;
using Protocol.Thor.Library.Communication;
using Protocol.Thor.Library.PIT;
using Thor_Flash.Services;

namespace Thor_Flash;

public partial class MainWindow : Window {
    private readonly OdinSession? _session;
    private readonly DispatcherTimer? _refreshTimer;
    private readonly SessionLog _sessionLog;
    private readonly ColoredLogWriter _coloredLog;
    private readonly ObservableCollection<FlashSlotGroup> _slotGroups = [];
    private CancellationTokenSource? _operationCts;
    private bool _busy;
    private bool _autoPipelineBusy;
    private bool _suppressAutoUntilRemoved;
    private bool _manualEndOdin;
    private bool _sessionHeaderWritten;
    private DateTime? _flashStartedUtc;
    private long _flashTotalBytes;

    private static string AppVersion {
        get {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.1";
        }
    }

    public MainWindow() {
        InitializeComponent();
        _coloredLog = new ColoredLogWriter(LogBox);
        _sessionLog = new SessionLog(_coloredLog);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .CreateLogger();

        PartitionsList.ItemsSource = _slotGroups;

        try {
            if (!OdinSession.IsPlatformSupported) {
                var err = USB.GetHandlerError();
                AppendLog(err != null ? $"ERROR USB: {err}" : "ERROR: Plataforma no soportada.");
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
            MessageBox.Show(ex.Message, "Thor_Flash", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        UpdateConnectionStatus();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) {
        if (_session == null || _refreshTimer == null)
            return;
        try {
            _sessionLog.WriteHeader($"Thor_Flash {AppVersion}", AppVersion);
            _sessionLog.WriteMessage("Waiting for device in Download mode…");
            _sessionHeaderWritten = true;
            await LoadUsbIdsAsync();
            RefreshDevices();
            _refreshTimer.Start();
            UpdateReadyIndicator();
            UpdateConnectionStatus();
        } catch (Exception ex) {
            AppendLog($"Carga inicial: {ex.Message}");
            Log.Debug(ex, "OnLoaded");
            MessageBox.Show(ex.Message, "Thor_Flash", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LoadUsbIdsAsync() {
        try {
            _ = await Lookup.Initialize();
        } catch (Exception ex) {
            Log.Debug(ex, "usb.ids");
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
        if (_session == null || _busy)
            return;
        try {
            if (!_session.IsUsbConnected) {
                var devices = _session.Handler.GetDevices();
                DeviceCombo.ItemsSource = devices;
                DeviceCombo.IsEnabled = devices.Count > 0;
                if (devices.Count > 0)
                    DeviceCombo.SelectedIndex = 0;
                if (devices.Count == 0) {
                    _suppressAutoUntilRemoved = false;
                    _manualEndOdin = false;
                }

                if (devices.Count > 0 && !_suppressAutoUntilRemoved && !_autoPipelineBusy)
                    _ = TryAutoPipelineAsync(devices[0]);
            } else if (!_session.IsOdinActive && !_manualEndOdin && !_autoPipelineBusy
                       && !_suppressAutoUntilRemoved
                       && DeviceCombo.SelectedItem is DeviceInfo dev) {
                _ = TryAutoPipelineAsync(dev);
            }

            UpdateReadyIndicator();
        } catch (Exception ex) {
            AppendLog($"Enumerar: {ex.Message}");
        }
    }

    private async Task TryAutoPipelineAsync(DeviceInfo device) {
        if (_session == null || _busy || _autoPipelineBusy || _suppressAutoUntilRemoved)
            return;
        if (_manualEndOdin && _session.IsUsbConnected)
            return;

        _autoPipelineBusy = true;
        await Dispatcher.InvokeAsync(UpdateReadyIndicator);

        try {
            if (!_session.IsUsbConnected) {
                EnsureSessionHeader();
                await Task.Run(() => _session.Connect(device.Identifier));
                _sessionLog.WriteStep("Checking Download Mode", "ODIN");
                await Dispatcher.InvokeAsync(UpdateConnectionUi);
            }

            if (!_session.IsOdinActive && !_manualEndOdin) {
                await Task.Run(() => _session.BeginOdin());
                var v = _session.BootloaderVersion!.Value;
                _sessionLog.WriteStep("Initializing Device", "Initialized");
                await Dispatcher.InvokeAsync(() => {
                    OdinInfoText.Text =
                        $"Bootloader Odin v{v.Version} · Unknown1={v.Unknown1} · Unknown2={v.Unknown2}";
                    SyncOdinOptionsFromUi();
                    UpdateConnectionUi();
                });
            }

            if (_session.IsOdinActive) {
                var pit = await Task.Run(() => _session.GetOrLoadDevicePit(CreatePitProgress(silent: true)));
                _sessionLog.WriteStep("Reading Pit from device", "Ok");
                await Dispatcher.InvokeAsync(() => {
                    LogDeviceInfo(pit);
                    BindPitEntries(pit.Entries);
                    if (_session.BootloaderVersion is { } bv) {
                        OdinInfoText.Text =
                            $"Bootloader Odin v{bv.Version} · chip/proyecto {pit.Project} · {pit.Entries.Count} particiones";
                    }
                    UpdateConnectionUi();
                });
            }
        } catch (Exception ex) {
            _sessionLog.WriteMessage(ex.Message, LogTone.Error);
            Log.Debug(ex, "AutoPipeline");
        } finally {
            _autoPipelineBusy = false;
            await Dispatcher.InvokeAsync(UpdateReadyIndicator);
        }
    }

    private void EnsureSessionHeader() {
        if (_sessionHeaderWritten)
            return;
        _sessionLog.WriteHeader($"Thor_Flash {AppVersion}", AppVersion);
        _sessionHeaderWritten = true;
    }

    private void UpdateReadyIndicator() {
        if (_session == null)
            return;

        Brush fill;
        string label;
        if (_autoPipelineBusy) {
            fill = (Brush)FindResource("TfReadyWaitingBrush");
            label = "Inicializando dispositivo…";
        } else if (_session.IsOdinActive && _session.DevicePit != null) {
            fill = (Brush)FindResource("TfReadyBrush");
            label = "Listo — selecciona firmware y flashea";
        } else if (_session.IsUsbConnected) {
            fill = (Brush)FindResource("TfReadyWaitingBrush");
            label = _manualEndOdin ? "Sesión Odin finalizada — pulsa «Iniciar Odin»" : "USB conectado — iniciando Odin…";
        } else if (DeviceCombo.Items.Count > 0) {
            fill = (Brush)FindResource("TfReadyWaitingBrush");
            label = "Dispositivo detectado — conectando…";
        } else {
            fill = (Brush)FindResource("TfReadyOffBrush");
            label = "Esperando dispositivo en Download…";
        }

        ReadyIndicator.Fill = fill;
        ReadyIndicator.Opacity = _session.IsOdinActive && _session.DevicePit != null ? 1.0 : 0.75;
        ReadyLabel.Text = label;
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null) return;
        if (DeviceCombo.SelectedItem is not DeviceInfo device) {
            _sessionLog.WriteMessage("Selecciona un dispositivo.", LogTone.Warning);
            return;
        }

        try {
            EnsureSessionHeader();
            _session.Connect(device.Identifier);
            _sessionLog.WriteStep("Checking Download Mode", "ODIN");
            UpdateConnectionUi();
        } catch (Exception ex) {
            _sessionLog.WriteMessage(ex.Message, LogTone.Error);
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
                _sessionLog.WriteStep("Initializing Device", "Initialized");
                OdinInfoText.Text =
                    $"Bootloader Odin v{v.Version} · Unknown1={v.Unknown1} · Unknown2={v.Unknown2}";
                SyncOdinOptionsFromUi();
                UpdateConnectionUi();
            });
            _manualEndOdin = false;
            _ = PreloadDevicePitAsync(useSessionLog: true);
        });
    }

    private void EndOdinButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null) return;
        try {
            _manualEndOdin = true;
            _session.EndOdinSession(tryShutdown: false);
            OdinInfoText.Text = "";
            AppendLog("Sesión Odin finalizada.");
            UpdateConnectionUi();
            UpdateReadyIndicator();
        } catch (Exception ex) {
            AppendLog($"Fin sesión: {ex.Message}");
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null) return;
        try {
            _suppressAutoUntilRemoved = true;
            _manualEndOdin = false;
            _session.DisconnectUsb();
            OdinInfoText.Text = "";
            _slotGroups.Clear();
            AppendLog("Desconectado. Reinicia el teléfono en Download para reconectar.");
            UpdateConnectionUi();
            UpdateReadyIndicator();
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

    private IProgress<PitDumpProgress> CreatePitProgress(bool silent = false) =>
        new Progress<PitDumpProgress>(p => {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => {
                if (p.TotalBlocks > 0)
                    FlashProgress.Value = Math.Min(100, p.BlockIndex * 100.0 / p.TotalBlocks);
            });
        });

    private async Task<PitData> EnsureDevicePitAsync(CancellationToken ct) {
        if (_session == null || !_session.IsOdinActive)
            throw new InvalidOperationException("Inicia sesión Odin primero.");
        if (_session.DevicePit != null)
            return _session.DevicePit;
        return await Task.Run(
            () => _session.GetOrLoadDevicePit(CreatePitProgress(silent: true)), ct);
    }

    private async Task PreloadDevicePitAsync(bool useSessionLog = false) {
        if (_session == null || !_session.IsOdinActive) return;
        try {
            var progress = CreatePitProgress(silent: true);
            var pit = await Task.Run(() => _session.GetOrLoadDevicePit(progress));
            await Dispatcher.InvokeAsync(() => {
                BindPitEntries(pit.Entries);
                if (_session?.BootloaderVersion is { } v) {
                    OdinInfoText.Text =
                        $"Bootloader Odin v{v.Version} · chip/proyecto {pit.Project} · {pit.Entries.Count} particiones";
                }
                UpdateReadyIndicator();
            });
            if (useSessionLog) {
                LogDeviceInfo(pit);
                _sessionLog.WriteStep("Reading Pit from device", "Ok");
            }
        } catch (Exception ex) {
            _session.InvalidatePitCache();
            if (useSessionLog) {
                _sessionLog.WriteStep("Reading Pit from device", "Failed");
                if (IsUsbSessionDead(ex))
                    _sessionLog.WriteMessage(
                        "Sesión USB inestable tras PIT. Fin sesión → Desconectar → reinicia Download.",
                        LogTone.Error);
            }
            Log.Debug(ex, "PreloadDevicePit");
        }
    }

    private static bool IsUsbSessionDead(Exception ex) {
        var msg = ex.ToString();
        return msg.Contains("Bulk write failed: Io", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Bulk read failed: Io", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Bulk write failed: Pipe", StringComparison.OrdinalIgnoreCase);
    }

    private async void ScanTarButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) {
            _sessionLog.WriteMessage("Sesión Odin no activa. Espera el indicador verde.", LogTone.Warning);
            return;
        }
        var source = TarFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(source)
            || (!File.Exists(source) && !Directory.Exists(source))) {
            _sessionLog.WriteMessage("Indica una carpeta o un archivo .tar / .tar.md5 válido.", LogTone.Warning);
            return;
        }

        await RunBusyAsync("Escaneando firmware…", async ct => {
            var pit = await EnsureDevicePitAsync(ct);
            var scan = await Task.Run(() => OdinOperations.ScanFirmwareSource(source, pit), ct);
            await Dispatcher.InvokeAsync(() => {
                _slotGroups.Clear();
                foreach (var group in FlashSlotGroup.CreateFromMatches(scan.Matches))
                    _slotGroups.Add(group);
            });

            if (scan.Matches.Count == 0)
                _sessionLog.WriteMessage("Please Select Firmware Package and try again", LogTone.Warning);
        });
    }

    private void SelectAllPartitions_Click(object sender, RoutedEventArgs e) {
        foreach (var group in _slotGroups)
            group.Selected = true;
    }

    private void SelectNoPartitions_Click(object sender, RoutedEventArgs e) {
        foreach (var group in _slotGroups)
            group.Selected = false;
    }

    private IEnumerable<FlashPartitionItem> AllPartitionItems =>
        _slotGroups.SelectMany(g => g.Partitions);

    private async void FlashTarButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        var selected = AllPartitionItems.Where(x => x.Selected).ToList();
        if (selected.Count == 0) {
            _sessionLog.WriteMessage("Marca al menos una partición.", LogTone.Warning);
            return;
        }

        var summary = string.Join("\n",
            _slotGroups
                .Select(g => (g, count: g.Partitions.Count(x => x.Selected)))
                .Where(x => x.count > 0)
                .Select(x => $"• {x.g.DisplayLabel}: {x.count} imagen(es)"));
        var msg = $"¿Flashear {selected.Count} imagen(es)?\n\n{summary}";
        if (MessageBox.Show(msg, "Confirmar flash", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SyncOdinOptionsFromUi();
        _sessionLog.WriteBlank();
        _sessionLog.WriteCalculatedSize(OdinOperations.SumSelectedBytes(selected));
        var progress = new Progress<FlashProgressReport>(r => {
            UpdateFlashProgress(r);
            SetFlashActivity(r.Message);
            if (!string.IsNullOrEmpty(r.CompletedFlashFile))
                _sessionLog.WriteFlashOk(r.CompletedFlashFile);
        });

        await RunBusyAsync("Flasheando…", async ct => {
            await Task.Run(() => OdinOperations.FlashSelected(
                _session.RequireOdin(), selected, progress, ct), ct);
            await TryAutoRebootAfterFlashAsync(ct);
            WriteFlashCompletedSummary();
        }, trackFlashActivity: true, flashTotalBytes: OdinOperations.SumSelectedBytes(selected));
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
            SetFlashActivity(r.Message);
            if (!string.IsNullOrEmpty(r.CompletedFlashFile))
                _sessionLog.WriteFlashOk(r.CompletedFlashFile);
        });

        await RunBusyAsync("Flasheando archivo…", async ct => {
            await Task.Run(() => OdinOperations.FlashSingleFile(
                _session.RequireOdin(), path, entry, progress, ct), ct);
            await TryAutoRebootAfterFlashAsync(ct);
            WriteFlashCompletedSummary();
        }, trackFlashActivity: true, flashTotalBytes: new FileInfo(path).Length);
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
            await Task.Run(() => _session.RebootAfterFlash(), ct);
            _sessionLog.WriteStep("Rebooting Device To Normal Mode", "Ok");
            await Dispatcher.InvokeAsync(() => {
                OdinInfoText.Text = "";
                _suppressAutoUntilRemoved = true;
            });
            try {
                _session.DisconnectUsb();
            } catch {
                /* enlace USB caducado tras reinicio — esperado */
            }
            await Dispatcher.InvokeAsync(UpdateConnectionUi);
        } catch (Exception ex) {
            _sessionLog.WriteStep("Rebooting Device To Normal Mode", "Failed");
            _sessionLog.WriteMessage(
                $"Reinicio automático: {ex.Message}. Usa la pestaña «Reinicio» si el teléfono sigue en Download.",
                LogTone.Warning);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) {
        _operationCts?.Cancel();
        _sessionLog.WriteMessage("Cancelación solicitada…", LogTone.Warning);
    }

    private void WebsiteLink_RequestNavigate(object sender, RequestNavigateEventArgs e) {
        Process.Start(new ProcessStartInfo {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true
        });
        e.Handled = true;
    }

    private async Task RunBusyAsync(
        string status,
        Func<CancellationToken, Task> work,
        bool trackFlashActivity = false,
        long flashTotalBytes = 0) {
        if (_busy) return;
        _busy = true;
        _operationCts = new CancellationTokenSource();
        SetUiEnabled(false);
        StopButton.IsEnabled = true;
        if (trackFlashActivity) {
            BeginFlashMetrics(flashTotalBytes);
            SetFlashActivity(status);
        }
        FlashProgress.Value = 0;
        FlashPartitionProgress.Value = 0;
        try {
            await work(_operationCts.Token);
        } catch (OperationCanceledException) {
            _sessionLog.WriteMessage("Operación cancelada.", LogTone.Warning);
        } catch (Exception ex) {
            _sessionLog.WriteMessage(ex.Message, LogTone.Error);
            if (ex.Message.Contains("Auth", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("OEM", StringComparison.OrdinalIgnoreCase)) {
                _sessionLog.WriteMessage(
                    "Auth/OEM: incluye BL en el lote si flasheas bootloader; OEM Unlock en opciones desarrollador; firmware del mismo modelo/región.",
                    LogTone.Warning);
            }
            if (IsUsbSessionDead(ex))
                _sessionLog.WriteMessage(
                    "Desconecta, reinicia modo Download y vuelve a conectar (sesión USB caducada).",
                    LogTone.Error);
            Log.Debug(ex, "Operation");
        } finally {
            if (trackFlashActivity) {
                SetFlashActivity(null);
                _flashStartedUtc = null;
            }
            _operationCts?.Dispose();
            _operationCts = null;
            _busy = false;
            StopButton.IsEnabled = false;
            SetUiEnabled(true);
            UpdateConnectionUi();
            FlashProgress.Value = 0;
            FlashPartitionProgress.Value = 0;
        }
    }

    private void UpdateConnectionUi() {
        if (_session == null) {
            UpdateConnectionStatus();
            return;
        }
        var usb = _session.IsUsbConnected;
        var odin = _session.IsOdinActive;
        var pitReady = _session.DevicePit != null;

        DeviceCombo.IsEnabled = !usb && !_busy;
        RefreshButton.IsEnabled = !_busy;
        DiagnoseButton.IsEnabled = !_busy;
        ConnectButton.IsEnabled = !usb && !_busy;
        BeginOdinButton.IsEnabled = usb && !odin && !_busy;
        EndOdinButton.IsEnabled = odin && !_busy;
        DisconnectButton.IsEnabled = usb && !_busy;

        FlashTab.IsEnabled = odin;
        SingleFileTab.IsEnabled = odin;
        PitTab.IsEnabled = odin;
        AdvancedTab.IsEnabled = odin;
        RebootTab.IsEnabled = odin;
        ScanTarButton.IsEnabled = odin && pitReady && !_busy;
        FlashTarButton.IsEnabled = odin && pitReady && !_busy && AllPartitionItems.Any();
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

        UpdateConnectionStatus();
        UpdateReadyIndicator();
    }

    private void UpdateConnectionStatus() {
        if (_session == null || (!_session.IsUsbConnected && !_session.IsOdinActive)) {
            StatusText.Text = "Desconectado";
            StatusText.Foreground = (Brush)FindResource("TfStatusDisconnectedBrush");
            return;
        }

        if (_session.IsOdinActive && _session.DevicePit != null) {
            StatusText.Text = "Ready";
            StatusText.Foreground = (Brush)FindResource("TfStatusConnectedBrush");
            return;
        }

        StatusText.Text = "Conectado";
        StatusText.Foreground = (Brush)FindResource("TfReadyWaitingBrush");
    }

    private void SetFlashActivity(string? text) {
        FlashStatusText.Text = text ?? "";
        FlashStatusText.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SetUiEnabled(bool _) => UpdateConnectionUi();

    private void UpdateFlashProgress(FlashProgressReport r) {
        if (r.TotalBytes > 0)
            _flashTotalBytes = r.TotalBytes;

        if (r.TotalBytes > 0)
            FlashProgress.Value = Math.Clamp(r.SentBytes * 100.0 / r.TotalBytes, 0, 100);
        if (r.PartitionTotalBytes > 0)
            FlashPartitionProgress.Value = Math.Clamp(
                r.PartitionSentBytes * 100.0 / r.PartitionTotalBytes, 0, 100);
        else
            FlashPartitionProgress.Value = 0;

        if (_flashStartedUtc != null)
            UpdateFlashStats(r.SentBytes, _flashTotalBytes > 0 ? _flashTotalBytes : r.TotalBytes);
    }

    private void BeginFlashMetrics(long totalBytes) {
        _flashStartedUtc = DateTime.UtcNow;
        _flashTotalBytes = totalBytes;
        FlashStatsPanel.Visibility = Visibility.Visible;
        UpdateFlashStats(0, totalBytes);
    }

    private void WriteFlashCompletedSummary() {
        if (_flashStartedUtc == null) {
            _sessionLog.WriteSuccess();
            return;
        }

        var elapsed = DateTime.UtcNow - _flashStartedUtc.Value;
        if (_flashTotalBytes > 0)
            UpdateFlashStats(_flashTotalBytes, _flashTotalBytes);
        _sessionLog.WriteFlashCompleted(elapsed);
        _sessionLog.WriteSuccess();
        _flashStartedUtc = null;
    }

    private void UpdateFlashStats(long writtenBytes, long totalBytes) {
        TotalSizeText.Text = $"Total Size : {FormatFlashSize(totalBytes)}";
        WrittenSizeText.Text = $"Written Size : {FormatFlashSize(writtenBytes)}";

        if (_flashStartedUtc == null) {
            TransferRateText.Text = "Transfer Rate : 0 KB/s";
            return;
        }

        var elapsedSeconds = (DateTime.UtcNow - _flashStartedUtc.Value).TotalSeconds;
        var rate = elapsedSeconds > 0.3 ? writtenBytes / elapsedSeconds : 0;
        TransferRateText.Text = $"Transfer Rate : {FormatTransferRate(rate)}";
    }

    private static string FormatFlashSize(long bytes) {
        if (bytes <= 0)
            return "0 MB";

        var mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1.0)
            return mb.ToString("N0", CultureInfo.CurrentCulture) + " MB";

        var kb = bytes / 1024.0;
        return kb.ToString("N3", CultureInfo.CurrentCulture) + " KB";
    }

    private static string FormatTransferRate(double bytesPerSecond) {
        var kb = bytesPerSecond / 1024.0;
        return kb.ToString("N3", CultureInfo.CurrentCulture) + " KB/s";
    }

    private void AppendLog(string line) =>
        _coloredLog.WriteLine(line, LogTone.Diagnostic);

    private void LogDeviceInfo(PitData? pit = null) {
        if (_session == null)
            return;

        var id = DeviceDvif.TryRead(_session.Handler);
        if (id.Model != null || id.UniqueId != null || id.ProductId != null)
            _sessionLog.WriteDeviceIdentity(id);
        else if (pit != null)
            _sessionLog.WriteDeviceFromPit(pit);
    }
}
