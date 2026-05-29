using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace TrimFetch.Models;

public sealed partial class DownloadItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string SourceUrl { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ThumbnailPath { get; set; }

    [ObservableProperty]
    public partial bool IsNavigationHighlighted { get; set; }

    [ObservableProperty]
    public partial bool IsPointerHighlighted { get; set; }

    public bool ShowsHistoryRowChrome => IsNavigationHighlighted || IsPointerHighlighted;

    partial void OnIsNavigationHighlightedChanged(bool value) =>
        OnPropertyChanged(nameof(ShowsHistoryRowChrome));

    partial void OnIsPointerHighlightedChanged(bool value) =>
        OnPropertyChanged(nameof(ShowsHistoryRowChrome));

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    private bool _isCopied;

    [JsonIgnore]
    public bool IsCopied
    {
        get => _isCopied;
        set => SetProperty(ref _isCopied, value);
    }

    [ObservableProperty]
    [JsonIgnore]
    public partial bool IsGifExporting { get; set; }

    [ObservableProperty]
    [JsonIgnore]
    public partial bool IsGifCopied { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Title)
        ? Path.GetFileNameWithoutExtension(FilePath)
        : Title;

    public string FileName => Path.GetFileName(FilePath);

    public string FolderPath => Path.GetDirectoryName(FilePath) ?? string.Empty;

    public bool FileExists => File.Exists(FilePath);

    public string SourceName
    {
        get
        {
            if (!Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                return "Web";
            }

            var host = uri.Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                host = host[4..];
            }

            var source = host.Split('.')[0];
            return source.ToLowerInvariant() switch
            {
                "instagram" => "Instagram",
                "youtube" or "youtu" => "YouTube",
                "tiktok" => "TikTok",
                "x" or "twitter" => "X",
                "vimeo" => "Vimeo",
                _ => char.ToUpperInvariant(source[0]) + source[1..],
            };
        }
    }
}
