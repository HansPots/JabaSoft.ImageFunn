using System.Windows.Controls;

namespace ImageFunn.App.Controls;

/// <summary>
/// Interaction logic for Beeldserviceinstellingen.xaml - de ImageFunn-kaart
/// op het instellingenscherm van Jabasoft. Bewaart in de voorkeuren van
/// ImageFunn (Layout.Preferences); ImageServiceLauncher geeft het mee aan de
/// service.
/// </summary>
public partial class Beeldserviceinstellingen : UserControl
{
    /// <summary>De modellen die service.py kent (zie MODEL_KEUZE daar), met wat er op het scherm staat.</summary>
    private static readonly (string Waarde, string Tekst)[] Modellen =
    [
        ("realvis", "RealVisXL V5.0 - fotorealistisch (standaard)"),
        ("juggernaut", "JuggernautXL Ragnarok - fotorealistisch"),
        ("sdxl", "SDXL-basismodel"),
        ("flux", "FLUX.1-schnell (vraagt een Hugging Face-login)"),
    ];

    /// <summary>De keuzes voor het loslaten, in seconden zoals service.py ze wil (IMAGEFUNN_ONTLAAD_NA). 0 = nooit.</summary>
    private static readonly (double Seconden, string Tekst)[] Ontladen =
    [
        (60, "1 minuut"),
        (300, "5 minuten (standaard)"),
        (900, "15 minuten"),
        (1800, "30 minuten"),
        (0, "Nooit - de service blijft draaien"),
    ];

    /// <summary>Aan terwijl de keuzelijsten hun bewaarde stand krijgen, anders lokt dat zelf meteen weer een opslag uit.</summary>
    private bool _laden;

    public Beeldserviceinstellingen()
    {
        InitializeComponent();

        _laden = true;
        try
        {
            foreach (var (_, tekst) in Modellen)
            {
                ModelKeuze.Items.Add(tekst);
            }

            foreach (var (_, tekst) in Ontladen)
            {
                OntladenKeuze.Items.Add(tekst);
            }

            // Nooit gekozen? Dan de standaard van service.py tonen - dat is
            // ook wat er dan draait.
            var model = Array.FindIndex(Modellen, m => m.Waarde == Layout.Preferences.Beeldmodel);
            ModelKeuze.SelectedIndex = model >= 0 ? model : 0;

            var ontladen = Array.FindIndex(Ontladen, o => o.Seconden == Layout.Preferences.OntladenNaSeconden);
            OntladenKeuze.SelectedIndex = ontladen >= 0 ? ontladen : 1;
        }
        finally
        {
            _laden = false;
        }
    }

    private void Instelling_Gewijzigd(object sender, SelectionChangedEventArgs e)
    {
        if (_laden)
        {
            return;
        }

        if (ReferenceEquals(sender, ModelKeuze) && ModelKeuze.SelectedIndex >= 0)
        {
            Layout.Preferences.BewaarBeeldmodel(Modellen[ModelKeuze.SelectedIndex].Waarde);
        }
        else if (ReferenceEquals(sender, OntladenKeuze) && OntladenKeuze.SelectedIndex >= 0)
        {
            Layout.Preferences.BewaarOntladenNaSeconden(Ontladen[OntladenKeuze.SelectedIndex].Seconden);
        }
    }
}
