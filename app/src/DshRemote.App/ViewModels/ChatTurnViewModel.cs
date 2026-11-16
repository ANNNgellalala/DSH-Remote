using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;

namespace DshRemote.App.ViewModels;

/// <summary>
/// One agent turn: the reasoning / tool calls / intermediate output are folded into a collapsible process
/// block, while the final assistant answer stays visible in the transcript.
/// </summary>
public sealed class ChatTurnViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private bool _isRunning;
    private bool _hasAutoExpanded;
    private bool _hasAutoCollapsed;
    private bool _isExpanded;
    private long _startedAt;
    private long _endedAt;
    private string _reasoningText = string.Empty;
    private bool _hasReasoning;

    /// <summary>
    /// A running turn is auto-expanded only up to this many process rows. Reopening a session whose single
    /// running turn has collected hundreds of tool calls would otherwise expand and lay out every card on the
    /// first screen (several seconds of blocked UI). Longer turns start collapsed with the same live header;
    /// the user can still expand them on demand.
    /// </summary>
    private const int AutoExpandProcessLimit = 40;

    public ChatTurnViewModel(int turn)
    {
        Turn = turn;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Raise(nameof(TitleText), nameof(DurationText));
    }

    public int Turn { get; }

    /// <summary>Reasoning / tool calls / intermediate output (rendered inside the expander).</summary>
    public ObservableCollection<ChatMessageViewModel> Process { get; } = new();

    /// <summary>The final assistant answer for this turn (rendered in the main transcript).</summary>
    public ObservableCollection<ChatMessageViewModel> Final { get; } = new();

    /// <summary>
    /// Files this turn produced (via <c>present</c> / <c>deliverables/presented</c>) or modified (file-writing
    /// tool calls), rendered as a display-only card under the final answer.
    /// </summary>
    public ObservableCollection<FileChangeViewModel> Files { get; } = new();

    public bool HasFiles => Files.Count > 0;

    private int _fileOverflow;

    public bool HasFileOverflow => _fileOverflow > 0;

    public string FileOverflowText => "+ " + _fileOverflow.ToString(CultureInfo.CurrentCulture) + " 个文件未显示";

    public ICommand ToggleCommand { get; }

    public bool HasProcess => Process.Count > 0;

    public bool ShowHeader => HasProcess || _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                Raise(nameof(ShowHeader), nameof(TitleText), nameof(Chevron));
                if (value)
                {
                    _timer.Start();
                }
                else
                {
                    _timer.Stop();
                }
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                Raise(nameof(Chevron));
            }
        }
    }

    public string Chevron => IsExpanded ? "▾" : "▸";

    public bool HasReasoning
    {
        get => _hasReasoning;
        private set => SetProperty(ref _hasReasoning, value);
    }

    public string ReasoningText
    {
        get => _reasoningText;
        private set => SetProperty(ref _reasoningText, value);
    }

    public string DurationText => FormatDuration(ElapsedMilliseconds);

    public string TitleText
    {
        get
        {
            var duration = FormatDuration(ElapsedMilliseconds);
            if (_isRunning)
            {
                return string.IsNullOrEmpty(duration) ? "正在执行中" : "正在执行中 · " + duration;
            }

            return string.IsNullOrEmpty(duration) ? "已完成" : "已完成 · " + duration;
        }
    }

    public long StartedAt => _startedAt;

    private long ElapsedMilliseconds
    {
        get
        {
            var end = _isRunning
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                : (_endedAt > 0 ? _endedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return Math.Max(0, end - _startedAt);
        }
    }

    /// <summary>Rebuilds the fold contents; expansion defaults follow the run state (running: open, done: closed).</summary>
    public void Update(
        IReadOnlyList<ChatMessageViewModel> process,
        ChatMessageViewModel? final,
        bool running,
        long startedAt,
        long endedAt,
        string? reasoning,
        IReadOnlyList<FileChangeViewModel>? files = null,
        int fileOverflow = 0)
    {
        SyncProcess(process);
        SyncFinal(final);

        UpdateFiles(files, fileOverflow);

        _startedAt = startedAt > 0 ? startedAt : _startedAt;
        _endedAt = endedAt;
        IsRunning = running;
        HasReasoning = !string.IsNullOrWhiteSpace(reasoning);
        ReasoningText = reasoning ?? string.Empty;

        if (running && !_hasAutoExpanded)
        {
            _hasAutoExpanded = true;
            IsExpanded = process.Count <= AutoExpandProcessLimit;
        }
        else if (!running && !_hasAutoCollapsed)
        {
            _hasAutoCollapsed = true;
            IsExpanded = false;
        }

        Raise(nameof(HasProcess), nameof(ShowHeader), nameof(TitleText), nameof(DurationText), nameof(HasFiles));
#if DEBUG
        if (Environment.GetEnvironmentVariable("DSHREMOTE_TIMELINE_TRACE") == "1")
        {
            Console.WriteLine($"[Turn] turn={Turn} running={running} process={process.Count} final={(final is null ? 0 : 1)} files={Files.Count} expanded={IsExpanded}");
        }
#endif
    }

    /// <summary>
    /// Keeps <see cref="Process"/> in sync incrementally. Streaming appends one tool/reasoning row at a
    /// time; clearing and refilling the whole collection on every update made the fold re-create hundreds
    /// of cards per event and froze the UI on large turns.
    /// </summary>
    private void SyncProcess(IReadOnlyList<ChatMessageViewModel> process)
    {
        var common = 0;
        while (common < Process.Count && common < process.Count && ReferenceEquals(Process[common], process[common]))
        {
            common++;
        }

        if (common == Process.Count && common == process.Count)
        {
            return;
        }

        if (common == Process.Count)
        {
            for (var index = common; index < process.Count; index++)
            {
                Process.Add(process[index]);
            }

            return;
        }

        if (common == process.Count)
        {
            for (var index = Process.Count - 1; index >= common; index--)
            {
                Process.RemoveAt(index);
            }

            return;
        }

        Process.Clear();
        foreach (var message in process)
        {
            Process.Add(message);
        }
    }

    private void SyncFinal(ChatMessageViewModel? final)
    {
        if (final is null)
        {
            if (Final.Count > 0)
            {
                Final.Clear();
            }

            return;
        }

        if (Final.Count == 1 && ReferenceEquals(Final[0], final))
        {
            return;
        }

        Final.Clear();
        Final.Add(final);
    }

    /// <summary>
    /// Refreshes the turn's file card. Entries are compared by identity: the projection creates fresh view
    /// models for every rebuild, so a structural signature (path + kind + counts + description) decides
    /// whether the observable collection has to be repopulated.
    /// </summary>
    private void UpdateFiles(IReadOnlyList<FileChangeViewModel>? files, int fileOverflow)
    {
        files ??= Array.Empty<FileChangeViewModel>();
        var changed = files.Count != Files.Count || fileOverflow != _fileOverflow;
        if (!changed)
        {
            for (var index = 0; index < files.Count; index++)
            {
                var candidate = files[index];
                var current = Files[index];
                if (!string.Equals(candidate.Path, current.Path, StringComparison.Ordinal) ||
                    candidate.Kind != current.Kind ||
                    candidate.Added != current.Added ||
                    candidate.Deleted != current.Deleted ||
                    !string.Equals(candidate.Description, current.Description, StringComparison.Ordinal))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
        {
            return;
        }

        Files.Clear();
        foreach (var file in files)
        {
            Files.Add(file);
        }

        _fileOverflow = Math.Max(0, fileOverflow);
        Raise(nameof(HasFiles), nameof(HasFileOverflow), nameof(FileOverflowText));
    }

    /// <summary>Stops the timer when the conversation is disposed.</summary>
    public void Stop()
    {
        _timer.Stop();
    }

    private static string FormatDuration(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return string.Empty;
        }

        if (milliseconds < 60_000)
        {
            return Math.Max(1, milliseconds / 1000).ToString(CultureInfo.CurrentCulture) + "s";
        }

        var seconds = milliseconds / 1000;
        return (seconds / 60) + "m" + (seconds % 60).ToString("00", CultureInfo.CurrentCulture) + "s";
    }
}
