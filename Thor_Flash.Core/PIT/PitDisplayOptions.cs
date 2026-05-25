namespace Protocol.Thor.Library.PIT;

/// <summary>Opciones de visualización del PIT para técnicos.</summary>
public sealed class PitDisplayOptions {
    /// <summary>Si false, solo entradas con nombre de archivo Odin.</summary>
    public bool ShowAllEntries { get; init; }

    /// <summary>BinaryType, DeviceType y campos numéricos extra.</summary>
    public bool ShowAdvancedDetails { get; init; }

    public static PitDisplayOptions TechnicianDefault { get; } = new();
}
