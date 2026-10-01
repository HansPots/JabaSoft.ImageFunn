using System.Windows.Controls;
using System.Windows.Markup;

namespace ImageFunn.App.Paginas;

/// <summary>
/// Interaction logic for Hoofdscherm.xaml - see that file for which
/// slots are baked in (Header/Footer) versus passed through
/// (Menu/Inhoud/Actie). [ContentProperty(nameof(MainContent))] mirrors
/// Basis' own convention.
/// </summary>
[ContentProperty(nameof(MainContent))]
public partial class Hoofdscherm : UserControl
{
    public Hoofdscherm()
    {
        InitializeComponent();
    }

    public object? MenuContent
    {
        get => Shell.MenuContent;
        set => Shell.MenuContent = value;
    }

    public object? MainContent
    {
        get => Shell.MainContent;
        set => Shell.MainContent = value;
    }

    /// <summary>
    /// De footer van deze pagina, zodat MainWindow er zijn HealthMonitor
    /// aan kan koppelen. De footer zit hier ingebakken en niet als
    /// doorgegeven inhoud, dus zonder zo een doorgeefluik komt de app er
    /// niet bij.
    /// </summary>
    public Regios.Footer.Statusbalk? Footer => Shell.FooterContent as Regios.Footer.Statusbalk;

    /// <summary>De header van deze pagina - zelfde reden en zelfde weg als Footer hierboven.</summary>
    public Regios.Header.Hoofdbalk? Header => Shell.HeaderContent as Regios.Header.Hoofdbalk;

    /// <summary>De actierail. Zit ingebakken, dus op elke pagina aanwezig.</summary>
    public Regios.Actie.Actiebalk? Actie => Shell.ActionContent as Regios.Actie.Actiebalk;

    /// <summary>Zet de splitters in de header en de footer terug op hun standaardstand - wat de verversknop in de rail doet.</summary>
    public void ResetSplitters()
    {
        Header?.ResetLayout();
        Footer?.ResetLayout();
    }
}
