using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LocalCaption.App.Interview.Settings;

/// <summary>
/// Settings → Interview: Skills, Model &amp; answers, Layout, Privacy (specs/SPEC-16 §5.7).
/// Edits <see cref="InterviewSettingsModel.Draft"/>; skills load and remove at once.
/// </summary>
public partial class InterviewSettingsPage : UserControl
{
    public InterviewSettingsPage(InterviewSettingsModel model)
    {
        Model = model;
        InitializeComponent();
        DataContext = model;
    }

    public InterviewSettingsModel Model { get; }

    /// <summary>Load… / Replace…: a <c>SKILL.md</c> or any <c>.md</c> file.</summary>
    private void OnLoadFile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SkillSlotModel slot) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Choose the {slot.Slot} skill — a SKILL.md / .md file",
            Filter = "Markdown (*.md)|*.md",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Model.LoadSkill(slot, dialog.FileName);
    }

    /// <summary>Folder…: a skill folder with <c>SKILL.md</c> and its reference files.</summary>
    private void OnLoadFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SkillSlotModel slot) return;
        var dialog = new OpenFolderDialog
        {
            Title = $"Choose the {slot.Slot} skill folder (with SKILL.md)",
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Model.LoadSkill(slot, dialog.FolderName);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SkillSlotModel slot) Model.RemoveSkill(slot);
    }

    private void OnShowNoticeAgain(object sender, RoutedEventArgs e) => Model.ShowNoticeAgain();
}
