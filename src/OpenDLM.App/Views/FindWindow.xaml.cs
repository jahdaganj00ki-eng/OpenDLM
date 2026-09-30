using System.Windows;
using System.Windows.Input;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Views;

/// <summary>
/// The find dialog. It drives the engine's searcher, so "find next" keeps working
/// after the list is re-sorted or an entry disappears.
/// </summary>
public partial class FindWindow : Window
{
    private readonly DownloadSearcher _searcher;

    public FindWindow(DownloadSearcher searcher, SearchSettings settings)
    {
        _searcher = searcher;

        InitializeComponent();

        SearchBox.Text = string.Empty;
        MatchModeBox.IsChecked = settings.Match == SearchMatchMode.Exact;

        var fields = settings.Fields;
        FieldNameBox.IsChecked = fields.Contains(SearchField.FileName);
        FieldDescriptionBox.IsChecked = fields.Contains(SearchField.Description);
        FieldPageBox.IsChecked = fields.Contains(SearchField.ParentPage);
        FieldLinkBox.IsChecked = fields.Contains(SearchField.Url);
        FieldParentBox.IsChecked = fields.Contains(SearchField.ParentPage);
        FieldRefererBox.IsChecked = fields.Contains(SearchField.Referer);

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
    }

    /// <summary>The entry the search landed on, for the caller to select.</summary>
    public DownloadItem? Found { get; private set; }

    private SearchQuery BuildQuery() => new()
    {
        Text = SearchBox.Text,
        Match = MatchModeBox.IsChecked == true ? SearchMatchMode.Exact : SearchMatchMode.Partial,
        Fields = CollectFields()
    };

    private HashSet<SearchField> CollectFields()
    {
        var fields = new HashSet<SearchField>();

        if (FieldNameBox.IsChecked == true) fields.Add(SearchField.FileName);
        if (FieldDescriptionBox.IsChecked == true) fields.Add(SearchField.Description);
        if (FieldPageBox.IsChecked == true) fields.Add(SearchField.ParentPage);
        if (FieldLinkBox.IsChecked == true) fields.Add(SearchField.Url);
        if (FieldParentBox.IsChecked == true) fields.Add(SearchField.ParentPage);
        if (FieldRefererBox.IsChecked == true) fields.Add(SearchField.Referer);

        return fields;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Run();
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => Advance(forward: true);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => Advance(forward: false);

    private void Advance(bool forward)
    {
        // A fresh query whenever the text or the fields changed, otherwise just step.
        Run();
        Found = forward ? _searcher.FindNext() : _searcher.FindPrevious();
        Report();
    }

    private void Run()
    {
        var query = BuildQuery();

        if (query.IsEmpty)
        {
            _searcher.Reset();
            Found = null;
            ResultText.Text = string.Empty;
            return;
        }

        _searcher.Find(query);
        Found = _searcher.Current;
        Report();
    }

    private void Report()
    {
        if (_searcher.MatchCount == 0)
        {
            ResultText.Text = "No download matched.";
            return;
        }

        ResultText.Text = Found is null
            ? $"{_searcher.MatchCount} match(es)."
            : $"{_searcher.MatchCount} match(es) - current: {Fmt.Ellipsis(Found.FileName, 48)}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
