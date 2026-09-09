using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArkaCode.Services;
using Microsoft.Win32;

namespace ArkaCode;

public partial class MainWindow : Window
{
    private readonly AiBusClient _client = new();
    private readonly WorkspaceService _workspace = new();
    private readonly ObservableCollection<ChatEntry> _chat = [];
    private IReadOnlyList<FileOperation> _pendingOperations = [];
    private CancellationTokenSource? _runCancellation;
    private string? _workspaceRoot;
    private string? _currentFile;

    public MainWindow()
    {
        InitializeComponent();
        ChatList.ItemsSource = _chat;
        BaseUrlBox.Text = "https://aibus.00f.ir";
        SettingsPanel.Visibility = Visibility.Visible;
        _chat.Add(new ChatEntry { Role = "agent", Content = "سلام، من عامل برنامه‌نویسی ArkaCode هستم. Workspace را انتخاب کنید، کلید AiBus را وارد کنید و دقیقاً بگویید چه چیزی باید بسازیم.", Meta = "Ready" });
    }

    private void ToggleSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void ChooseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "انتخاب Workspace برای ArkaCode", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _workspaceRoot = dialog.FolderName;
        WorkspacePathBox.Text = _workspaceRoot;
        RefreshFiles();
        FooterStatus.Text = "Workspace ایمن فعال شد · تغییرات فقط پس از تأیید اعمال می‌شوند";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password)) { ShowError("API Key پنل AiBus را وارد کنید."); return; }
        SetBusy(true, "در حال دریافت مدل‌ها...");
        try
        {
            var models = await _client.GetModelsAsync(BaseUrlBox.Text, ApiKeyBox.Password, CancellationToken.None);
            ModelCombo.ItemsSource = models;
            ModelCombo.SelectedItem = models.FirstOrDefault(model => model.Id.Contains("codex", StringComparison.OrdinalIgnoreCase)) ?? models.FirstOrDefault();
            ConnectionBadge.Text = $"{models.Count} مدل فعال";
            ConnectionBadge.Foreground = (Brush)FindResource("Mint");
            SettingsPanel.Visibility = Visibility.Collapsed;
            _chat.Add(new ChatEntry { Role = "agent", Content = $"اتصال امن برقرار شد. {models.Count} مدل از AiBus دریافت شد؛ اکنون مدل مناسب را انتخاب کنید.", Meta = "Connected" });
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally { SetBusy(false); }
    }

    private void RefreshFiles_Click(object sender, RoutedEventArgs e) => RefreshFiles();

    private void RefreshFiles()
    {
        if (_workspaceRoot is null) return;
        var files = _workspace.ListFiles(_workspaceRoot);
        FilesList.ItemsSource = files;
        ContextStats.Text = $"{files.Count:N0} فایل قابل تحلیل\nسقف Context: 90,000 نویسه";
        ContextProgress.Value = Math.Min(100, files.Count / 2d);
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_workspaceRoot is null || FilesList.SelectedItem is not string relative) return;
        try
        {
            _currentFile = relative;
            EditorTitle.Text = relative;
            EditorBox.Text = _workspace.Read(_workspaceRoot, relative);
            EditorBox.IsReadOnly = false;
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void SaveFile_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceRoot is null || _currentFile is null) return;
        try
        {
            _workspace.Apply(_workspaceRoot, [new FileOperation(_currentFile, EditorBox.Text)]);
            RunStatus.Text = "فایل ذخیره شد";
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async void RunAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_runCancellation is not null) { _runCancellation.Cancel(); return; }
        await RunAgentAsync();
    }

    private async void PromptBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (_runCancellation is null) await RunAgentAsync();
    }

    private async Task RunAgentAsync()
    {
        if (_workspaceRoot is null) { ShowError("ابتدا یک Workspace انتخاب کنید."); return; }
        if (ModelCombo.SelectedItem is not AiModel model) { ShowError("ابتدا به AiBus متصل شوید و یک مدل انتخاب کنید."); return; }
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password)) { ShowError("API Key در دسترس نیست؛ اتصال را دوباره انجام دهید."); return; }
        var task = PromptBox.Text.Trim();
        if (task.Length < 3) return;
        var effort = new[] { EffortLow, EffortMedium, EffortHigh, EffortXHigh }.First(item => item.IsChecked == true).Tag?.ToString() ?? "medium";
        _chat.Add(new ChatEntry { Role = "you", Content = task, Meta = model.DisplayName });
        PromptBox.Clear();
        _runCancellation = new CancellationTokenSource();
        SetBusy(true, "عامل در حال تحلیل Workspace است...");
        try
        {
            var timer = Stopwatch.StartNew();
            var snapshot = await Task.Run(() => _workspace.Snapshot(_workspaceRoot), _runCancellation.Token);
            RunStatus.Text = $"{snapshot.Count} فایل در Context · در حال استدلال {effort}";
            var result = await _client.RunAgentAsync(BaseUrlBox.Text, ApiKeyBox.Password, model, effort, task, snapshot, _runCancellation.Token);
            _pendingOperations = result.Operations;
            _chat.Add(new ChatEntry { Role = "agent", Content = $"{result.Summary}\n\n{result.Explanation}", Meta = $"{timer.Elapsed.TotalSeconds:0.0}s · {result.Operations.Count} changes" });
            ChangeCount.Text = $"{result.Operations.Count} فایل";
            PreviewBox.Text = result.Operations.Count == 0 ? result.Raw : string.Join("\n\n", result.Operations.Select(operation => $"{operation.Action.ToUpperInvariant()}  {operation.Path}\n────────────────────────\n{Preview(operation.Content)}"));
            ApplyButton.IsEnabled = result.Operations.Count > 0;
            RunStatus.Text = result.Operations.Count > 0 ? "آماده بازبینی و اعمال" : "پاسخ بدون تغییر فایل";
        }
        catch (OperationCanceledException) { RunStatus.Text = "اجرای عامل متوقف شد"; }
        catch (Exception exception)
        {
            _chat.Add(new ChatEntry { Role = "agent", Content = exception.Message, Meta = "Error" });
            ShowError(exception.Message);
        }
        finally { _runCancellation.Dispose(); _runCancellation = null; SetBusy(false); }
    }

    private void ApplyChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceRoot is null || _pendingOperations.Count == 0) return;
        var message = $"ArkaCode می‌خواهد {_pendingOperations.Count} فایل را تغییر دهد.\n\nقبل از هر بازنویسی، نسخه پشتیبان داخل .arkacode/backups ساخته می‌شود. ادامه می‌دهید؟";
        if (MessageBox.Show(this, message, "تأیید اعمال تغییرات", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var count = _workspace.Apply(_workspaceRoot, _pendingOperations);
            _pendingOperations = [];
            ApplyButton.IsEnabled = false;
            ChangeCount.Text = "0 فایل";
            RunStatus.Text = $"{count} فایل با موفقیت اعمال شد";
            _chat.Add(new ChatEntry { Role = "agent", Content = $"تغییرات روی {count} فایل اعمال شد و نسخه پشتیبان امن ساخته شد.", Meta = "Applied" });
            RefreshFiles();
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void SetBusy(bool busy, string status = "")
    {
        RunButton.Content = busy ? "توقف  ■" : "اجرای Agent  ↑";
        if (!string.IsNullOrWhiteSpace(status)) RunStatus.Text = status;
        ModelCombo.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        RunStatus.Text = "خطا";
        MessageBox.Show(this, message, "ArkaCode", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Preview(string content) => content.Length <= 3500 ? content : content[..3500] + "\n… preview truncated …";
}
