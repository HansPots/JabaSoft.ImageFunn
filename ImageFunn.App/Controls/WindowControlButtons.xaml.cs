using System.Windows;
using System.Windows.Controls;

namespace ImageFunn.App.Controls;

/// <summary>
/// Interaction logic for WindowControlButtons.xaml - see that file for what it looks like.
///
/// De drie knopjes bedienen het venster waar dit component in hangt. Dat
/// venster wordt niet als eigenschap doorgegeven maar opgezocht met
/// <see cref="Window.GetWindow(DependencyObject)"/>: zo werkt het component
/// in elke header, zonder dat de pagina eromheen iets hoeft door te geven.
/// Staat er geen venster omheen (de ontwerpweergave van Visual Studio), dan
/// blijven de knopjes gewoon stil - vandaar overal de null-controle.
/// </summary>
public partial class WindowControlButtons : UserControl
{
    private Window? _venster;

    public WindowControlButtons()
    {
        InitializeComponent();

        // Pas bij Loaded: in de constructor hangt dit component nog nergens
        // aan, dus GetWindow zou dan altijd null teruggeven.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_venster is not null)
        {
            return;
        }

        _venster = Window.GetWindow(this);
        if (_venster is null)
        {
            return;
        }

        _venster.StateChanged += OnStateChanged;
        ToonToestand();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_venster is null)
        {
            return;
        }

        _venster.StateChanged -= OnStateChanged;
        _venster = null;
    }

    private void OnStateChanged(object? sender, EventArgs e) => ToonToestand();

    /// <summary>
    /// Laat het middelste knopje zien wat het GAAT doen: een enkel kader
    /// (maximaliseren) of twee kadertjes over elkaar (terug naar de gewone
    /// maat).
    /// </summary>
    private void ToonToestand()
    {
        var gemaximaliseerd = _venster?.WindowState == WindowState.Maximized;

        MaximizeGlyph.Visibility = gemaximaliseerd ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = gemaximaliseerd ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.ToolTip = gemaximaliseerd ? "Vorige grootte" : "Maximaliseren";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_venster is not null)
        {
            _venster.WindowState = WindowState.Minimized;
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_venster is null)
        {
            return;
        }

        _venster.WindowState = _venster.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _venster?.Close();
}
