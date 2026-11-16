using System.Windows.Input;
using Avalonia.Media.Imaging;
using DshRemote.Core;

namespace DshRemote.App.ViewModels;

/// <summary>One image block inside a transcript row (thumbnail + optional full-screen command).</summary>
public sealed class ChatImagePartViewModel : ObservableObject
{
    private Bitmap? _bitmap;
    private bool _isLoading;
    private string? _errorText;

    public ChatImagePartViewModel(DshImageRef image, ICommand? openCommand = null, string? sessionId = null)
    {
        Image = image;
        OpenCommand = openCommand;
        SessionId = sessionId;
        IsLoading = true;
    }

    /// <summary>Local preview while the optimistic (not yet persisted) message is on screen.</summary>
    public static ChatImagePartViewModel FromLocalBytes(byte[] bytes, string mediaType, ICommand? openCommand = null)
    {
        var image = new DshImageRef("local-" + Guid.NewGuid().ToString("N")[..8], mediaType, bytes.Length, 0, 0);
        var viewModel = new ChatImagePartViewModel(image, openCommand) { IsLoading = false };
        viewModel.SetBitmapBytes(bytes);
        return viewModel;
    }

    public DshImageRef Image { get; }

    public string? SessionId { get; }

    /// <summary>Durable attachment id; local previews get a synthetic id.</summary>
    public string CacheKey => Image.AttachmentId;

    public ICommand? OpenCommand { get; }

    public Bitmap? Bitmap
    {
        get => _bitmap;
        private set
        {
            if (SetProperty(ref _bitmap, value))
            {
                Raise(nameof(HasBitmap), nameof(CanOpen));
            }
        }
    }

    public bool HasBitmap => _bitmap is not null;

    public bool CanOpen => _bitmap is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                Raise(nameof(HasError), nameof(IsLoading));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public string Caption
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Image.Name) ? "图片" : Image.Name!;
            var size = Image.Width > 0 && Image.Height > 0 ? $" · {Image.Width}×{Image.Height}" : string.Empty;
            return name + size;
        }
    }

    public double MaxThumbnailWidth => 320;

    public double MaxThumbnailHeight => 240;

    public void SetBitmap(Bitmap bitmap)
    {
        Bitmap = bitmap;
        IsLoading = false;
        ErrorText = null;
    }

    public void SetBitmapBytes(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            SetBitmap(new Bitmap(stream));
        }
        catch (Exception ex)
        {
            SetError("图片解码失败: " + ex.Message);
        }
    }

    public void SetError(string message)
    {
        ErrorText = message;
        IsLoading = false;
    }
}

/// <summary>A picked-but-not-yet-sent image in the composer.</summary>
public sealed class PendingImageViewModel : ObservableObject
{
    private readonly Action<PendingImageViewModel> _remove;

    public PendingImageViewModel(DshImageAttachment attachment, Bitmap? preview, Action<PendingImageViewModel> remove)
    {
        Attachment = attachment;
        Preview = preview;
        _remove = remove;
        RemoveCommand = new RelayCommand(() => _remove(this));
    }

    public DshImageAttachment Attachment { get; }

    public Bitmap? Preview { get; }

    public bool HasPreview => Preview is not null;

    public string Caption => string.IsNullOrWhiteSpace(Attachment.Name) ? "已附图" : Attachment.Name!;

    public ICommand RemoveCommand { get; }
}
