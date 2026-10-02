using System.Windows.Controls;

namespace LocalCaption.App.Interview.Settings;

/// <summary>
/// Settings → Asking: the Ask and Screenshot hotkeys, what an Ask sends, the busy policy and
/// clipboard screenshots (specs/SPEC-16 §5.7). Edits <see cref="InterviewSettingsModel.Draft"/>.
/// </summary>
/// <remarks>
/// The hotkeys are only <i>recorded</i> here. Registering them with Windows is the host's
/// hotkey service, after Save; its "unavailable" reason comes back through
/// <see cref="InterviewSettingsModel.AskRegistrationError"/> /
/// <see cref="InterviewSettingsModel.ScreenshotRegistrationError"/>.
/// </remarks>
public partial class AskingSettingsPage : UserControl
{
    public AskingSettingsPage(InterviewSettingsModel model)
    {
        Model = model;
        InitializeComponent();
        DataContext = model;
    }

    public InterviewSettingsModel Model { get; }
}
