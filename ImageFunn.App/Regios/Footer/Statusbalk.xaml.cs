using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ImageFunn.App.Layout;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Health;
using Jabasoft.Base.SystemStats;

namespace ImageFunn.App.Regios.Footer;

/// <summary>Interaction logic for Statusbalk.xaml - see that file for what it looks like.</summary>
public partial class Statusbalk : UserControl
{
    /// <summary>Waar de ververstijd vandaan komt als er niets gezet is - gelijk aan de standaard op de REFRESH TIME-kaart.</summary>
    private const double FallbackIntervalSeconds = 5;

    private const double BytesPerGigabyte = 1024d * 1024 * 1024;

    /// <summary>Waaronder de kolombreedtes van de pillen bewaard worden.</summary>
    private const string LayoutKey = "footer.pills";

    private SystemStatsPoller? _poller;

    private HealthMonitor? _monitor;

    public Statusbalk()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Statisch, net als TokenTicker - het scherm dat de vraag stelt
        // kent deze pil niet. Los van OnLoaded/OnUnloaded (die om de poller
        // gaan) omdat dit geen eigen op- of afbouw nodig heeft.
        PromptProgressTicker.Changed += OnPromptProgressChanged;
        Unloaded += (_, _) => PromptProgressTicker.Changed -= OnPromptProgressChanged;
    }

    /// <summary>
    /// De poller wordt hier gemaakt en niet in MainWindow: dit component
    /// weet welke pillen het heeft, en zo hoeft een app die deze footer
    /// gebruikt er verder niets voor te doen.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // De breedtes die de gebruiker de vorige keer versleept heeft.
        SplitterLayout.Restore(PillRow, LayoutKey);

        if (_poller is not null)
        {
            return;
        }

        _poller = new SystemStatsPoller(new WindowsSystemStatsService(), TimeSpan.FromSeconds(ReadIntervalSeconds()));
        _poller.Updated += OnStatsUpdated;
        _poller.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_poller is null)
        {
            return;
        }

        _poller.Updated -= OnStatsUpdated;
        _poller.Dispose();
        _poller = null;

        if (_monitor is not null)
        {
            _monitor.Changed -= OnHealthChanged;
            _monitor = null;
        }
    }

    /// <summary>
    /// Koppelt de SYSTEM HEALTH-pil aan de monitor van de applicatie. De
    /// app bepaalt WAT er gecontroleerd wordt en geeft die monitor hier
    /// door; deze footer laat alleen zien hoe het ervoor staat.
    /// </summary>
    public void Observe(HealthMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        if (_monitor is not null)
        {
            _monitor.Changed -= OnHealthChanged;
        }

        _monitor = monitor;
        _monitor.Changed += OnHealthChanged;

        // Meteen de stand van nu tonen: de monitor kan al gelopen hebben
        // voordat deze footer in beeld kwam.
        HealthPill.Show(monitor.Current);
    }

    /// <summary>
    /// Bewaren zodra het slepen klaar is, niet tijdens - anders schrijf je
    /// tientallen keren per seconde naar schijf.
    /// </summary>
    private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        SplitterLayout.Remember(PillRow, LayoutKey);

    /// <summary>Alle pillen weer even breed, en het bewaarde gesleep vergeten.</summary>
    public void ResetLayout() => SplitterLayout.Reset(PillRow, LayoutKey);

    private void OnHealthChanged(object? sender, HealthStatus status) => HealthPill.Show(status);

    /// <summary>
    /// Komt van de draad waar het antwoord binnenkomt (zie
    /// PromptProgressTicker), dus via de Dispatcher naar het scherm.
    /// </summary>
    private void OnPromptProgressChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (PromptProgressTicker.Active)
        {
            ProcessingPill.Show(
                string.Format(CultureInfo.InvariantCulture, "{0:0}%", PromptProgressTicker.Progress * 100),
                PromptProgressTicker.Progress);
            return;
        }

        ProcessingPill.ToonInactief();
    });

    private void OnStatsUpdated(object? sender, SystemStatsSnapshot snapshot)
    {
        CpuPill.Show(
            string.Format(CultureInfo.InvariantCulture, "{0:0}%", snapshot.CpuPercent),
            snapshot.CpuPercent / 100);

        RamPill.Show(
            FormatGigabytes(snapshot.RamUsedBytes, snapshot.RamTotalBytes),
            Fraction(snapshot.RamUsedBytes, snapshot.RamTotalBytes));

        VramPill.Show(
            snapshot.VramUsedBytes is { } vramUsed && snapshot.VramTotalBytes is { } vramTotal
                ? FormatGigabytes(vramUsed, vramTotal)
                : "n.b.",
            snapshot.VramUsedBytes is { } used && snapshot.VramTotalBytes is { } total ? Fraction(used, total) : 0);

        // De REFRESH TIME-kaart zet de nieuwe waarde neer terwijl de poller
        // al loopt; hier pikken we 'm op.
        if (_poller is { } poller)
        {
            poller.Interval = TimeSpan.FromSeconds(ReadIntervalSeconds());
        }
    }

    /// <summary>De ververstijd uit de resources, gezet door de REFRESH TIME-kaart (Controls/Setting-03).</summary>
    private double ReadIntervalSeconds() =>
        TryFindResource("RefreshIntervalSeconds") is double seconds && seconds > 0
            ? seconds
            : FallbackIntervalSeconds;

    private static string FormatGigabytes(long usedBytes, long totalBytes) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.0} / {1:0.0} GB",
            usedBytes / BytesPerGigabyte,
            totalBytes / BytesPerGigabyte);

    private static double Fraction(long used, long total) => total > 0 ? (double)used / total : 0;
}
