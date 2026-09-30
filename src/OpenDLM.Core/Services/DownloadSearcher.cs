using OpenDLM.Core.Models;

namespace OpenDLM.Core.Services;

/// <summary>
/// One search request, mirroring the reference tool's fields: the text to look for,
/// which fields to look in, and whether a partial or an exact match counts.
/// </summary>
public sealed class SearchQuery
{
    public string Text { get; set; } = string.Empty;

    /// <summary>At least one field must be enabled, otherwise the search would match everything.</summary>
    public HashSet<SearchField> Fields { get; set; } = new() { SearchField.FileName };

    public SearchMatchMode Match { get; set; } = SearchMatchMode.Partial;

    /// <summary>Only items in this category are considered; null means every category.</summary>
    public DownloadCategory? Category { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) || Fields.Count == 0;

    public bool Matches(DownloadItem item)
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return false;
        }

        if (Category.HasValue && item.Category != Category.Value)
        {
            return false;
        }

        var text = Text.Trim();

        foreach (var field in Fields)
        {
            var candidate = ValueFor(item, field);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var hit = Match == SearchMatchMode.Exact
                ? string.Equals(candidate.Trim(), text, StringComparison.OrdinalIgnoreCase)
                : candidate.Contains(text, StringComparison.OrdinalIgnoreCase);

            if (hit)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ValueFor(DownloadItem item, SearchField field) => field switch
    {
        SearchField.FileName => item.FileName,
        SearchField.Description => item.Description,
        SearchField.Url => item.Url,
        // The reference calls this the parent web page: where the link was found.
        SearchField.ParentPage => item.PageUrl,
        SearchField.Referer => item.Referer ?? item.PageUrl,
        _ => null
    };
}

/// <summary>
/// Searches the download list and walks matches, including "find next".
///
/// Kept in the engine rather than the view model because "find next" has to survive
/// the list being re-sorted or an item disappearing, which only the engine can see.
/// </summary>
public sealed class DownloadSearcher
{
    private readonly DownloadManager _manager;
    private IReadOnlyList<DownloadItem> _lastMatches = Array.Empty<DownloadItem>();
    private int _cursor = -1;

    public DownloadSearcher(DownloadManager manager)
    {
        _manager = manager;
    }

    public int MatchCount => _lastMatches.Count;

    public DownloadItem? Current => _cursor >= 0 && _cursor < _lastMatches.Count
        ? _lastMatches[_cursor]
        : null;

    /// <summary>Runs a search and returns every match.</summary>
    public IReadOnlyList<DownloadItem> Find(SearchQuery query)
    {
        if (query.IsEmpty)
        {
            _lastMatches = Array.Empty<DownloadItem>();
            _cursor = -1;
            return _lastMatches;
        }

        _lastMatches = _manager.Snapshot().Where(query.Matches).ToList();
        _cursor = _lastMatches.Count > 0 ? 0 : -1;
        return _lastMatches;
    }

    /// <summary>Advances to the next match, wrapping around at the end.</summary>
    public DownloadItem? FindNext()
    {
        if (_lastMatches.Count == 0)
        {
            return null;
        }

        _cursor = (_cursor + 1) % _lastMatches.Count;
        return Current;
    }

    /// <summary>Steps back to the previous match, wrapping around at the start.</summary>
    public DownloadItem? FindPrevious()
    {
        if (_lastMatches.Count == 0)
        {
            return null;
        }

        _cursor = (_cursor - 1 + _lastMatches.Count) % _lastMatches.Count;
        return Current;
    }

    public void Reset()
    {
        _lastMatches = Array.Empty<DownloadItem>();
        _cursor = -1;
    }
}
