using System.Windows.Input;

namespace DshRemote.App.ViewModels;

/// <summary>
/// One provider row in the first level of the model picker. The picker is a three-level cascade
/// (provider → model → reasoning effort), so every row owns a parameterless <see cref="SelectCommand"/>
/// that calls back into its owning <c>SessionViewModel</c> with itself — the view stays pure XAML
/// (<c>Command="{Binding SelectCommand}"</c>).
/// </summary>
public sealed class ModelProviderViewModel : ObservableObject
{
    public ModelProviderViewModel(string id, string name, string detail, Action<ModelProviderViewModel> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        Id = id ?? string.Empty;
        Name = name ?? string.Empty;
        Detail = detail ?? string.Empty;
        SelectCommand = new RelayCommand(() => select(this));
    }

    /// <summary>Provider id (the <c>provider</c> field of <c>session/selectModel</c>).</summary>
    public string Id { get; }

    /// <summary>Provider display name from the catalog group.</summary>
    public string Name { get; }

    /// <summary>Secondary line: how many models this provider offers.</summary>
    public string Detail { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public ICommand SelectCommand { get; }
}

/// <summary>
/// One model row in the second level of the picker. Models that expose reasoning efforts are drillable
/// (the row shows a chevron and opens the effort level); the rest are committed right away.
/// </summary>
public sealed class ModelDescriptorViewModel : ObservableObject
{
    public ModelDescriptorViewModel(
        string providerId,
        string id,
        string name,
        string detail,
        bool hasReasoning,
        IReadOnlyList<ModelEffortViewModel> efforts,
        bool isSelected,
        Func<ModelDescriptorViewModel, Task> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        ProviderId = providerId ?? string.Empty;
        Id = id ?? string.Empty;
        Name = name ?? string.Empty;
        Detail = detail ?? string.Empty;
        HasReasoning = hasReasoning;
        Efforts = efforts ?? Array.Empty<ModelEffortViewModel>();
        IsSelected = isSelected;
        SelectCommand = new AsyncRelayCommand(() => select(this));
    }

    /// <summary>Owning provider id (the <c>provider</c> field of <c>session/selectModel</c>).</summary>
    public string ProviderId { get; }

    /// <summary>Model id (the <c>model</c> field of <c>session/selectModel</c>).</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>Secondary line: catalog description, falling back to the badge text.</summary>
    public string Detail { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>True when the catalog advertises a <c>reasoning</c> block for this model.</summary>
    public bool HasReasoning { get; }

    /// <summary>Efforts offered by this model (empty when it does not support reasoning).</summary>
    public IReadOnlyList<ModelEffortViewModel> Efforts { get; }

    /// <summary>Drills into the effort level instead of committing the model directly.</summary>
    public bool IsDrillable => HasReasoning && Efforts.Count > 0;

    /// <summary>Check mark for the current session selection.</summary>
    public bool IsSelected { get; }

    public ICommand SelectCommand { get; }
}

/// <summary>
/// One reasoning-effort row in the third level of the picker. <see cref="Id"/> is <c>null</c> for the
/// implicit "default (decided by the provider)" row, which omits <c>reasoningEffort</c> from the request.
/// </summary>
public sealed class ModelEffortViewModel : ObservableObject
{
    public ModelEffortViewModel(
        string? id,
        string name,
        string detail,
        bool isSelected,
        Func<ModelEffortViewModel, Task> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        Id = id;
        Name = name ?? string.Empty;
        Detail = detail ?? string.Empty;
        IsSelected = isSelected;
        SelectCommand = new AsyncRelayCommand(() => select(this));
    }

    /// <summary>Effort id sent as <c>reasoningEffort</c>; <c>null</c> means "let the provider decide".</summary>
    public string? Id { get; }

    public string Name { get; }

    public string Detail { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>True for the "default" row (no explicit <c>reasoningEffort</c>).</summary>
    public bool IsDefault => Id is null;

    public bool IsSelected { get; }

    public ICommand SelectCommand { get; }
}
