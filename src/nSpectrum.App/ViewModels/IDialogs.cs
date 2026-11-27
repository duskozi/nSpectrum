namespace nSpectrum.App.ViewModels;

/// <summary>The dialogs the view model needs, provided by the window.</summary>
public interface IDialogs
{
    void ShowInfo(string message);
    void ShowWarning(string message);

    /// <summary>Asks where to save a CSV file. Returns the chosen path, or null when cancelled.</summary>
    string? AskSaveCsvPath(string initialFolder, string fileName);
}
