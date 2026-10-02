namespace Nexus.App.Shell;

/// <summary>Implemented by pages that react to the shared toolbar (refresh and search).</summary>
public interface IShellPage
{
    bool SupportsSearch { get; }

    void Refresh();

    /// <summary>Live filtering while the user types.</summary>
    void OnSearchTextChanged(string text);

    /// <summary>Enter in the search box: deep search below the current folder.</summary>
    void OnSearchSubmitted(string text);

    /// <summary>Esc in the address bar or search box hands the keyboard back to the page's main content.</summary>
    void FocusContent()
    {
        if (this is Microsoft.UI.Xaml.Controls.Control page)
        {
            page.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        }
    }
}
