using System.Text.Json.Serialization;

namespace OpenDLM.Core.Models;

/// <summary>Settings for the find dialog, mirroring the reference's search tool.</summary>
public sealed class SearchSettings
{
    [JsonPropertyName("match")]
    public SearchMatchMode Match { get; set; } = SearchMatchMode.Partial;

    [JsonPropertyName("fields")]
    public List<SearchField> Fields { get; set; } = new() { SearchField.FileName };

    /// <summary>Wrap around to the first match after the last one.</summary>
    [JsonPropertyName("wrapAround")]
    public bool WrapAround { get; set; } = true;

    /// <summary>Select the next match automatically as the download runs.</summary>
    [JsonPropertyName("autoSelectNext")]
    public bool AutoSelectNext { get; set; }
}

/// <summary>The interface font, which the reference can change at runtime.</summary>
public sealed class UiFontSettings
{
    /// <summary>Empty means the WPF default, which is the system font.</summary>
    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public double Size { get; set; } = 12;

    [JsonPropertyName("bold")]
    public bool Bold { get; set; }
}

/// <summary>How the toolbar icons are drawn.</summary>
public sealed class ToolbarIconSettings
{
    [JsonPropertyName("size")]
    public ToolbarIconSize Size { get; set; } = ToolbarIconSize.Classic;

    /// <summary>When set, the icons come from this folder instead of the built-in set.</summary>
    [JsonPropertyName("customIconFolder")]
    public string CustomIconFolder { get; set; } = string.Empty;
}

/// <summary>Which columns the find dialog searches, as separate switches.</summary>
public sealed class SearchFieldSettings
{
    [JsonPropertyName("searchFileName")]
    public bool SearchFileName { get; set; } = true;

    [JsonPropertyName("searchDescription")]
    public bool SearchDescription { get; set; } = true;

    [JsonPropertyName("searchPageName")]
    public bool SearchPageName { get; set; }

    [JsonPropertyName("searchDownloadLink")]
    public bool SearchDownloadLink { get; set; }

    [JsonPropertyName("searchParentPage")]
    public bool SearchParentPage { get; set; }

    [JsonPropertyName("searchReferer")]
    public bool SearchReferer { get; set; }

    [JsonPropertyName("partialMatch")]
    public bool PartialMatch { get; set; } = true;

    public HashSet<SearchField> ToFields()
    {
        var fields = new HashSet<SearchField>();

        if (SearchFileName) fields.Add(SearchField.FileName);
        if (SearchDescription) fields.Add(SearchField.Description);
        if (SearchPageName) fields.Add(SearchField.ParentPage);
        if (SearchDownloadLink) fields.Add(SearchField.Url);
        if (SearchParentPage) fields.Add(SearchField.ParentPage);
        if (SearchReferer) fields.Add(SearchField.Referer);

        return fields;
    }
}
