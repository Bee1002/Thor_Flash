using LibUsbDotNet;

namespace Protocol.Thor.Library.Platform;

internal sealed record OdinUsbInterface(
    int Number,
    int Alternate,
    byte ReadEp,
    byte WriteEp,
    ClassCode Class) {
    public string Describe() =>
        $"interfaz #{Number} alt {Alternate} clase 0x{(byte)Class:X2} IN 0x{ReadEp:X2} OUT 0x{WriteEp:X2}";
}
