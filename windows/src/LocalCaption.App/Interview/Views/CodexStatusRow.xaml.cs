using System.Windows;
using System.Windows.Controls;
using LocalCaption.App.Interview.Views.Prep;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Views;

/// <summary>
/// Codex status and sign-in (Mac <c>CodexStatusRow</c>, SPEC-16 §5.2): "Checking Codex…", the
/// status line, <b>Sign in…</b> / Cancel / <b>Check again</b>, the browser hint, and the
/// lowest usage window (orange below 20 %). Usable in the preparation and in Settings → Codex.
/// </summary>
/// <remarks>
/// Set <see cref="Codex"/> from code. The row listens to <see cref="CodexService.Changed"/>
/// only while loaded (the service is app-wide). It never starts Codex on its own — Caption only
/// mode must not (owner decision) — unless the host sets <see cref="CheckWhenShown"/>.
/// </remarks>
public partial class CodexStatusRow : UserControl
{
    private CodexStatusViewModel? _model;

    public CodexStatusRow()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => _model?.Detach();
    }

    /// <summary>The service to show. Setting it rebinds the row.</summary>
    public CodexService? Codex
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            _model?.Detach();
            field = value;
            _model = value is null ? null : new CodexStatusViewModel(value, Dispatcher);
            DataContext = _model;
            if (IsLoaded) Attach();
        }
    }

    /// <summary>
    /// When true, the row runs one status check when it is shown and Codex has never been
    /// checked — the Mac row's behaviour. Off by default: only set it where Interview mode is
    /// already chosen.
    /// </summary>
    public bool CheckWhenShown { get; set; }

    private void Attach()
    {
        if (_model is null) return;
        _model.Attach();
        if (CheckWhenShown) _model.CheckIfUnchecked();
    }
}
