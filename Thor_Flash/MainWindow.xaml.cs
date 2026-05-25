using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private bool _scanFirmwareWhenReady;
    private bool _loggedUsbReconnectHint;
    private DateTime? _flashStartedUtc;
    private long _flashTotalBytes;
    private PitData? _pitViewData;
    private bool _inFlashPhase;

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

        if (DesignerProperties.GetIsInDesignMode(this)) {
            UpdateConnectionStatus();
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .CreateLogger();

        PartitionsList.ItemsSource = _slotGroups;

        try {
            if (!OdinSession.IsPlatformSupported) {
                var err = USB.GetHandlerError();
                AppendLog(err != null ? $"ERROR USB: {err}" : "ERROR: Unsupported platform.");
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
            AppendLog($"Startup failed: {ex.Message}");
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
            MainTabs.SelectedIndex = 0;
            UpdateConnectionStatus();
        } catch (Exception ex) {
            AppendLog($"Initial load: {ex.Message}");
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

    private DeviceInfo? GetSelectedOrFirstDevice() {
        if (DeviceCombo.SelectedItem is DeviceInfo selected)
            return selected;
        if (DeviceCombo.Items.Count > 0 && DeviceCombo.Items[0] is DeviceInfo first)
            return first;
        return null;
    }

    private void UpdateDevicePickerVisibility() {
        DevicePickerPanel.Visibility = DeviceCombo.Items.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DiagnoseButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null) return;
        try {
            var id = GetSelectedOrFirstDevice()?.Identifier;
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
                UpdateDevicePickerVisibility();
                if (devices.Count == 0) {
                    _suppressAutoUntilRemoved = false;
                    _manualEndOdin = false;
                }

                if (devices.Count > 0 && !_suppressAutoUntilRemoved && !_autoPipelineBusy)
                    _ = TryAutoPipelineAsync(devices[0]);
            } else if (!_session.IsOdinActive && !_manualEndOdin && !_autoPipelineBusy
                       && !_suppressAutoUntilRemoved
                       && GetSelectedOrFirstDevice() is DeviceInfo dev) {
                _ = TryAutoPipelineAsync(dev);
            }

            UpdateConnectionUi();
        } catch (Exception ex) {
            AppendLog($"Enumerate: {ex.Message}");
        }
    }

    private async Task TryAutoPipelineAsync(DeviceInfo device) {
        if (_session == null || _busy || _autoPipelineBusy || _suppressAutoUntilRemoved)
            return;
        if (_manualEndOdin && _session.IsUsbConnected)
            return;

        _autoPipelineBusy = true;
        await Dispatcher.InvokeAsync(UpdateConnectionUi);

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
                    if (_session.BootloaderVersion is { } bv) {
                        OdinInfoText.Text =
                            $"Bootloader Odin v{bv.Version} · chip/project {pit.Project} · {pit.Entries.Count} partitions";
                    }
                    UpdateConnectionUi();
                });
                _loggedUsbReconnectHint = false;
            }
        } catch (Exception ex) {
            if (IsUsbSessionDead(ex))
                HandleDeadUsbSession();
            else
                WriteSessionError(ex);
            Log.Debug(ex, "AutoPipeline");
        } finally {
            _autoPipelineBusy = false;
            await Dispatcher.InvokeAsync(UpdateConnectionUi);
        }
    }

    private void EnsureSessionHeader() {
        if (_sessionHeaderWritten)
            return;
        _sessionLog.WriteHeader($"Thor_Flash {AppVersion}", AppVersion);
        _sessionHeaderWritten = true;
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null) return;
        if (DeviceCombo.SelectedItem is not DeviceInfo device) {
            _sessionLog.WriteMessage("Select a device.", LogTone.Message);
            return;
        }

        try {
            EnsureSessionHeader();
            _session.Connect(device.Identifier);
            _sessionLog.WriteStep("Checking Download Mode", "ODIN");
            UpdateConnectionUi();
        } catch (Exception ex) {
            WriteSessionError(ex);
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
                    ? " Reboot to Download mode, use Connect and Begin Odin (once). Check Zadig CDC interface 0x0A."
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
            AppendLog("Odin session ended.");
            UpdateConnectionUi();
        } catch (Exception ex) {
            AppendLog($"End session: {ex.Message}");
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
            AppendLog("Disconnected. Reboot the phone to Download mode to reconnect.");
            UpdateConnectionUi();
            RefreshDevices();
        } catch (Exception ex) {
            AppendLog($"Disconnect: {ex.Message}");
        }
    }

    private void OdinOptionChanged(object sender, RoutedEventArgs e) {
        _session?.SetFlashOptions(EfsClearCheck.IsChecked == true);
    }

    private void SyncOdinOptionsFromUi() {
        _session?.SetFlashOptions(EfsClearCheck.IsChecked == true);
    }

    private async void BrowseTarFolder_Click(object sender, RoutedEventArgs e) {
        var dlg = new OpenFolderDialog { Title = "Carpeta con firmware Odin (.tar / .tar.md5)" };
        if (dlg.ShowDialog() != true)
            return;

        TarFolderBox.Text = dlg.FolderName;
        await TryScanFirmwareAsync(deferIfNotReady: true);
    }

    private async void BrowseTarFile_Click(object sender, RoutedEventArgs e) {
        var dlg = new OpenFileDialog {
            Title = "Paquete Odin",
            Filter = "Odin tar|*.tar;*.tar.md5|Todos|*.*"
        };
        if (dlg.ShowDialog() != true)
            return;

        TarFolderBox.Text = dlg.FileName;
        await TryScanFirmwareAsync(deferIfNotReady: true);
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
            throw new InvalidOperationException("Start Odin session first.");
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
                if (_session?.BootloaderVersion is { } v) {
                    OdinInfoText.Text =
                        $"Bootloader Odin v{v.Version} · chip/project {pit.Project} · {pit.Entries.Count} partitions";
                }
                UpdateConnectionUi();
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
                    HandleDeadUsbSession();
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

    private void WriteSessionError(Exception ex) {
        if (IsUsbSessionDead(ex)) {
            HandleDeadUsbSession();
            return;
        }

        _sessionLog.WriteMessage(ex.Message, LogTone.Error);
    }

    /// <summary>Cierra el enlace USB muerto y muestra el aviso una sola vez hasta reconectar bien.</summary>
    private void HandleDeadUsbSession() {
        _suppressAutoUntilRemoved = true;
        if (!_loggedUsbReconnectHint) {
            _loggedUsbReconnectHint = true;
            _sessionLog.WriteMessage(SessionLog.UsbReconnectMessage, LogTone.Error);
        }

        FinalizeUsbLink(silent: true);
    }

    /// <summary>Tras reinicio del teléfono: soltar USB sin mensaje de error (comportamiento esperado).</summary>
    private void FinalizeUsbAfterReboot() {
        _loggedUsbReconnectHint = false;
        FinalizeUsbLink(silent: true);
    }

    private void FinalizeUsbLink(bool silent) {
        if (_session == null)
            return;

        try {
            if (_session.IsUsbConnected || _session.IsOdinActive)
                _session.DisconnectUsb();
        } catch (Exception ex) {
            if (!silent)
                Log.Debug(ex, "FinalizeUsbLink");
        }

        Dispatcher.BeginInvoke(() => {
            OdinInfoText.Text = "";
            UpdateConnectionUi();
        });
    }

    private async void ScanTarButton_Click(object sender, RoutedEventArgs e) =>
        await TryScanFirmwareAsync(deferIfNotReady: false);

    private string? GetFirmwareSourcePath() {
        var source = TarFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(source))
            return null;
        if (File.Exists(source) || Directory.Exists(source))
            return source;
        return null;
    }

    private bool CanScanFirmwareNow() =>
        _session != null && _session.IsOdinActive && _session.DevicePit != null && !_busy;

    private async Task TryScanFirmwareAsync(bool deferIfNotReady = false) {
        var source = GetFirmwareSourcePath();
        if (source == null) {
            _sessionLog.WriteMessage("Select a folder or a valid .tar / .tar.md5 file.", LogTone.Message);
            return;
        }

        if (!CanScanFirmwareNow()) {
            if (deferIfNotReady)
                _scanFirmwareWhenReady = true;

            if (_session == null || !_session.IsOdinActive) {
                _sessionLog.WriteMessage(deferIfNotReady
                    ? "Odin session not active. Firmware will be scanned when Ready."
                    : "Odin session not active. Wait for Ready.", LogTone.Message);
            } else {
                _sessionLog.WriteMessage(deferIfNotReady
                    ? "Waiting for PIT… Firmware will be scanned when Ready."
                    : "Wait until PIT is ready (Ready).", LogTone.Message);
            }
            return;
        }

        _scanFirmwareWhenReady = false;
        await ScanFirmwareAsync(source);
    }

    private async Task ScanFirmwareAsync(string source) {
        await RunBusyAsync("Scanning firmware…", async ct => {
            var pit = await EnsureDevicePitAsync(ct);
            var scan = await Task.Run(() => OdinOperations.ScanFirmwareSource(source, pit), ct);
            await Dispatcher.InvokeAsync(() => {
                _slotGroups.Clear();
                foreach (var group in FlashSlotGroup.CreateFromMatches(scan.Matches))
                    _slotGroups.Add(group);
            });

            if (scan.Matches.Count == 0)
                _sessionLog.WriteMessage("Please Select Firmware Package and try again", LogTone.Message);
        });
    }

    private async Task TryDeferredFirmwareScanAsync() {
        if (!_scanFirmwareWhenReady || !CanScanFirmwareNow())
            return;

        var source = GetFirmwareSourcePath();
        if (source == null) {
            _scanFirmwareWhenReady = false;
            return;
        }

        _scanFirmwareWhenReady = false;
        await ScanFirmwareAsync(source);
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
            _sessionLog.WriteMessage("Select at least one partition.", LogTone.Message);
            return;
        }

        var summary = string.Join("\n",
            _slotGroups
                .Select(g => (g, count: g.Partitions.Count(x => x.Selected)))
                .Where(x => x.count > 0)
                .Select(x => $"• {x.g.DisplayLabel}: {x.count} image(s)"));
        var msg = $"Flash {selected.Count} image(s)?\n\n{summary}";
        if (MessageBox.Show(msg, "Confirm flash", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SyncOdinOptionsFromUi();
        ShowFlashPhase();
        _sessionLog.WriteBlank();
        _sessionLog.WriteCalculatedSize(OdinOperations.SumSelectedBytes(selected));
        var progress = new Progress<FlashProgressReport>(r => {
            UpdateFlashProgress(r);
            SetFlashActivity(r.Message);
            if (!string.IsNullOrEmpty(r.CompletedFlashFile))
                _sessionLog.WriteFlashOk(r.CompletedFlashFile);
        });

        await RunBusyAsync("Flashing…", async ct => {
            await Task.Run(() => OdinOperations.FlashSelected(
                _session.RequireOdin(), selected, progress, ct), ct);
            await TryAutoRebootAfterFlashAsync(ct);
            WriteFlashCompletedSummary();
        }, trackFlashActivity: true, flashTotalBytes: OdinOperations.SumSelectedBytes(selected));
    }

    private void NewFlashButton_Click(object sender, RoutedEventArgs e) {
        if (_busy)
            return;
        ShowSetupPhase();
    }

    private void ShowSetupPhase() {
        _inFlashPhase = false;
        MainTabs.Visibility = Visibility.Visible;
        FlashPhasePanel.Visibility = Visibility.Collapsed;
        MainTabs.SelectedIndex = 0;
        _refreshTimer?.Start();
        UpdateConnectionUi();
    }

    private void ShowFlashPhase() {
        _inFlashPhase = true;
        _refreshTimer?.Stop();
        MainTabs.Visibility = Visibility.Collapsed;
        FlashPhasePanel.Visibility = Visibility.Visible;
        LogBox.ScrollToEnd();
        UpdateConnectionUi();
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
            await Dispatcher.InvokeAsync(() => {
                _pitViewData = pit;
                RefreshPitView();
            });
            AppendLog($"PIT: {pit.Entries.Count} entradas, proyecto {pit.Project}");
        });
    }

    private PitDisplayOptions GetPitDisplayOptions() => new() {
        ShowAllEntries = PitShowAllCheck.IsChecked == true,
        ShowAdvancedDetails = PitShowAdvancedCheck.IsChecked == true,
    };

    private void RefreshPitView() {
        if (_pitViewData == null) {
            PitViewBox.Text = "";
            return;
        }

        PitViewBox.Text = OdinOperations.FormatPit(_pitViewData, GetPitDisplayOptions());
    }

    private void PitViewOption_Changed(object sender, RoutedEventArgs e) => RefreshPitView();

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

    private void PrintPitFileButton_Click(object sender, RoutedEventArgs e) {
        var dlg = new OpenFileDialog { Filter = "PIT|*.pit|Todos|*.*" };
        if (dlg.ShowDialog() != true) return;
        try {
            _pitViewData = new PitData(dlg.FileName);
            RefreshPitView();
            AppendLog($"PIT cargado desde archivo ({_pitViewData.Entries.Count} entradas).");
        } catch (Exception ex) {
            AppendLog($"PIT inválido: {ex.Message}");
        }
    }

    private async void RebootButton_Click(object sender, RoutedEventArgs e) {
        if (_session == null || !_session.IsOdinActive) return;
        await RunBusyAsync("Reiniciando…", async ct => {
            await RunDeviceRebootAsync(() => _session.RebootDevice(), ct);
            await Dispatcher.InvokeAsync(() =>
                _sessionLog.WriteStep("Rebooting Device To Normal Mode", "Ok"));
            FinalizeUsbAfterReboot();
        });
    }

    private async Task RunDeviceRebootAsync(Action reboot, CancellationToken ct) {
        try {
            await Task.Run(reboot, ct);
        } catch (Exception ex) when (IsUsbSessionDead(ex)) {
            // Tras enviar el reinicio el enlace USB suele caer; no es un fallo del usuario.
            Log.Debug(ex, "Device reboot USB drop (expected)");
        }
    }

    private async Task TryAutoRebootAfterFlashAsync(CancellationToken ct) {
        if (_session == null || AutoRebootCheck.IsChecked != true)
            return;

        try {
            await RunDeviceRebootAsync(() => _session.RebootAfterFlash(), ct);
            _sessionLog.WriteStep("Rebooting Device To Normal Mode", "Ok");
            await Dispatcher.InvokeAsync(() => _suppressAutoUntilRemoved = true);
            FinalizeUsbAfterReboot();
            await Dispatcher.InvokeAsync(UpdateConnectionUi);
        } catch (Exception ex) {
            _sessionLog.WriteStep("Rebooting Device To Normal Mode", "Failed");
            if (IsUsbSessionDead(ex))
                HandleDeadUsbSession();
            else
                _sessionLog.WriteMessage(
                    $"Auto reboot: {ex.Message}. Use the Reboot tab if the phone stays in Download mode.",
                    LogTone.Message);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) {
        _operationCts?.Cancel();
        _sessionLog.WriteMessage("Cancel requested…", LogTone.Message);
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
            _sessionLog.WriteMessage("Operation canceled.", LogTone.Message);
        } catch (Exception ex) {
            WriteSessionError(ex);
            if (!IsUsbSessionDead(ex)
                && (ex.Message.Contains("Auth", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("OEM", StringComparison.OrdinalIgnoreCase))) {
                _sessionLog.WriteMessage(
                    "Auth/OEM: include BL in the batch when flashing bootloader; enable OEM Unlock in developer options; use firmware for the same model/region.",
                    LogTone.Message);
            }
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
            if (_inFlashPhase)
                LogBox.ScrollToEnd();
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
        var flashReady = odin && pitReady;

        DeviceCombo.IsEnabled = !usb && !_busy;
        RefreshButton.IsEnabled = !_busy;
        DiagnoseButton.IsEnabled = !_busy;
        DriverHelpButton.IsEnabled = !_busy;
        ConnectButton.IsEnabled = !usb && !_busy && DeviceCombo.Items.Count > 0;
        BeginOdinButton.IsEnabled = usb && !odin && !_busy;
        EndOdinButton.IsEnabled = odin && !_busy;
        DisconnectButton.IsEnabled = usb && !_busy;

        FlashOptionsPanel.IsEnabled = flashReady && !_busy && !_inFlashPhase;
        FlashPartitionsPanel.IsEnabled = flashReady && !_busy && !_inFlashPhase;
        ScanTarButton.IsEnabled = flashReady && !_busy && !_inFlashPhase;
        FlashTarButton.IsEnabled = flashReady && !_busy && !_inFlashPhase && AllPartitionItems.Any();
        DumpPitButton.IsEnabled = odin && !_busy && !_inFlashPhase;
        PrintPitDeviceButton.IsEnabled = odin && !_busy && !_inFlashPhase;
        FlashPitButton.IsEnabled = odin && !_busy && !_inFlashPhase;
        RebootDeviceButton.IsEnabled = odin && !_busy && !_inFlashPhase;

        NewFlashButton.Visibility = _inFlashPhase ? Visibility.Visible : Visibility.Collapsed;
        NewFlashButton.IsEnabled = _inFlashPhase && !_busy;
        ConnectionExpander.IsEnabled = !_inFlashPhase || !_busy;

        UpdateDevicePickerVisibility();

        UpdateConnectionStatus();
        if (_scanFirmwareWhenReady && odin && pitReady)
            _ = TryDeferredFirmwareScanAsync();
    }

    private void UpdateConnectionStatus() {
        if (_session == null || (!_session.IsUsbConnected && !_session.IsOdinActive)) {
            StatusText.Text = "Disconnected";
            StatusText.Foreground = (Brush)FindResource("TfStatusDisconnectedBrush");
            return;
        }

        if (_session.IsOdinActive && _session.DevicePit != null) {
            StatusText.Text = "Ready";
            StatusText.Foreground = (Brush)FindResource("TfStatusConnectedBrush");
            return;
        }

        StatusText.Text = "Connected";
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
