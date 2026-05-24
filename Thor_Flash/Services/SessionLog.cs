using System.Globalization;
using Protocol.Thor.Library;
using Protocol.Thor.Library.PIT;

namespace Thor_Flash.Services;

/// <summary>Log de sesión estilo Odin Flash (sin marca de tiempo).</summary>
public sealed class SessionLog(ColoredLogWriter log) {
    public void WriteHeader(string title, string version) {
        log.WriteLine(title);
        log.WriteLine(new string('-', 36));
    }

    public void WriteBlank() => log.WriteLine("");

    public void WriteMessage(string text, LogTone tone = LogTone.Session) => log.WriteLine(text, tone);

    public void WriteLabelValue(string label, string? value) {
        log.WriteLine($"{label} : ");
        log.WriteLine(string.IsNullOrWhiteSpace(value) ? "—" : value);
    }

    public void WriteStep(string step, string result) {
        log.WriteLine($"{step} : ");
        log.WriteLine(string.IsNullOrWhiteSpace(result) ? "—" : result,
            result is "Failed" or "Fail" ? LogTone.Error : LogTone.Session);
    }

    public void WriteFlashOk(string fileName) {
        log.WriteLine($"Flashing {fileName}: ");
        log.WriteLine(" : Ok");
    }

    public void WriteCalculatedSize(long bytes) {
        var mb = bytes / (1024.0 * 1024.0);
        WriteLabelValue("Calculated Size", mb.ToString("N3", CultureInfo.CurrentCulture) + " MB");
    }

    public void WriteDeviceIdentity(DeviceDvif.DeviceIdentity id) {
        if (id.Model != null) WriteLabelValue("Model Number", id.Model);
        if (id.UniqueId != null) WriteLabelValue("Unique Id", id.UniqueId);
        if (id.CapaGb != null) WriteLabelValue("Capa Number", id.CapaGb.Value.ToString(CultureInfo.InvariantCulture));
        if (id.Vendor != null) WriteLabelValue("vendor", id.Vendor);
        if (id.FirmwareVersion != null) WriteLabelValue("Firmware Version", id.FirmwareVersion);
        if (id.ProductId != null) WriteLabelValue("Product Id", id.ProductId);
        if (id.Provision != null) WriteLabelValue("Provision", id.Provision);
        if (id.SalesCode != null) WriteLabelValue("Sales Code", id.SalesCode);
        if (id.BuildNumber != null) WriteLabelValue("Build Number", id.BuildNumber);
        if (id.DidNumber != null) WriteLabelValue("did Number", id.DidNumber);
        if (id.TmuNumber != null) WriteLabelValue("Tmu Number", id.TmuNumber);
    }

    /// <summary>Datos básicos desde PIT cuando DVIF no está disponible (WinUSB).</summary>
    public void WriteDeviceFromPit(PitData pit) {
        if (!string.IsNullOrWhiteSpace(pit.Project))
            WriteLabelValue("Model Number", pit.Project);
    }

    public void WriteSuccess(string message = " Good Job Boy 👍") =>
        log.WriteLine(message, LogTone.Success);
}
