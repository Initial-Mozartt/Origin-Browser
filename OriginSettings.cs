using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OriginBrowser;

public class OriginSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OriginBrowser", "settings.json");

    // --- Session ---
    public List<TabSession> LastSessionTabs { get; set; } = new();
    public bool RestoreLastSession { get; set; } = true;

    // --- Bookmarks ---
    public List<BookmarkItem> Bookmarks { get; set; } = new();

    // --- History (last 500 entries, deduped by URL) ---
    public List<HistoryEntry> History { get; set; } = new();
    public int MaxHistoryEntries { get; set; } = 500;

    // --- Preferences ---
    public string HomePageUrl { get; set; } = "https://origin.mozartt.workers.dev/";
    public string SearchEngineUrl { get; set; } = "https://www.google.com/search?q={0}";
    public bool FramelessWindow { get; set; } = false;
    public bool ChromeVisible { get; set; } = true;
    public bool ShowBookmarkBar { get; set; } = false;

    // --- Singleton ---
    private static OriginSettings? _instance;
    public static OriginSettings Instance => _instance ??= Load();

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(SettingsPath, json);
    }

    private static OriginSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new OriginSettings();
        var json = File.ReadAllText(SettingsPath);
        try
        {
            return JsonSerializer.Deserialize<OriginSettings>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }) ?? new OriginSettings();
        }
        catch
        {
            return new OriginSettings();
        }
    }

    public void AddHistoryEntry(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        History.RemoveAll(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase));
        History.Insert(0, new HistoryEntry { Url = url, Title = title, VisitedAt = DateTime.UtcNow });
        while (History.Count > MaxHistoryEntries) History.RemoveAt(History.Count - 1);
        Save();
    }

    public void RecordSession(List<TabSession> tabs)
    {
        LastSessionTabs = tabs ?? new List<TabSession>();
        Save();
    }
}

public class TabSession
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public class BookmarkItem
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class HistoryEntry
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime VisitedAt { get; set; }
}
