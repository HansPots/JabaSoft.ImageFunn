using System.Collections.ObjectModel;
using Jabasoft.Base.AiBroker;

namespace ImageFunn.App.Controls;

/// <summary>
/// Een scherm dat met een model praat en dus iets te loggen heeft. Er is
/// ÉÉN LogPaneel-plek voor de hele app, net als in AI Studio; ImageFunn
/// heeft daar zelf nog geen scherm voor, maar het mechanisme (de Log-knop
/// in de rail, zie Regios/Actie/Actiebalk) staat al net zo klaar.
/// </summary>
public interface IHeeftLog
{
    /// <summary>De logregels van dit scherm.</summary>
    ObservableCollection<Logregel> Regels { get; }
}
