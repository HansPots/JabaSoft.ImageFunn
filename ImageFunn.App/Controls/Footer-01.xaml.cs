using System.Windows.Controls;
using System.Windows.Media;
using Jabasoft.Base.Health;

namespace ImageFunn.App.Controls;

/// <summary>Interaction logic for Footer-01.xaml - see that file for what it looks like.</summary>
public partial class Footer01 : UserControl
{
    public Footer01()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Zet het lampje en de tekst. De kleur komt uit het thema
    /// (HealthOk/Busy/ErrorBrush), zodat groen en rood in beide thema's
    /// leesbaar blijven en niet hier vastgetimmerd staan.
    /// </summary>
    public void Show(HealthStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var brushKey = status.State switch
        {
            HealthState.Healthy => "HealthOkBrush",
            HealthState.Failed => "HealthErrorBrush",
            _ => "HealthBusyBrush",
        };

        if (TryFindResource(brushKey) is Brush brush)
        {
            StatusLamp.Fill = brush;
        }

        StatusText.Text = status.Message;
    }
}
