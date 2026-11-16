using System.Collections.ObjectModel;
using System.Windows.Input;
using DshRemote.Core;

namespace DshRemote.App.ViewModels;

/// <summary>One workspace section in the grouped session list (or the "其他"/"已归档" buckets).</summary>
public sealed class WorkspaceGroupViewModel : ObservableObject
{
    private bool _isExpanded = true;

    public WorkspaceGroupViewModel(
        string? workspaceId,
        string title,
        string? path,
        bool isArchived = false,
        bool isUngrouped = false,
        ICommand? createSessionCommand = null)
    {
        WorkspaceId = workspaceId;
        Title = title;
        Path = path;
        IsArchived = isArchived;
        IsUngrouped = isUngrouped;
        CreateSessionCommand = createSessionCommand ?? new RelayCommand(() => { });
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        _isExpanded = !isArchived;
    }

    public ICommand ToggleCommand { get; }

    public string? WorkspaceId { get; }

    public string Title { get; }

    public string? Path { get; }

    public string PathText => Path ?? string.Empty;

    public bool HasPath => !string.IsNullOrWhiteSpace(Path);

    public bool IsArchived { get; }

    public bool IsUngrouped { get; }

    public bool CanCreateSession => !IsArchived && !IsUngrouped && !string.IsNullOrWhiteSpace(WorkspaceId);

    public ICommand CreateSessionCommand { get; }

    public ObservableCollection<SessionListViewModel> Sessions { get; } = new();

    public int Count => Sessions.Count;

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>
    /// True while at least one visible session of this workspace is running. Drives the quiet green dot
    /// in the workspace picker (both the dropdown rows and the collapsed selection).
    /// </summary>
    public bool HasRunningSessions => Sessions.Any(session => session.Running);

    public string CountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
            }
        }
    }

    public string Chevron => IsExpanded ? "▾" : "▸";

    public string HeaderText => IsArchived ? $"已归档（{Count}）" : Title;

    /// <summary>Used by ComboBox's default item text; otherwise it shows the CLR type name.</summary>
    public override string ToString() => HeaderText;

    public void RefreshCounters()
    {
        Raise(
            nameof(Count),
            nameof(HasSessions),
            nameof(HasRunningSessions),
            nameof(CountText),
            nameof(HeaderText));
    }
}

