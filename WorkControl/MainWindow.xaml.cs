using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace WorkControl;

public partial class MainWindow : Window
{
    private const string SharedRoot = @"D:\코덱스&클로드";
    private Chat? chat;
    private CancellationTokenSource? running;
    private bool findingCli;
    private bool cleaning;
    private static string DataRoot => Directory.Exists(SharedRoot) ? SharedRoot :
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string WorkRoot => Path.Combine(DataRoot, "어시 작업");
    private static string HistoryFile => Path.Combine(DataRoot, "어시 데이터", "chats.json");

    public MainWindow()
    {
        InitializeComponent();
        ClaudeExe.Text = CliDiscovery.Find("claude") ?? "";
        CodexExe.Text = CliDiscovery.Find("codex") ?? "";
        Loaded += FindPackagedCodex;
        RestoreChats();
        if (Chats.Items.Count == 0)
            AddChat(null);
        else
            Chats.SelectedIndex = Chats.Items.Count - 1;
    }

    private async void FindPackagedCodex(object sender, RoutedEventArgs e)
    {
        Loaded -= FindPackagedCodex;
        if (CodexExe.Text.Length != 0)
            return;
        findingCli = true;
        UpdateButtons();
        var executable = await CliDiscovery.FindPackagedCodex();
        if (CodexExe.Text.Length == 0)
            CodexExe.Text = executable ?? "";
        findingCli = false;
        UpdateButtons();
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "프로젝트 폴더 열기" };
        if (Directory.Exists(SharedRoot))
            dialog.InitialDirectory = SharedRoot;
        if (dialog.ShowDialog(this) == true)
            AddChat(dialog.FolderName);
    }

    private void StartNewChat(object sender, RoutedEventArgs e) => AddChat(chat?.Folder);

    private void AddChat(string? folder)
    {
        var item = new Chat(folder);
        Chats.Items.Add(item);
        Chats.SelectedItem = item;
        Prompt.Focus();
        SaveChats();
    }

    private void ChangeChat(object sender, SelectionChangedEventArgs e)
    {
        if (Chats.SelectedItem is not Chat selected)
            return;
        chat = selected;
        ChatTitle.Text = selected.Title;
        ProjectPath.Text = selected.Folder ?? "폴더 없이 대화";
        ProjectPath.ToolTip = selected.Folder;
        EditMode.IsChecked = false;
        WriterRole.SelectedIndex = selected.Writer == "Codex" ? 1 : 0;
        Status.Text = "";
        Messages.Children.Clear();
        foreach (var turn in selected.Turns)
        {
            ShowMessage("나", turn.Request, true);
            if (turn.Activity.Count != 0)
            {
                var log = ShowActivity();
                foreach (var line in turn.Activity)
                    AddLine(log, line);
            }
            if (turn.Answer is not null)
                ShowMessage("어시", turn.Answer, false);
        }
        if (selected.Turns.Count == 0)
        {
            Messages.Children.Add(new TextBlock
            {
                Text = "무엇을 함께 할까요?",
                FontSize = 30, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 55, 0, 12)
            });
            Messages.Children.Add(new TextBlock
            {
                Text = "메시지를 보내면 Claude와 Codex가 함께 살펴봅니다.",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        UpdateButtons();
        ScrollToBottom();
    }

    private async void SendMessage(object sender, RoutedEventArgs e) => await Submit();

    private async void SubmitWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;
        e.Handled = true;
        await Submit();
    }

    private async Task Submit()
    {
        if (running is not null || findingCli || cleaning || chat is not { } active)
            return;
        var request = Prompt.Text.Trim();
        if (request.Length == 0)
            return;
        var edit = EditMode.IsChecked == true;
        var writer = active.Writer;
        if (edit && active.Folder is null)
        {
            Status.Text = "코드 작업을 하려면 먼저 폴더를 여세요.";
            return;
        }
        var claude = CliDiscovery.Resolve(ClaudeExe.Text, "claude");
        var codex = CliDiscovery.Resolve(CodexExe.Text, "codex");
        if (claude is null || codex is null)
        {
            Status.Text = $"{(claude is null ? "Claude" : "Codex")} CLI를 찾지 못했어요. 왼쪽 CLI 설정을 확인하세요.";
            return;
        }

        if (active.Turns.Count == 0)
            Messages.Children.Clear();
        var history = string.Join("\n\n", active.Turns.TakeLast(4).Where(turn => turn.Answer is not null)
            .Select(turn => $"사용자: {Limit(turn.Request, 1300)}\n어시: {Limit(turn.Answer!, 2300)}"));
        var entry = new Turn(request);
        active.Turns.Add(entry);
        if (active.Turns.Count == 1)
        {
            active.SetTitle(Limit(request.Split('\n', 2)[0], 31));
            ChatTitle.Text = active.Title;
        }
        Prompt.Clear();
        ShowMessage("나", request, true);
        var activity = ShowActivity();
        SaveChats();

        using var cancellation = new CancellationTokenSource();
        running = cancellation;
        UpdateButtons();
        try
        {
            var folder = active.Workspace?.Root ?? active.Folder ?? AppContext.BaseDirectory;
            if (edit && active.Workspace is null)
            {
                Status.Text = "원본과 분리된 작업 폴더를 준비하는 중…";
                var target = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
                active.Workspace = await GitWorkspace.Create(active.Folder!, target, cancellation.Token,
                    line => Dispatcher.InvokeAsync(() =>
                    {
                        entry.Activity.Add(line);
                        AddLine(activity, line);
                    }));
                folder = active.Workspace.Root;
                active.HasPatch = false;
            }
            Status.Text = "Claude와 Codex가 메시지를 검토 중…";
            IAssistantAgent[] participants = [active.Agent(AgentKind.Claude, claude, folder),
                active.Agent(AgentKind.Codex, codex, folder)];
            var flow = new Collaboration(participants, folder, (label, message) => Dispatcher.InvokeAsync(() =>
            {
                var line = $"{label}\n{Limit(message, 8000)}";
                if (entry.Activity.Count != 0 && entry.Activity[^1] == line)
                    return;
                if (entry.Activity.Count >= 180)
                    entry.Activity.RemoveAt(0);
                entry.Activity.Add(line);
                AddLine(activity, line);
                if (message == "시작")
                    Status.Text = label + " 중…";
            }));
            entry.Answer = await flow.Reply(request, history, edit ? active.Workspace : null, writer, cancellation.Token);
            ShowMessage("어시", entry.Answer, false);
            Status.Text = "완료";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status.Text = "중지됨";
        }
        catch (Exception error)
        {
            var line = $"실행 오류\n{error.Message}";
            entry.Activity.Add(line);
            AddLine(activity, line);
            if (activity.Parent is Expander expander)
                expander.IsExpanded = true;
            Status.Text = "협업을 마치지 못했어요. 협업 과정에서 오류를 확인하세요.";
        }
        finally
        {
            if (active.Workspace is not null)
            {
                try { active.HasPatch = (await active.Workspace.ReadPatch(CancellationToken.None)).Length != 0; }
                catch (Exception error) { AddLine(activity, $"변경분 확인 실패\n{error.Message}"); }
            }
            running = null;
            UpdateButtons();
            SaveChats();
            ScrollToBottom();
            Prompt.Focus();
        }
    }

    private void StopMessage(object sender, RoutedEventArgs e)
    {
        Status.Text = "중지 요청 중…";
        running?.Cancel();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (running is null)
            return;
        e.Cancel = true;
        Status.Text = "실행을 중지하는 중이에요. 정리 후 창을 닫을 수 있습니다.";
        running.Cancel();
    }

    private void UpdateButtons()
    {
        var busy = running is not null || cleaning;
        Send.IsEnabled = !busy && !findingCli;
        Stop.IsEnabled = running is not null;
        NewChat.IsEnabled = OpenProject.IsEnabled = Chats.IsEnabled = !busy;
        EditMode.IsEnabled = !busy && chat?.Folder is not null;
        WriterRole.IsEnabled = !busy;
        OpenWork.IsEnabled = !busy && chat?.Workspace is not null;
        SavePatch.IsEnabled = !busy && chat?.HasPatch == true;
        CleanWork.IsEnabled = !busy && chat?.Workspace is not null;
    }

    private void ChangeWriterRole(object sender, SelectionChangedEventArgs e)
    {
        if (chat is null)
            return;
        chat.Writer = WriterRole.SelectedIndex == 1 ? "Codex" : "Claude";
        SaveChats();
    }

    private async void CleanWorkFolder(object sender, RoutedEventArgs e)
    {
        if (chat is not { Workspace: { } work } active || running is not null || cleaning)
            return;
        if (MessageBox.Show(this, "이 채팅의 작업 폴더와 그 안의 변경을 삭제할까요? 필요한 변경은 먼저 패치로 저장하세요.",
            "작업 폴더 정리", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        cleaning = true;
        UpdateButtons();
        try
        {
            await work.Delete(WorkRoot);
            active.Workspace = null;
            active.HasPatch = false;
            Status.Text = "이 채팅의 작업 폴더를 정리했어요.";
            SaveChats();
        }
        catch (Exception error) { Status.Text = $"정리 실패 · {error.Message}"; }
        finally
        {
            cleaning = false;
            UpdateButtons();
        }
    }

    private void OpenWorkFolder(object sender, RoutedEventArgs e)
    {
        if (chat?.Workspace is { } work)
            Process.Start(new ProcessStartInfo(work.Root) { UseShellExecute = true });
    }

    private async void ExportPatch(object sender, RoutedEventArgs e)
    {
        if (chat?.Workspace is not { } work)
            return;
        var dialog = new SaveFileDialog { FileName = "assi.patch", Filter = "Git 패치 (*.patch)|*.patch" };
        if (Directory.Exists(SharedRoot))
            dialog.InitialDirectory = SharedRoot;
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var bytes = await work.ReadPatch(CancellationToken.None);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            Status.Text = "변경분을 저장했어요.";
        }
        catch (Exception error) { Status.Text = $"저장 실패 · {error.Message}"; }
    }

    private void ShowMessage(string name, string text, bool user)
    {
        var bubble = new StackPanel();
        bubble.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
        bubble.Children.Add(new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(0), IsUndoEnabled = false
        });
        Messages.Children.Add(new Border
        {
            Child = bubble, CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
            Background = user ? new SolidColorBrush(Color.FromRgb(244, 244, 244)) : Brushes.White,
            Margin = new Thickness(user ? 48 : 0, 8, 0, 8)
        });
        ScrollToBottom();
    }

    private TextBox ShowActivity()
    {
        var box = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 290,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsUndoEnabled = false
        };
        Messages.Children.Add(new Expander { Header = "협업 과정 · Claude와 Codex", Content = box, Margin = new Thickness(12, 5, 0, 5) });
        return box;
    }

    private static void AddLine(TextBox activity, string text)
    {
        if (activity.Text.Length > 60_000)
            activity.Text = activity.Text[^40_000..];
        activity.AppendText(text + "\r\n\r\n");
        activity.ScrollToEnd();
    }

    private void ScrollToBottom() => Dispatcher.InvokeAsync(Transcript.ScrollToEnd);
    private static string Limit(string text, int size) => text.Length <= size ? text : text[..size] + "…";

    private void RestoreChats()
    {
        try
        {
            if (!File.Exists(HistoryFile))
                return;
            var saved = JsonSerializer.Deserialize<List<SavedChat>>(File.ReadAllText(HistoryFile)) ?? [];
            foreach (var item in saved)
            {
                var restored = new Chat(item.Folder)
                {
                    Workspace = item.Workspace is { } work && Directory.Exists(work.Root) ? work : null,
                    Writer = item.Writer
                };
                restored.HasPatch = restored.Workspace is not null && item.HasPatch;
                restored.SetTitle(item.Title);
                foreach (var (key, session) in item.Sessions)
                    restored.Sessions[key] = session;
                foreach (var savedTurn in item.Turns)
                {
                    var turn = new Turn(savedTurn.Request) { Answer = savedTurn.Answer };
                    turn.Activity.AddRange(savedTurn.Activity);
                    restored.Turns.Add(turn);
                }
                Chats.Items.Add(restored);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            historyUnavailable = true;
            MessageBox.Show(this, $"기존 채팅 기록을 읽지 못했어요. 기록 파일을 보존합니다.\n{error.Message}",
                "채팅 기록", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool historyUnavailable;

    private void SaveChats()
    {
        if (historyUnavailable)
            return;
        try
        {
            var snapshot = Chats.Items.Cast<Chat>().Select(item => item.Save()).ToArray();
            var path = HistoryFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
                File.Move(temp, path, overwrite: true);
            }
            finally { File.Delete(temp); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = $"채팅 기록 저장 실패 · {error.Message}";
        }
    }

    private sealed record SavedChat(string? Folder, string Title, string Writer, GitWorkspace? Workspace,
        bool HasPatch, Dictionary<string, SessionState> Sessions, List<SavedTurn> Turns);
    private sealed record SavedTurn(string Request, string? Answer, List<string> Activity);
    private sealed record SessionState(string? Read, string? Write);

    private sealed class Chat(string? folder) : INotifyPropertyChanged
    {
        public string? Folder { get; } = folder;
        public string Title { get; private set; } = folder is null ? "새 채팅" : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        public GitWorkspace? Workspace { get; set; }
        public bool HasPatch { get; set; }
        public string Writer { get; set; } = "Claude";
        public Dictionary<string, SessionState> Sessions { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AgentCli> agents = new(StringComparer.OrdinalIgnoreCase);
        public List<Turn> Turns { get; } = [];
        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetTitle(string title)
        {
            Title = title.Trim();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }

        public AgentCli Agent(AgentKind kind, string executable, string directory)
        {
            var key = $"{kind}|{directory}|{executable}";
            if (agents.TryGetValue(key, out var agent))
                return agent;
            Sessions.TryGetValue(key, out var state);
            agent = new AgentCli(kind, executable) { ReadSession = state?.Read, WriteSession = state?.Write };
            agents.Add(key, agent);
            return agent;
        }

        public SavedChat Save()
        {
            foreach (var (key, agent) in agents)
                Sessions[key] = new(agent.ReadSession, agent.WriteSession);
            return new(Folder, Title, Writer, Workspace, HasPatch, Sessions,
                Turns.Select(turn => new SavedTurn(turn.Request, turn.Answer, turn.Activity)).ToList());
        }
    }

    private sealed class Turn(string request)
    {
        public string Request { get; } = request;
        public List<string> Activity { get; } = [];
        public string? Answer { get; set; }
    }
}
