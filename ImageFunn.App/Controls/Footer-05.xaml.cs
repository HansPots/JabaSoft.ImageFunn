using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Jabasoft.Base.AiBroker;

namespace ImageFunn.App.Controls;

/// <summary>
/// Interaction logic for Footer-05.xaml - de pil AI-WACHTRIJ.
///
/// Laat zien wat er aan vragen bij de AI-broker loopt en wacht, uit ALLE
/// onderdelen samen (zie Jabasoft.Base.AiBroker.WachtrijMonitor): de tekst
/// telt ze, het balkje toont hoe ver de lopende vraag is. Een klik op de pil
/// opent een lijstje met elke vraag apart, met een knop om hem te annuleren.
///
/// De pil haalt de lijst zelf op zolang hij in beeld is - wie hem in een
/// footer zet hoeft er niets voor te doen.
/// </summary>
public partial class Footer05 : UserControl
{
    public Footer05()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            WachtrijMonitor.Changed += OnWachtrijChanged;
            Toon();
        };
        Unloaded += (_, _) => WachtrijMonitor.Changed -= OnWachtrijChanged;
    }

    /// <summary>
    /// Zet de waarde en de stand van het balkje - zelfde opzet als
    /// Footer02.Show. <paramref name="fraction"/> is 0 tot 1, buiten dat
    /// bereik geklemd.
    /// </summary>
    public void Show(string value, double fraction)
    {
        ValueText.Text = value;

        var filled = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        BarFill.Width = new GridLength(filled, GridUnitType.Star);
        BarRest.Width = new GridLength(1 - filled, GridUnitType.Star);
    }

    /// <summary>Komt van een achtergronddraad (zie WachtrijMonitor), dus via de Dispatcher.</summary>
    private void OnWachtrijChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Toon);

    private void Toon()
    {
        var lijst = WachtrijMonitor.Current;
        var bezig = lijst.FirstOrDefault(item => item.Status == WachtrijStatus.Bezig);
        var wachtend = lijst.Count(item => item.Status == WachtrijStatus.Wachtend);

        if (bezig is null && wachtend == 0)
        {
            Show(string.Empty, 0);
        }
        else
        {
            var tekst = wachtend == 0
                ? (bezig is null ? string.Empty : Stand(bezig))
                : string.Format(CultureInfo.CurrentCulture, "{0} · {1} wacht", bezig is null ? "-" : Stand(bezig), wachtend);
            Show(tekst, bezig is null ? 0 : Voortgang(bezig));
        }

        if (Lijstje.IsOpen)
        {
            VulLijstje();
        }
    }

    /// <summary>Hoe ver een lopende vraag is: het inlezen van de prompt, en daarna vol zodra het antwoord loopt.</summary>
    private static double Voortgang(WachtrijItem item) =>
        item.Tokens > 0 ? 1 : item.Inlezen ?? 0;

    /// <summary>Een paar woorden over waar een vraag staat - in de pil en in het lijstje.</summary>
    private static string Stand(WachtrijItem item) => item.Status switch
    {
        WachtrijStatus.Wachtend => string.Format(CultureInfo.CurrentCulture, "wacht · plek {0}", item.Plek),
        WachtrijStatus.Bezig when item.Tokens > 0 => string.Format(CultureInfo.CurrentCulture, "antwoordt · {0} tokens", item.Tokens),
        WachtrijStatus.Bezig when item.Inlezen is { } inlezen => string.Format(CultureInfo.CurrentCulture, "inlezen {0:0}%", inlezen * 100),
        WachtrijStatus.Bezig => "bezig",
        WachtrijStatus.Klaar => "klaar",
        WachtrijStatus.Geannuleerd => "geannuleerd",
        _ => "mislukt: " + (item.Fout ?? "onbekende fout"),
    };

    private void Pil_Klik(object sender, MouseButtonEventArgs e)
    {
        Lijstje.IsOpen = !Lijstje.IsOpen;
        if (Lijstje.IsOpen)
        {
            VulLijstje();
        }
    }

    /// <summary>
    /// Bouwt het lijstje boven de pil: per vraag wie hem stelde, welk model,
    /// waar hij staat en een balkje, met Annuleren zolang hij nog loopt of
    /// wacht. Opnieuw opgebouwd bij elke verandering - het zijn er nooit veel.
    /// </summary>
    private void VulLijstje()
    {
        Regels.Children.Clear();
        var lijst = WachtrijMonitor.Current;

        if (lijst.Count == 0)
        {
            Regels.Children.Add(Tekst("Er loopt of wacht nu geen vraag.", "TextMutedBrush", klein: true));
            return;
        }

        foreach (var item in lijst)
        {
            var regel = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            regel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            regel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var links = new StackPanel();
            var wie = string.IsNullOrWhiteSpace(item.Onderdeel) ? item.Applicatie : $"{item.Applicatie} · {item.Onderdeel}";
            links.Children.Add(Tekst(wie, "Text01Brush", klein: false));

            var kleur = item.Status switch
            {
                WachtrijStatus.Mislukt => "HealthErrorBrush",
                WachtrijStatus.Klaar => "HealthOkBrush",
                _ => "TextMutedBrush",
            };
            var duur = item.Gestart is { } gestart && item.Status == WachtrijStatus.Bezig
                ? string.Format(CultureInfo.CurrentCulture, " · {0:0} s", (DateTimeOffset.Now - gestart).TotalSeconds)
                : string.Empty;
            links.Children.Add(Tekst($"{item.Model} · {Stand(item)}{duur}", kleur, klein: true));

            if (item.Status == WachtrijStatus.Bezig)
            {
                links.Children.Add(Balkje(Voortgang(item)));
            }

            regel.Children.Add(links);

            if (item.Status is WachtrijStatus.Wachtend or WachtrijStatus.Bezig)
            {
                var knop = new Button
                {
                    Content = "Annuleren",
                    Margin = new Thickness(12, 0, 0, 0),
                    Padding = new Thickness(10, 2, 10, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = item.Id,
                };
                knop.Click += Annuleren_Click;
                Grid.SetColumn(knop, 1);
                regel.Children.Add(knop);
            }

            Regels.Children.Add(regel);
        }
    }

    private async void Annuleren_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id } knop)
        {
            knop.IsEnabled = false;
            await WachtrijMonitor.AnnuleerAsync(id);
        }
    }

    private TextBlock Tekst(string tekst, string kleur, bool klein)
    {
        var blok = new TextBlock { Text = tekst, TextWrapping = TextWrapping.Wrap };
        blok.SetResourceReference(TextBlock.ForegroundProperty, kleur);
        blok.SetResourceReference(TextBlock.FontSizeProperty, klein ? "FontSizeSmall" : "FontSizeBody");
        if (!klein)
        {
            blok.FontWeight = FontWeights.SemiBold;
        }

        return blok;
    }

    private static Grid Balkje(double fractie)
    {
        var balk = new Grid { Height = 4, Margin = new Thickness(0, 4, 0, 0) };
        balk.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(fractie, 0, 1), GridUnitType.Star) });
        balk.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Math.Clamp(fractie, 0, 1), GridUnitType.Star) });

        var spoor = new Border { Opacity = 0.3 };
        spoor.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        Grid.SetColumnSpan(spoor, 2);

        var vulling = new Border();
        vulling.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

        balk.Children.Add(spoor);
        balk.Children.Add(vulling);
        return balk;
    }
}
