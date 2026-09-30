using System.Windows;
using OpenDLM.App.Services;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Views;

/// <summary>
/// The "add a download" dialog.
///
/// It has two modes. Standalone, it collects the details and enqueues the download.
/// When the browser bridge passes in an existing request, the dialog edits that
/// request in place and adds nothing itself, so the pipe handler stays the single
/// place that enqueues browser hand-offs.
/// </summary>
public partial class AddUrlWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly SettingsService _settingsService;
    private readonly AddDownloadRequest? _prefill;
    private bool _addToQueue;

    public AddUrlWindow(DownloadManager manager, SettingsService settingsService, AddDownloadRequest? prefill = null)
    {
        _manager = manager;
        _settingsService = settingsService;
        _prefill = prefill;

        InitializeComponent();

        BuildCategoryList();
        BuildQueueList();
        BuildConnectionList();

        if (prefill is not null)
        {
            Title = "Confirm the download";
            UrlBox.Text = prefill.Url ?? string.Empty;
            DirectoryBox.Text = prefill.Directory ?? _manager.ResolveDirectory(prefill.Category ?? DownloadCategory.Other);
            FileNameBox.Text = prefill.FileName ?? string.Empty;
            DescriptionBox.Text = prefill.Description ?? "From the browser";
            RefererBox.Text = prefill.Referer ?? prefill.PageUrl ?? string.Empty;
            UserBox.Text = prefill.Username ?? string.Empty;
            PasswordBox.Password = prefill.Password ?? string.Empty;
            StartNowBox.IsChecked = prefill.StartNow;

            HintText.Text =
                "The browser handed this address to OpenDLM. Adjust anything you like, then choose OK to queue it.\n\n" +
                "Cookies and the user agent are carried over automatically so protected downloads keep working.";
        }
        else
        {
            var settings = _settingsService.Current;

            // Remember-last-save: reuse the folder of the previous download when the
            // user asked for that, otherwise start from the configured default.
            DirectoryBox.Text = settings.General.RememberLastSave &&
                                !string.IsNullOrWhiteSpace(settings.General.LastUsedDirectory)
                ? settings.General.LastUsedDirectory!
                : settings.Downloads.DefaultDownloadDirectory;

            StartNowBox.IsChecked = settings.Downloads.StartDownloadsAutomatically
                                    && !settings.Downloads.AddToQueueByDefault;
            _addToQueue = settings.Downloads.AddToQueueByDefault;
        }

        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.CaretIndex = UrlBox.Text.Length;
        };
    }

    /// <summary>Short status line handed back to the main window after a successful add.</summary>
    public string? ResultMessage { get; private set; }

    private void BuildCategoryList()
    {
        CategoryBox.Items.Add(new CategoryChoice("Automatic (from the file type)", null));
        CategoryBox.Items.Add(new CategoryChoice("Video", DownloadCategory.Video));
        CategoryBox.Items.Add(new CategoryChoice("Music", DownloadCategory.Music));
        CategoryBox.Items.Add(new CategoryChoice("Programs", DownloadCategory.Program));
        CategoryBox.Items.Add(new CategoryChoice("Documents", DownloadCategory.Document));
        CategoryBox.Items.Add(new CategoryChoice("Compressed", DownloadCategory.Compressed));
        CategoryBox.Items.Add(new CategoryChoice("Other", DownloadCategory.Other));
        CategoryBox.SelectedIndex = 0;
    }

    private void BuildQueueList()
    {
        QueueBox.Items.Add(new QueueChoice("Main queue", 0));

        foreach (var queue in _manager.Queues.Where(q => q.Id != 0 && q.Enabled))
        {
            QueueBox.Items.Add(new QueueChoice(queue.Name, queue.Id));
        }

        var preferred = _settingsService.Current.Downloads.DefaultQueueId;
        QueueBox.SelectedIndex = 0;

        for (var index = 0; index < QueueBox.Items.Count; index++)
        {
            if (QueueBox.Items[index] is QueueChoice choice && choice.Id == preferred)
            {
                QueueBox.SelectedIndex = index;
                break;
            }
        }
    }

    private void BuildConnectionList()
    {
        foreach (var count in new[] { 1, 2, 4, 8, 16, 32 })
        {
            ConnectionsBox.Items.Add(count);
        }

        var configured = Math.Clamp(_settingsService.Current.Connection.MaxConnectionsPerFile, 1, 32);
        ConnectionsBox.SelectedItem = new[] { 1, 2, 4, 8, 16, 32 }.Contains(configured) ? configured : 8;
    }

    /// <summary>
    /// Asks about a link that is already in the list. Returns the decision, and
    /// whether the user asked to remember it. Null means the dialog was dismissed,
    /// which is treated as "do not add" and never remembered.
    /// </summary>
    private (bool Add, bool Remember)? ShowDuplicatePrompt(
        Core.Models.DownloadItem existing, string url)
    {
        var owner = Application.Current?.ActiveWindow is { IsLoaded: true } active ? active : this;

        var dialog = new DuplicatePromptWindow(
            Fmt.Ellipsis(url, 160),
            existing.StatusText,
            _settingsService.Current.General.RememberDuplicateAnswers)
        {
            Owner = owner
        };

        var result = dialog.ShowDialog();

        return result switch
        {
            true => (Add: true, Remember: dialog.ShouldRemember),
            false => (Add: false, Remember: dialog.ShouldRemember),
            _ => null
        };
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var chosen = Dialogs.PickFolder(DirectoryBox.Text, "Where should OpenDLM save this file?");
        if (chosen is not null)
        {
            DirectoryBox.Text = chosen;
        }
    }

    private void OnAddToQueue(object sender, RoutedEventArgs e)
    {
        _addToQueue = true;
        StartNowBox.IsChecked = false;
        Commit();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        var urls = UrlBox.Text
            .Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           part.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                           part.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            Dialogs.Warn(this, "Enter at least one http, https or ftp address.");
            UrlBox.Focus();
            return;
        }

        // The extension already gave us an explicit destination; refuse a blank folder.
        if (string.IsNullOrWhiteSpace(DirectoryBox.Text))
        {
            Dialogs.Warn(this, "Choose a folder to save the file in.");
            return;
        }

        if (!_settingsService.Current.Downloads.UseCategoryFolders)
        {
            try
            {
                Directory.CreateDirectory(DirectoryBox.Text.Trim());
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "That folder cannot be used:\n" + ex.Message);
                return;
            }
        }

        var category = (CategoryBox.SelectedItem as CategoryChoice)?.Category;
        var queueId = (QueueBox.SelectedItem as QueueChoice)?.Id ?? 0;
        var connections = ConnectionsBox.SelectedItem is int count ? count : 8;

        // Editing an existing request: mutate it and let the caller enqueue.
        if (_prefill is not null)
        {
            _prefill.Url = urls[0];
            _prefill.Directory = DirectoryBox.Text.Trim();
            _prefill.FileName = string.IsNullOrWhiteSpace(FileNameBox.Text) ? null : FileNameBox.Text.Trim();
            _prefill.Description = DescriptionBox.Text.Trim();
            _prefill.Referer = string.IsNullOrWhiteSpace(RefererBox.Text) ? null : RefererBox.Text.Trim();
            _prefill.Username = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim();
            _prefill.Password = string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password;
            _prefill.Category = category;
            _prefill.QueueId = queueId;
            _prefill.Connections = connections;
            _prefill.StartNow = StartNowBox.IsChecked == true;
            _prefill.AddToQueue = !_prefill.StartNow;

            DialogResult = true;
            return;
        }

        var added = 0;
        foreach (var url in urls)
        {
            var existing = _settingsService.Current.General.WarnOnDuplicateDownload
                ? _manager.FindByUrl(url)
                : null;

            if (existing is not null)
            {
                // A remembered answer replaces the prompt; otherwise ask, and offer
                // to remember the answer so the same address is not asked about again.
                var decision = _manager.ResolveDuplicate(url);
                var proceed = true;

                if (decision == DownloadManager.DuplicateDecision.NeverAdd)
                {
                    continue;
                }

                if (decision == DownloadManager.DuplicateDecision.Ask)
                {
                    var answer = ShowDuplicatePrompt(existing, url);
                    if (answer is null)
                    {
                        // Dismissed: do not add, and remember nothing.
                        continue;
                    }

                    proceed = answer.Value.Add;

                    if (answer.Value.Remember)
                    {
                        _manager.RememberDuplicateDecision(url, answer.Value.Add
                            ? DownloadManager.DuplicateDecision.AlwaysAdd
                            : DownloadManager.DuplicateDecision.NeverAdd);
                    }
                }

                if (!proceed)
                {
                    continue;
                }
            }

            var request = new AddDownloadRequest
            {
                Url = url,
                Directory = DirectoryBox.Text.Trim(),
                FileName = string.IsNullOrWhiteSpace(FileNameBox.Text) ? null : FileNameBox.Text.Trim(),
                Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? "Added by the user" : DescriptionBox.Text.Trim(),
                Referer = string.IsNullOrWhiteSpace(RefererBox.Text) ? null : RefererBox.Text.Trim(),
                Username = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim(),
                Password = string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password,
                Category = category,
                QueueId = queueId,
                Connections = connections,
                StartNow = StartNowBox.IsChecked == true,
                AddToQueue = _addToQueue || StartNowBox.IsChecked != true
            };

            _ = _manager.AddAndStartAsync(request);
            added++;
        }

        if (added == 0)
        {
            return;
        }

        if (_settingsService.Current.General.RememberLastSave)
        {
            var used = DirectoryBox.Text.Trim();
            _settingsService.Update(settings => settings.General.LastUsedDirectory = used);
        }

        ResultMessage = added == 1
            ? "Download added"
            : $"{added} downloads added";

        Log.Info($"User added {added} download(s) from the add dialog.");
        DialogResult = true;
    }

    private sealed record CategoryChoice(string Label, DownloadCategory? Category)
    {
        public override string ToString() => Label;
    }

    private sealed record QueueChoice(string Label, int Id)
    {
        public override string ToString() => Label;
    }
}
