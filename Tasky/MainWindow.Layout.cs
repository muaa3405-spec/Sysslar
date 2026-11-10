using Avalonia.Controls;

namespace Tasky;

/// <summary>
/// Width breakpoints. The classes on RootGrid switch the layout through styles in MainWindow.axaml:
/// <c>compact</c> (under 640): card actions fold into ⋯, the view switch gets its own row, forms stack;
/// <c>narrow</c> (under 480): the app name next to the logo is hidden to make room for the header icons.
/// Widths are measured inside the text-size zoom, so a larger text size reaches the breakpoints sooner.
/// </summary>
public partial class MainWindow
{
    private const double CompactBelow = 640;
    private const double NarrowBelow  = 480;

    private void SetUpResponsiveLayout()
    {
        RootGrid.SizeChanged += (_, e) => ApplyBreakpoints(e.NewSize.Width);
        ApplyBreakpoints(RootGrid.Bounds.Width);
    }

    private bool IsCompact => RootGrid.Classes.Contains("compact");

    private void ApplyBreakpoints(double width)
    {
        if (width <= 0) return;
        bool compact = width < CompactBelow;
        RootGrid.Classes.Set("compact", compact);
        RootGrid.Classes.Set("narrow",  width < NarrowBelow);
        _editPage?.SetCompact(compact);
    }
}
