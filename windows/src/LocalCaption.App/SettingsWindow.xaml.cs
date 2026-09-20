using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using LocalCaption.Asr;
using LocalCaption.Audio;
using LocalCaption.Session;
using NAudio.CoreAudioApi;

namespace LocalCaption.App;

/// <summary>
/// Settings (§10), including the two pickers this machine made necessary: the §4.5 process
/// picker and the §4.6 output-endpoint picker.
/// </summary>
/// <remarks>
/// <para>Live-applying: font size, auto-scroll, timestamps, clipboard. Everything else takes
/// effect at the next Start, because changing a model or the VAD mid-utterance would mean
/// rebuilding the decode lanes underneath a running session.</para>
/// <para>The <c>summary</c>, <c>always_on_top</c> and <c>opacity</c> keys are deliberately
/// absent from this window but kept in the schema, so a <c>config.json</c> still round-trips
/// with the macOS app (§7.3, §9.2).</para>
/// </remarks>
public partial class SettingsWindow : Window
{
    /// <summary>One entry in a picker: what to show, and what to persist.</summary>
    private sealed record Choice(string Label, string? Value);

    private readonly AppEnvironment _env;

    public SettingsWindow(AppEnvironment env)
    {
        InitializeComponent();
        _env = env;
        Load();
    }

    private void Load()
    {
        var config = _env.Config;

        // §4.5: remember the executable, never the PID — it is meaningless once the user
        // restarts Teams, and it is re-resolved at every Start.
        var processes = new List<Choice> { new("(choose an application)", null) };
        processes.AddRange(AudioSessions.List()
            .Select(s => new Choice(s.Active ? $"{s.Name} — playing now" : s.Name, s.Executable)));
        ProcessPicker.ItemsSource = processes;
        ProcessPicker.SelectedItem = processes.FirstOrDefault(
            p => string.Equals(p.Value, config.Audio.TargetProcess, StringComparison.OrdinalIgnoreCase))
            ?? processes[0];

        // §4.6: "Follow system default" is the default, because the default endpoint moves
        // when a monitor, headphones or a remote session arrives.
        var endpoints = new List<Choice> { new("Follow system default", null) };
        endpoints.AddRange(Endpoints());
        EndpointPicker.ItemsSource = endpoints;
        EndpointPicker.SelectedItem = endpoints.FirstOrDefault(e => e.Value == config.Audio.OutputDevice)
                                      ?? endpoints[0];

        var process = config.Audio.CaptureMode.Equals("process", StringComparison.OrdinalIgnoreCase);
        ModeProcess.IsChecked = process;
        ModeEndpoint.IsChecked = !process;

        var models = ModelCatalog.All.Select(m => m.Name).ToList();
        InterimModel.ItemsSource = models;
        InterimModel.SelectedItem = models.Contains(config.Asr.InterimModel) ? config.Asr.InterimModel : models[0];
        FinalModel.ItemsSource = models;
        FinalModel.SelectedItem = models.Contains(config.Asr.FinalModel) ? config.Asr.FinalModel : models[^1];

        Backend.ItemsSource = new[] { "auto", "cuda", "cpu" };
        Backend.SelectedItem = config.Asr.Backend;
        BackendNote.Text = BackendProbe.HasCudaRuntime()
            ? "A CUDA runtime was found. 'auto' will use the GPU."
            : "No CUDA runtime was found, so 'auto' will run on the CPU — much slower. " +
              "See BENCH-RESULTS.md for what that costs.";

        FontSizeSlider.Value = config.Caption.FontSize;
        AutoScroll.IsChecked = config.Caption.AutoScroll;
        ShowTimestamps.IsChecked = config.Caption.ShowTimestamps;

        AutoUpdate.IsChecked = config.Clipboard.AutoUpdate;
        RecentSentences.Text = config.Clipboard.RecentSentences.ToString(CultureInfo.InvariantCulture);

        VadSensitivity.Value = config.Audio.VadSensitivity;
        EndpointSilence.Text = config.Asr.EndpointSilenceMs.ToString(CultureInfo.InvariantCulture);
        MaxUtterance.Text = config.Asr.MaxUtteranceS.ToString(CultureInfo.InvariantCulture);

        TranscriptFolder.Text = config.General.TranscriptFolder;
    }

    private static IEnumerable<Choice> Endpoints()
    {
        MMDeviceCollection devices;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var device in devices)
        {
            string label;
            try { label = device.FriendlyName; }
            catch (Exception) { continue; }
            yield return new Choice(label, device.ID);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var config = _env.Config;

        config.Audio.CaptureMode = ModeProcess.IsChecked == true ? "process" : "endpoint";
        config.Audio.TargetProcess = (ProcessPicker.SelectedItem as Choice)?.Value;
        config.Audio.OutputDevice = (EndpointPicker.SelectedItem as Choice)?.Value;
        config.Audio.VadSensitivity = (int)VadSensitivity.Value;

        config.Asr.InterimModel = InterimModel.SelectedItem as string ?? config.Asr.InterimModel;
        config.Asr.FinalModel = FinalModel.SelectedItem as string ?? config.Asr.FinalModel;
        config.Asr.Backend = Backend.SelectedItem as string ?? "auto";
        config.Asr.EndpointSilenceMs = Number(EndpointSilence.Text, config.Asr.EndpointSilenceMs);
        config.Asr.MaxUtteranceS = Number(MaxUtterance.Text, config.Asr.MaxUtteranceS);

        config.Caption.FontSize = (int)FontSizeSlider.Value;
        config.Caption.AutoScroll = AutoScroll.IsChecked == true;
        config.Caption.ShowTimestamps = ShowTimestamps.IsChecked == true;

        config.Clipboard.AutoUpdate = AutoUpdate.IsChecked == true;
        config.Clipboard.RecentSentences = Number(RecentSentences.Text, config.Clipboard.RecentSentences);

        if (TranscriptFolder.Text is { Length: > 0 } folder) config.General.TranscriptFolder = folder;

        _env.Update(config);
        DialogResult = true;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(TranscriptFolder.Text);
            Process.Start(new ProcessStartInfo(TranscriptFolder.Text) { UseShellExecute = true });
        }
        catch (Exception) { /* an unopenable folder is not worth an error dialog */ }
    }

    private static int Number(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
