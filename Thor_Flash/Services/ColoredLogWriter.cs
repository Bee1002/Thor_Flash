using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Thor_Flash.Services;

/// <summary>Tonos del log — mismos colores que Odin_Flash (Message #A0A0A0, Result cyan, Error yellowgreen).</summary>
public enum LogTone {
    /// <summary>Etiquetas y mensajes: «Model Number :», «Flashing …:».</summary>
    Message,
    /// <summary>Valores y resultados: «SM-A326B», «Ok», « : Ok».</summary>
    Result,
    /// <summary>Errores y fallos.</summary>
    Error,
    /// <summary>Log técnico interno (no sesión Odin).</summary>
    Diagnostic
}

public sealed class ColoredLogWriter(RichTextBox target) {
    static readonly SolidColorBrush MessageBrush = Create("#A0A0A0");
    static readonly SolidColorBrush ResultBrush = Brushes.Cyan;
    static readonly SolidColorBrush ErrorBrush = Brushes.YellowGreen;
    static readonly SolidColorBrush DiagnosticBrush = Create("#707880");

    public void WriteLine(string text, LogTone tone = LogTone.Message) {
        if (string.IsNullOrEmpty(text))
            return;

        if (!target.Dispatcher.CheckAccess()) {
            target.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => WriteLine(text, tone));
            return;
        }

        var range = new TextRange(target.Document.ContentEnd, target.Document.ContentEnd) {
            Text = text + Environment.NewLine
        };
        range.ApplyPropertyValue(TextElement.ForegroundProperty, GetBrush(tone));
        range.ApplyPropertyValue(TextElement.FontWeightProperty,
            tone == LogTone.Result ? FontWeights.Bold : FontWeights.Normal);
        target.ScrollToEnd();
    }

    private static Brush GetBrush(LogTone tone) => tone switch {
        LogTone.Result => ResultBrush,
        LogTone.Error => ErrorBrush,
        LogTone.Diagnostic => DiagnosticBrush,
        _ => MessageBrush
    };

    private static SolidColorBrush Create(string hex) {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }
}
