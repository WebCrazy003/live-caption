using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalCaption.App.Interview.Views.Prep;
using LocalCaption.Core.Data;
using LocalCaption.Interview;
using Microsoft.Win32;

namespace LocalCaption.App.Interview.Views;

/// <summary>
/// Stage 1 of Interview mode — the preparation (SPEC-16 §5.2, Mac
/// <c>InterviewSetupSection.swift</c> and the preparation stage's footer). Fills the window.
/// </summary>
/// <remarks>
/// <para>Call <see cref="Attach"/> once with the app's <see cref="InterviewController"/> and the
/// config accessors; the view listens to the controller, its <see cref="CodexService"/> and
/// <see cref="InterviewLibrary"/> only while shown (loaded and visible).</para>
/// <para>What the host does: open Settings on <see cref="OpenSkillsSettingsRequested"/> (and
/// call <see cref="Refresh"/> after Settings closes), and react to <see cref="Left"/> — the
/// view has already set <see cref="InterviewController.ShowingPreparation"/> to false by then.
/// Completing Start preparation leaves the stage by itself (the controller sets
/// <c>ShowingPreparation</c>); watch the controller's <c>Changed</c> for that.</para>
/// </remarks>
public partial class PreparationView : UserControl
{
    private PreparationViewModel? _model;

    public PreparationView()
    {
        InitializeComponent();
        // Listen only while on screen: the host keeps this view in the tree (collapsed) while
        // the interview layout streams answers, and a hidden form need not re-read per token.
        Loaded += (_, _) => { if (IsVisible) _model?.Attach(); };
        Unloaded += (_, _) => _model?.Detach();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _model?.Attach();
            else _model?.Detach();
        };
    }

    /// <summary>The missing-skills banner's "Open Settings → Interview → Skills".</summary>
    public event EventHandler? OpenSkillsSettingsRequested;

    /// <summary>
    /// Skip preparation / Back to the interview was pressed. <see cref="InterviewController.ShowingPreparation"/>
    /// is already false; the host swaps in the captions and answers and moves focus there.
    /// </summary>
    public event EventHandler? Left;

    /// <summary>
    /// Bind the view. <paramref name="config"/> returns the live config (<c>AppEnvironment.Config</c>);
    /// <paramref name="saveConfig"/> persists it (<c>AppEnvironment.Update</c>) — the model row
    /// writes <c>interview.model</c>, <c>prep_reasoning_effort</c> and <c>reasoning_effort</c>
    /// exactly as Settings → Interview does. Call on the UI thread (the controller's owner).
    /// </summary>
    public void Attach(InterviewController interview, Func<Config> config, Action<Config> saveConfig)
    {
        _model?.Detach();
        _model = new PreparationViewModel(interview, config, saveConfig, Dispatcher);
        DataContext = _model;
        CodexRow.Codex = interview.Codex;
        if (IsLoaded && IsVisible) _model.Attach();
    }

    /// <summary>Re-read everything — after Settings closed (skills, model, efforts may have changed).</summary>
    public void Refresh() => _model?.Refresh();

    /// <summary>Put the keyboard on the first empty detail (or the Start button's area) when the stage opens.</summary>
    public void FocusFirstField()
    {
        if (_model is null) return;
        if (_model.Candidate.Trim().Length == 0) CandidateBox.Focus();
        else if (_model.Company.Trim().Length == 0) CompanyBox.Focus();
        else CandidateBox.Focus();
    }

    // ── details: commit on focus loss / Enter, not per keystroke ─────────────────────────

    private void OnDetailsCommitted(object sender, RoutedEventArgs e) => _model?.CommitDetails();

    private void OnDetailsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _model?.CommitDetails();
        e.Handled = true;
    }

    // ── ① Upload CV… ─────────────────────────────────────────────────────────────────────

    private void OnUploadCv(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose your CV (PDF, Markdown or text)",
            Filter = "CV (PDF, Markdown or text)|*.pdf;*.md;*.markdown;*.txt;*.text",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        // PDF text extraction is quick for a CV; the UI waits rather than racing a second click.
        var previous = Cursor;
        Cursor = Cursors.Wait;
        try { _model.UploadCv(dialog.FileName); }
        finally { Cursor = previous; }
        CvBox.Focus();
    }

    // ── links and footer ─────────────────────────────────────────────────────────────────

    private void OnOpenSkills(object sender, RoutedEventArgs e) =>
        OpenSkillsSettingsRequested?.Invoke(this, EventArgs.Empty);

    private async void OnStartOver(object sender, RoutedEventArgs e)
    {
        if (_model is null || !_model.ShowStartOver) return;
        if (!ConfirmStartOver(Window.GetWindow(this))) return;
        await _model.DiscardAsync();
        FocusFirstField();
    }

    private void OnLeave(object sender, RoutedEventArgs e)
    {
        if (_model is null || !_model.CanLeave) return;
        _model.Leave();
        Left?.Invoke(this, EventArgs.Empty);
    }

    // ── responsive: the two effort pickers stack when narrow ─────────────────────────────

    private const double EffortsStackBelow = 460;
    private bool? _effortsStacked;

    private void OnEffortGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var stacked = e.NewSize.Width < EffortsStackBelow;
        if (_effortsStacked == stacked) return;
        _effortsStacked = stacked;
        EffortFirstColumn.Width = new GridLength(1, GridUnitType.Star);
        EffortGapColumn.Width = new GridLength(stacked ? 0 : 14);
        EffortSecondColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        EffortGapRow.Height = new GridLength(stacked ? 8 : 0);
        Grid.SetColumn(AnswerEffortCell, stacked ? 0 : 2);
        Grid.SetRow(AnswerEffortCell, stacked ? 2 : 0);
    }

    // ── Start over? ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Mac's confirmation, in the app's dialog clothes: the destructive button is red at
    /// rest and Cancel is the default, so Enter never discards.
    /// </summary>
    private static bool ConfirmStartOver(Window? owner)
    {
        var title = new TextBlock { Text = "Start over?", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        var text = new TextBlock
        {
            Text = "The steps run so far are deleted. Your uploaded CVs and skills stay.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };

        var discard = new Button { Content = "_Discard the preparation", MinWidth = 170 };
        discard.SetResourceReference(StyleProperty, "Button.Destructive");
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(discard);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        panel.Children.Add(title);
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        var dialog = new ChromeWindow
        {
            Title = "Start over",
            Content = panel,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        discard.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }
}
