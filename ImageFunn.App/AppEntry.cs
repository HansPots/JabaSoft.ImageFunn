namespace ImageFunn.App;

/// <summary>
/// Eén geregistreerd JabaSoft-familielid, geladen uit appsettings.json's
/// Apps-lijst. Available=false betekent "nog niet op de nieuwe
/// WPF-stack herbouwd" - zo'n entry blijft zichtbaar maar is niet
/// klikbaar, in plaats van doen alsof de app al bestaat.
/// </summary>
public sealed class AppEntry
{
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public bool Available { get; set; }
}
