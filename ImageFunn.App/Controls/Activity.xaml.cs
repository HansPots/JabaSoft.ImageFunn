using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Jabasoft.Base.Logging;

namespace ImageFunn.App.Controls;

/// <summary>Interaction logic for Activity.xaml - see that file for what it looks like.</summary>
public partial class Activity : UserControl
{
    /// <summary>Hoeveel regels er hooguit in beeld blijven staan.</summary>
    private const int MaxLines = 200;

    private readonly ObservableCollection<ActivityEntry> _lines = [];
    private ActivityLog? _log;

    public Activity()
    {
        InitializeComponent();

        LogLines.ItemsSource = _lines;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Haakt aan de gedeelde opvang. Eerst wat er al in staat, daarna elke
    /// nieuwe regel - anders zou je bij het openen van het scherm de
    /// opstartregels missen.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_log is not null)
        {
            return;
        }

        _log = ActivityLog.Shared;

        foreach (var entry in _log.Snapshot())
        {
            Append(entry);
        }

        _log.Added += OnEntryAdded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_log is null)
        {
            return;
        }

        _log.Added -= OnEntryAdded;
        _log = null;
    }

    /// <summary>
    /// De opvang meldt op de draad van de schrijver - dat is voor de
    /// brokerregels een achtergronddraad. Daarom hier naar de UI-draad
    /// springen voordat we aan de lijst komen.
    /// </summary>
    private void OnEntryAdded(object? sender, ActivityEntry entry) =>
        Dispatcher.BeginInvoke(new Action(() => Append(entry)));

    /// <summary>
    /// De regel gaat er als geheel in, niet als samengevoegde tekst: de
    /// drie kolommen in het sjabloon binden elk aan hun eigen veld en
    /// lijnen zo over alle regels uit.
    /// </summary>
    private void Append(ActivityEntry entry)
    {
        _lines.Add(entry);

        while (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
        }

        LogScroller.ScrollToEnd();
    }
}
