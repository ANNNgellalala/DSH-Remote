using System.Globalization;
using System.Text.Json;

namespace DshRemote.App.ViewModels;

/// <summary>Where a turn's file row came from.</summary>
public enum FileChangeKind
{
    /// <summary>A file the agent announced through <c>present</c> (a durable deliverable).</summary>
    Produced,

    /// <summary>A file an editing tool wrote or patched while the turn ran.</summary>
    Modified
}

/// <summary>
/// One file touched by an agent turn: deliverables announced with <c>present</c>, or files written/edited
/// by the file tools. Phone builds cannot open the desktop file remotely yet, so this is display-only:
/// the card names the file, shows its workspace-relative path and the approximate line delta.
/// </summary>
public sealed class FileChangeViewModel : ObservableObject
{
    public FileChangeViewModel(string path, FileChangeKind kind)
    {
        Path = path;
        Kind = kind;
    }

    /// <summary>Absolute path as recorded by the host (folder separators normalized for display).</summary>
    public string Path { get; }

    public FileChangeKind Kind { get; }

    /// <summary>File name, the part that identifies the file at a glance.</summary>
    public string DisplayName
    {
        get
        {
            var normalized = Path.Replace('\\', '/');
            var at = normalized.LastIndexOf('/');
            return at < 0 ? normalized : normalized[(at + 1)..];
        }
    }

    /// <summary>Parent folder shown after the file name (compacted for narrow phone bubbles).</summary>
    public string DirectoryText
    {
        get
        {
            var normalized = Path.Replace('\\', '/');
            var at = normalized.LastIndexOf('/');
            if (at <= 0)
            {
                return string.Empty;
            }

            var directory = normalized[..at];
            return directory.Length <= 46 ? directory : "…" + directory[^45..];
        }
    }

    public bool HasDirectory => !string.IsNullOrEmpty(DirectoryText);

    public bool IsProduced => Kind == FileChangeKind.Produced;

    public string KindText => IsProduced ? "产出" : "修改";

    /// <summary>Description supplied by <c>present</c>; empty for edited files.</summary>
    public string Description { get; private set; } = string.Empty;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public int Added { get; private set; }

    public int Deleted { get; private set; }

    public bool HasCounts => Added > 0 || Deleted > 0;

    public string AddedText => Added > 0 ? "+" + Added.ToString(CultureInfo.CurrentCulture) : string.Empty;

    public string DeletedText => Deleted > 0 ? "−" + Deleted.ToString(CultureInfo.CurrentCulture) : string.Empty;

    public string CountsText
    {
        get
        {
            var parts = new List<string>(2);
            if (Added > 0)
            {
                parts.Add(AddedText);
            }

            if (Deleted > 0)
            {
                parts.Add(DeletedText);
            }

            return string.Join(" ", parts);
        }
    }

    public void SetDescription(string? description)
    {
        if (!string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(Description))
        {
            Description = description!.Trim();
            Raise(nameof(Description), nameof(HasDescription));
        }
    }

    public void AddChange(int added, int deleted)
    {
        if (added <= 0 && deleted <= 0)
        {
            return;
        }

        Added += Math.Max(0, added);
        Deleted += Math.Max(0, deleted);
        Raise(nameof(Added), nameof(Deleted), nameof(HasCounts), nameof(AddedText), nameof(DeletedText),
            nameof(CountsText));
    }

    public static FileChangeViewModel Produced(string path) => new(path, FileChangeKind.Produced);

    public static FileChangeViewModel Modified(string path) => new(path, FileChangeKind.Modified);

    /// <summary>Counts the lines a block of text spans (0 for an empty block).</summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var lines = 1;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    /// <summary>
    /// Parses the file target and approximate line delta out of a file-writing tool call's arguments.
    /// Returns null when the call is not a file write or carries no usable path.
    /// </summary>
    public static (string Path, int Added, int Deleted)? TryParseToolCall(string toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var path = ReadPath(root);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var name = toolName.ToLowerInvariant();
            if (name == "write" || name == "create_file" || name == "write_file")
            {
                var content = ReadString(root, "content");
                return (path!, CountLines(content), 0);
            }

            if (name.Contains("edit", StringComparison.Ordinal) || name == "apply_patch" || name == "patch")
            {
                var added = CountLines(ReadString(root, "new_string") ?? ReadString(root, "new_str") ??
                                       ReadString(root, "content"));
                var deleted = CountLines(ReadString(root, "old_string") ?? ReadString(root, "old_str"));
                if (added == 0 && deleted == 0)
                {
                    var patch = ReadString(root, "patch") ?? ReadString(root, "input");
                    (added, deleted) = CountPatchLines(patch);
                }

                return (path!, added, deleted);
            }

            return null;
        }
    }

    /// <summary>Parses the <c>files</c> array of a <c>present</c> tool call.</summary>
    public static List<FileChangeViewModel> ParsePresentedToolCall(string? argumentsJson)
    {
        var files = new List<FileChangeViewModel>();
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return files;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("files", out var filesElement) &&
                filesElement.ValueKind == JsonValueKind.Array)
            {
                AppendPresentedFiles(filesElement, files);
            }
        }
        catch (JsonException)
        {
            // A malformed tool payload must never break transcript projection; the tool card still shows it.
        }

        return files;
    }

    /// <summary>Parses the <c>files</c> array of a durable <c>deliverables/presented</c> event.</summary>
    public static List<FileChangeViewModel> ParsePresentedEvent(JsonElement data)
    {
        var files = new List<FileChangeViewModel>();
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("files", out var filesElement) &&
            filesElement.ValueKind == JsonValueKind.Array)
        {
            AppendPresentedFiles(filesElement, files);
        }

        return files;
    }

    private static void AppendPresentedFiles(JsonElement filesElement, List<FileChangeViewModel> files)
    {
        foreach (var entry in filesElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = ReadString(entry, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var file = Produced(path!.Trim());
            file.SetDescription(ReadString(entry, "description"));
            files.Add(file);
        }
    }

    private static string? ReadPath(JsonElement root)
        => ReadString(root, "file_path") ?? ReadString(root, "filePath") ?? ReadString(root, "path");

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Counts added/deleted lines in a unified-diff style patch body.</summary>
    private static (int Added, int Deleted) CountPatchLines(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return (0, 0);
        }

        var added = 0;
        var deleted = 0;
        foreach (var line in patch.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+'))
            {
                added++;
            }
            else if (line.StartsWith('-'))
            {
                deleted++;
            }
        }

        return (added, deleted);
    }
}
