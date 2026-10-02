using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalCaption.App.Interview.Answers;

/// <summary>
/// The coach box (Mac <c>typeField</c> in <c>InterviewPanel.swift</c>). Holds the draft; the
/// owner sends it. One instance moves between the bottom bar and the narrow layout's popup, so
/// the draft survives a resize.
/// </summary>
public partial class CoachInput : UserControl
{
    private int _imageCount;

    public CoachInput()
    {
        AnswersTheme.Apply(this);      // before InitializeComponent: see AnswersTheme.xaml
        InitializeComponent();
        ToolTipService.SetShowOnDisabled(SendButton, true);
    }

    /// <summary>
    /// Send was asked for with this text (possibly blank when screenshots are waiting). The box
    /// is already cleared — the Mac clears it before the turn is sent.
    /// </summary>
    public event EventHandler<string>? SendRequested;

    /// <summary>Screenshots waiting in the tray: Send is enabled with them even when the box is empty.</summary>
    public int ImageCount
    {
        get => _imageCount;
        set
        {
            if (_imageCount == value) return;
            _imageCount = value;
            UpdateSend();
        }
    }

    /// <summary>The draft.</summary>
    public string Text
    {
        get => Box.Text;
        set => Box.Text = value;
    }

    /// <summary>Put the caret in the box.</summary>
    public void FocusInput()
    {
        Box.Focus();
        Box.CaretIndex = Box.Text.Length;
    }

    private bool CanSend => Box.Text.Trim().Length > 0 || _imageCount > 0;

    private void UpdateSend()
    {
        SendButton.IsEnabled = CanSend;
        SendButton.ToolTip = _imageCount == 0 ? "Send" : $"Send with {_imageCount} screenshot(s)";
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => UpdateSend();

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift)) return;   // Shift+Enter: the TextBox adds a line
        if (modifiers is ModifierKeys.None or ModifierKeys.Control)
        {
            e.Handled = true;
            Send();
        }
    }

    private void OnSend(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        if (!CanSend) return;
        var text = Box.Text;
        Box.Clear();
        SendRequested?.Invoke(this, text);
    }
}
