using System.Windows;
using System.Windows.Controls;

namespace LocalCaption.App.Interview.Settings;

/// <summary>
/// Settings → Codex: status and sign-in, the Codex path, Check again, Sign out…, Plus usage
/// (specs/SPEC-16 §5.7). The path is a config key (Save/Cancel); everything else acts at once.
/// </summary>
/// <remarks>
/// Showing the page never starts Codex by itself (SPEC-16 §9.1) unless
/// <see cref="InterviewSettingsModel.StartCodexOnOpen"/> is set; when Codex is already signed
/// in, showing it refreshes the usage, as the Mac's section does on appear.
/// </remarks>
public partial class CodexSettingsPage : UserControl
{
    public CodexSettingsPage(InterviewSettingsModel model)
    {
        Model = model;
        InitializeComponent();
        DataContext = model;
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true) await Model.CodexPageShownAsync();
        };
    }

    public InterviewSettingsModel Model { get; }

    private async void OnCheckAgain(object sender, RoutedEventArgs e) => await Model.CheckAgainAsync();

    private async void OnSignIn(object sender, RoutedEventArgs e) => await Model.SignInAsync();

    private async void OnCancelSignIn(object sender, RoutedEventArgs e) => await Model.CancelSignInAsync();

    private async void OnRefreshUsage(object sender, RoutedEventArgs e) => await Model.RefreshUsageAsync();

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (!Model.IsReady) return;
        var confirmed = ConfirmDialog.Ask(Window.GetWindow(this), "Sign out of ChatGPT in LocalCaption?",
            "Interview mode stops working until you sign in again. This signs out LocalCaption only — "
            + "the Codex app in your terminal or editor stays signed in. Any answer being written is cut off.",
            "Sign out");
        if (confirmed) await Model.SignOutAsync();
    }
}
