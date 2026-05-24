using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Thor_Flash.Services;

public enum LogTone {
    Session,
    Success,
    Error,
    Warning,
    Diagnostic
}

/// <summary>Log con colores estilo Odin Flash (#59b369, cyan, orange…).</summary>
public sealed class ColoredLogWriter(RichTextBox target) {
    static readonly SolidColorBrush SessionBrush = Create("#59B369");
    static readonly SolidColorBrush SuccessBrush = Brushes.Cyan;
    static readonly SolidColorBrush ErrorBrush = Brushes.YellowGreen;
    static readonly SolidColorBrush WarningBrush = Brushes.Orange;
    static readonly SolidColorBrush DiagnosticBrush = Create("#90A4AE");

    public void WriteLine(string text, LogTone tone = LogTone.Session) {
        if (string.IsNullOrEmpty(text))
            return;

        if (!target.Dispatcher.CheckAccess()) {
            // BeginInvoke evita deadlock si el hilo UI espera progreso USB/PIT.
            target.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => WriteLine(text, tone));
            return;
        }

        var range = new TextRange(target.Document.ContentEnd, target.Document.ContentEnd) {
            Text = text + Environment.NewLine
        };
        range.ApplyPropertyValue(TextElement.ForegroundProperty, GetBrush(tone));
        range.ApplyPropertyValue(TextElement.FontWeightProperty,
            tone is LogTone.Success or LogTone.Error ? FontWeights.Bold : FontWeights.Normal);
        target.ScrollToEnd();
    }

    private static Brush GetBrush(LogTone tone) => tone switch {
        LogTone.Success => SuccessBrush,
        LogTone.Error => ErrorBrush,
        LogTone.Warning => WarningBrush,
        LogTone.Diagnostic => DiagnosticBrush,
        _ => SessionBrush
    };

    private static SolidColorBrush Create(string hex) {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }
}
