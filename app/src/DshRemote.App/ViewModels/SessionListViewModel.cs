using System.Globalization;
using System.Windows.Input;
using Arc.Avalonia.Media.Controls;
using DshRemote.Core;

namespace DshRemote.App.ViewModels;

/// <summary>One row of the session list; a plain projection of <see cref="SessionSummary"/>.</summary>
public sealed class SessionListViewModel : ObservableObject
{
    private readonly SessionSummary _summary;
    private readonly ICommand? _archiveCommand;
    private readonly ICommand? _unarchiveCommand;

    public SessionListViewModel(
        SessionSummary summary,
        ICommand openCommand,
        ICommand? archiveCommand = null,
        ICommand? unarchiveCommand = null,
        bool isArchived = false)
    {
        _summary = summary;
        _archiveCommand = archiveCommand;
        _unarchiveCommand = unarchiveCommand;
        OpenCommand = openCommand;
        IsArchived = isArchived;
        SwipeActions = archiveCommand is null
            ? null
            : new List<ArcSwipeAction>
            {
                new("归档", "archive", ArcSwipeActionTone.Danger, () => archiveCommand.Execute(this))
            };
    }

    public ICommand OpenCommand { get; }

    public ICommand? UnarchiveCommand => _unarchiveCommand;

    /// <summary>Archive action for the row's revealed swipe button (null when archiving is unavailable).</summary>
    public ICommand? ArchiveCommand => _archiveCommand;

    public bool CanArchive => _archiveCommand is not null && !IsArchived;

    public bool IsArchived { get; }

    /// <summary>
    /// Left-swipe actions for the list row (archive); null for archived rows. Bound to
    /// <c>ArcSwipeActions.Trailing</c> — the component owns the horizontal gesture and only paints the
    /// action stack while the row is being revealed.
    /// </summary>
    public IList<ArcSwipeAction>? SwipeActions { get; }

    public SessionSummary Summary => _summary;

    public string SessionId => _summary.SessionId;

    public string Title => string.IsNullOrWhiteSpace(_summary.Title)
        ? (_summary.Blank ? "(空白会话)" : "(未命名会话)")
        : _summary.Title!;

    public bool IsBlank => _summary.Blank;

    public bool Running => _summary.Running;

    public bool IsIdle => !_summary.Running;

    public bool AgentAvailable => _summary.AgentAvailable;

    public bool HasCwd => !string.IsNullOrWhiteSpace(_summary.Cwd);

    public string CwdText => ShortenPath(_summary.Cwd);

    public string CwdFull => _summary.Cwd ?? string.Empty;

    public bool HasModel => !string.IsNullOrWhiteSpace(_summary.Model);

    public string ModelText => _summary.Model ?? string.Empty;

    public string UpdatedText => FormatTimestamp(_summary.UpdatedAt);

    public string ShortId => _summary.SessionId.Length <= 10
        ? _summary.SessionId
        : _summary.SessionId[..10];

    /// <summary>Shows the last two path segments, which is what disambiguates workspaces on a phone.</summary>
    public static string ShortenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path!.TrimEnd('\\', '/');
        var parts = normalized.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2)
        {
            return normalized;
        }

        return parts[^2] + "\\" + parts[^1];
    }

    public static string FormatTimestamp(long value)
    {
        if (value <= 0)
        {
            return string.Empty;
        }

        var time = value > 100_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
        var local = time.ToLocalTime();
        var delta = DateTimeOffset.Now - local;
        if (delta.TotalMinutes < 1)
        {
            return "刚刚";
        }

        if (delta.TotalHours < 1)
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (delta.TotalDays < 1)
        {
            return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        return local.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
    }
}
