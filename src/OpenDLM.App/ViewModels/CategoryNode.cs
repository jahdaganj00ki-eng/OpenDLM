using System.Collections.ObjectModel;

namespace OpenDLM.App.ViewModels;

/// <summary>
/// One entry of the category tree on the left of the main window: a display name,
/// an icon, an optional filter that decides which downloads it shows, and a live count.
/// </summary>
public sealed class CategoryNode : ObservableObject
{
    private int _count;

    public CategoryNode(string name, string iconKey, Func<Core.Models.DownloadItem, bool>? filter)
    {
        Name = name;
        IconKey = iconKey;
        Filter = filter;
    }

    public string Name { get; }

    /// <summary>Key of a geometry string in Theme/Icons.xaml.</summary>
    public string IconKey { get; }

    /// <summary>Null means "refuse nothing".</summary>
    public Func<Core.Models.DownloadItem, bool>? Filter { get; }

    public ObservableCollection<CategoryNode> Children { get; } = new();

    public int Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value))
            {
                Raise(nameof(DisplayName));
            }
        }
    }

    /// <summary>Shows the count in brackets, but only once there is something to count.</summary>
    public string DisplayName => _count > 0 ? $"{Name} ({_count})" : Name;

    public override string ToString() => Name;

    /// <summary>Builds the default tree: summary nodes, then categories, then queues.</summary>
    public static ObservableCollection<CategoryNode> BuildDefaultTree()
    {
        var all = new CategoryNode("All downloads", "Icon.Queue", null);
        all.Children.Add(new CategoryNode("Unfinished", "Icon.Pause",
            item => item.Status is Core.Models.DownloadStatus.Queued
                or Core.Models.DownloadStatus.Connecting
                or Core.Models.DownloadStatus.Downloading
                or Core.Models.DownloadStatus.Paused
                or Core.Models.DownloadStatus.Stopped
                or Core.Models.DownloadStatus.Scheduled
                or Core.Models.DownloadStatus.Error));
        all.Children.Add(new CategoryNode("Finished", "Icon.Start",
            item => item.Status == Core.Models.DownloadStatus.Complete));

        all.Children.Add(new CategoryNode("Video", "Icon.Folder",
            item => item.Category == Core.Models.DownloadCategory.Video));
        all.Children.Add(new CategoryNode("Music", "Icon.Folder",
            item => item.Category == Core.Models.DownloadCategory.Music));
        all.Children.Add(new CategoryNode("Programs", "Icon.Folder",
            item => item.Category == Core.Models.DownloadCategory.Program));
        all.Children.Add(new CategoryNode("Documents", "Icon.Folder",
            item => item.Category == Core.Models.DownloadCategory.Document));
        all.Children.Add(new CategoryNode("Compressed", "Icon.Folder",
            item => item.Category == Core.Models.DownloadCategory.Compressed));
        all.Children.Add(new CategoryNode("Other", "Icon.Folder",
            item => item.Category == Core.Models.DownloadCategory.Other));

        return new ObservableCollection<CategoryNode> { all };
    }
}
