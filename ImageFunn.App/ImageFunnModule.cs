using ImageFunn.App.ImageService;

namespace ImageFunn.App;

/// <summary>
/// Wat er bij ImageFunn hoort te draaien naast het scherm zelf. Stond eerst
/// in App.OnStartup/OnExit van de losse ImageFunn-applicatie; nu ImageFunn
/// een onderdeel van Jabasoft is, roept Jabasoft dit aan.
/// </summary>
public static class ImageFunnModule
{
    /// <summary>
    /// Bij het openen van ImageFunn. Fire-and-forget: de "Tekst naar
    /// beeld"-pagina heeft de lokale Python beeldservice nodig (het model dat
    /// de beelden maakt), en die mag alvast opstarten terwijl het scherm al
    /// verschijnt.
    /// </summary>
    public static void Start() => _ = ImageServiceLauncher.EnsureRunningAsync();

    /// <summary>Bij het afsluiten van Jabasoft: de beeldservice stoppen, maar alleen als wij hem gestart hebben.</summary>
    public static void Stop() => ImageServiceLauncher.StopIfStarted();
}
