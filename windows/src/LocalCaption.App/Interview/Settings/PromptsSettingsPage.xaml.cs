using System.Windows.Controls;

namespace LocalCaption.App.Interview.Settings;

/// <summary>
/// Settings → Prompts: <c>interview.custom_instructions</c> (specs/SPEC-16 §5.7). Edits
/// <see cref="InterviewSettingsModel.Draft"/>.
/// </summary>
public partial class PromptsSettingsPage : UserControl
{
    public PromptsSettingsPage(InterviewSettingsModel model)
    {
        Model = model;
        InitializeComponent();
        DataContext = model;
    }

    public InterviewSettingsModel Model { get; }
}
