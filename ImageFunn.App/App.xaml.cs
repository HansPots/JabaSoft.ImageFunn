using System.Windows;
using Jabasoft.Base;
using Jabasoft.Base.AiBroker;
using ImageFunn.App.ImageService;

namespace ImageFunn.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Draait deze app al (of is hij net aan het opstarten), dan komt dat
        // venster naar voren en stopt dit tweede exemplaar meteen weer - vóór
        // er ook maar een venster van gemaakt is. Zie AppHandover.ClaimSingleInstance.
        if (!AppHandover.ClaimSingleInstance("ImageFunn.App"))
        {
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        // Vóór het venster: thema, lettertype, marges en ververstijd staan
        // dan meteen goed in plaats van dat je het scherm ziet omklappen.
        // Zie Layout/Preferences.
        Layout.Preferences.Apply(this);

        // Fire-and-forget, niet afgewacht: de header en de footer hebben de
        // broker nodig (tokenverbruik, gezondheidscontrole), dus die mag
        // alvast opstarten terwijl het venster al verschijnt. Elke methode
        // hierin vangt zijn eigen fouten al af, dus hier is geen try/catch
        // nodig.
        _ = AiBrokerProcessLauncher.EnsureRunningAsync();

        // Zelfde reden: de "Tekst naar beeld"-pagina heeft de lokale Python
        // beeldservice nodig (het model dat de beelden maakt), en die
        // mag alvast opstarten terwijl het venster al verschijnt.
        _ = ImageServiceLauncher.EnsureRunningAsync();
    }

    /// <summary>
    /// Ruimt de broker op als ImageFunn de laatste applicatie van de
    /// familie was die draaide. Draait er nog een andere, dan blijft hij
    /// staan - die heeft hem nodig.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        // Als eerste: wie de app nu opnieuw start, krijgt meteen een nieuw
        // exemplaar in plaats van te wachten tot het opruimen hieronder klaar is.
        AppHandover.ReleaseSingleInstance();

        // Vangnet: staat er een applicatie van de familie verborgen omdat wij
        // het scherm van haar overgenomen hebben, dan komt ze nu terug.
        AppHandover.RevealHidden();

        AiBrokerProcessLauncher.StopIfUnused();
        ImageServiceLauncher.StopIfStarted();

        base.OnExit(e);
    }
}
