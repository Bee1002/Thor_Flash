using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Protocol.Thor.Library;

namespace Thor_Flash.Services;

/// <summary>Grupo colapsable BL / AP / CP / CSC / HOME para la lista de flash.</summary>
public sealed class FlashSlotGroup : INotifyPropertyChanged {
    private bool _isExpanded;
    private bool _selected;
    private bool _syncingSelection;

    public string Label { get; }
    public string TarPath { get; }
    public string DisplayLabel => Label switch {
        "HOME_CSC" => "HOME",
        _ => Label
    };
    public ObservableCollection<FlashPartitionItem> Partitions { get; }

    public bool IsExpanded {
        get => _isExpanded;
        set {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public bool Selected {
        get => _selected;
        set {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
            SetAllPartitions(value);
        }
    }

    public string SelectionSummary => $"{Partitions.Count(x => x.Selected)}/{Partitions.Count}";

    private FlashSlotGroup(string label, string tarPath, IEnumerable<FlashPartitionItem> partitions) {
        Label = label;
        TarPath = tarPath;
        Partitions = new ObservableCollection<FlashPartitionItem>(partitions);
        _selected = Partitions.Count > 0 && Partitions.All(x => x.Selected);

        foreach (var item in Partitions)
            item.PropertyChanged += OnPartitionPropertyChanged;
    }

    public static IEnumerable<FlashSlotGroup> CreateFromMatches(IEnumerable<FlashPartitionItem> matches) =>
        matches
            .GroupBy(x => x.TarPath)
            .OrderBy(g => OdinOperations.GetOdinTarSlotOrder(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => {
                var items = g.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList();
                // HOME_CSC casi nunca se flashea en uso diario; BL/AP/CP/CSC quedan marcados por defecto.
                if (OdinOperations.GetOdinTarSlotOrder(g.Key) == 3) {
                    foreach (var item in items)
                        item.Selected = false;
                }

                return new FlashSlotGroup(
                    OdinOperations.GetOdinTarSlotLabel(g.Key),
                    g.Key,
                    items);
            });

    private void SetAllPartitions(bool selected) {
        _syncingSelection = true;
        try {
            foreach (var item in Partitions)
                item.Selected = selected;
        } finally {
            _syncingSelection = false;
        }

        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void OnPartitionPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(FlashPartitionItem.Selected) || _syncingSelection)
            return;

        var all = Partitions.All(x => x.Selected);
        if (_selected != all) {
            _selected = all;
            OnPropertyChanged(nameof(Selected));
        }

        OnPropertyChanged(nameof(SelectionSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
