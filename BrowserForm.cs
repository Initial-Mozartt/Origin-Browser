using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OriginBrowser;

public partial class BrowserForm : Form
{
    private WebView2 _chromeWebView = null!;
    private Panel _contentPanel = null!;

    private CoreWebView2Environment? _chromeEnv;
    private CoreWebView2Environment? _contentEnv;

    private readonly Dictionary<int, WebView2> _tabWebViews = new();
    private readonly List<int> _tabOrder = new();
    private int _activeTabId = -1;
    private int _tabIdCounter = 0;

    private readonly string _chromeUiPath;
    private readonly string _userDataFolder;
    private bool _isNavigatingFromUI;

    public BrowserForm()
    {
        _userDataFolder = Path.Combine(Application.StartupPath, "OriginUserData");
        _chromeUiPath = Path.Combine(Application.StartupPath, "chrome-ui", "index.html");
        InitializeComponent();
        _ = InitializeAsync();
    }

    private void InitializeComponent()
    {
        Text = "Origin";
        Size = new System.Drawing.Size(1400, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
        KeyPreview = true;

        // --- Top Chrome UI (explicit location — never overlaps content) ---
        var chromePanel = new Panel
        {
            Location = new System.Drawing.Point(0, 0),
            Size = new System.Drawing.Size(ClientSize.Width, 78),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        _chromeWebView = new WebView2
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        _chromeWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30);
        chromePanel.Controls.Add(_chromeWebView);
        Controls.Add(chromePanel);

        // --- Content Panel (explicitly placed at Y=78, fills the rest) ---
        _contentPanel = new Panel
        {
            Location = new System.Drawing.Point(0, 78),
            Size = new System.Drawing.Size(ClientSize.Width, ClientSize.Height - 78),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
            BorderStyle = BorderStyle.None,
            Padding = new Padding(0),
            Margin = new Padding(0),
            AutoScroll = false
        };
        Controls.Add(_contentPanel);

        // Whenever the panel resizes, snap the active WebView2 to exactly fill it.
        _contentPanel.Resize += (s, e) => SnapActiveWebView();
    }

    private async System.Threading.Tasks.Task InitializeAsync()
    {
        try
        {
            // Disable elastic overscroll / rubber-band and edge glow.
            var options = new CoreWebView2EnvironmentOptions(
                additionalBrowserArguments:
                    "--disable-overscroll-scroll-edge-effects " +
                    "--disable-features=ElasticOverscroll,PullToRefresh");

            _chromeEnv = await CoreWebView2Environment.CreateAsync(
                null,
                Path.Combine(_userDataFolder, "Chrome"),
                options);

            _contentEnv = await CoreWebView2Environment.CreateAsync(
                null,
                Path.Combine(_userDataFolder, "Content"),
                options);

            await _chromeWebView.EnsureCoreWebView2Async(_chromeEnv);

            // Dark scrollbars / form controls in chrome UI
            _chromeWebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

            _chromeWebView.CoreWebView2.Navigate(_chromeUiPath);
            _chromeWebView.CoreWebView2.WebMessageReceived += ChromeWebView_WebMessageReceived;

            _chromeWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _chromeWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            var settings = OriginSettings.Instance;
            if (settings.RestoreLastSession && settings.LastSessionTabs.Count > 0)
            {
                foreach (var tab in settings.LastSessionTabs)
                {
                    AddNewTab(tab.Url);
                }
            }
            else
            {
                AddNewTab(settings.HomePageUrl);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize Origin: {ex.Message}", "Origin Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
        }
    }

    // ---------------------------------------------------------------------
    // Layout helper: force the active WebView2 to exactly fill the panel.
    // ---------------------------------------------------------------------
    private void SnapActiveWebView()
    {
        if (_activeTabId != -1 && _tabWebViews.TryGetValue(_activeTabId, out var wv))
        {
            // ClientRectangle is (0,0, ClientSize.Width, ClientSize.Height).
            // This guarantees zero gap and no viewport offset.
            wv.Bounds = _contentPanel.ClientRectangle;
        }
    }

    // ---------------------------------------------------------------------
    // Shortcut Injection (Ctrl+T/W/L/etc. from inside any page)
    // ---------------------------------------------------------------------
    private void InjectShortcutHandler(WebView2 webView)
    {
        const string script = @"
(function() {
    if (window.__originAccel) return;
    window.__originAccel = true;

    document.addEventListener('keydown', function(e) {
        if (!e.ctrlKey) return;

        var action = null;
        switch (e.key) {
            case 'Tab':
                if (e.shiftKey) action = 'prevTab';
                else action = 'nextTab';
                break;
            case 't': case 'T': action = 'newTab'; break;
            case 'w': case 'W': action = 'closeTab'; break;
            case 'l': case 'L': action = 'focusAddressBar'; break;
            case 'r': case 'R': action = 'reload'; break;
            case 'h': case 'H': action = 'home'; break;
            case 'ArrowLeft':  action = 'goBack'; break;
            case 'ArrowRight': action = 'goForward'; break;
            case '[': action = 'goBack'; break;
            case ']': action = 'goForward'; break;
        }

        if (action && window.chrome && window.chrome.webview) {
            e.preventDefault();
            e.stopImmediatePropagation();
            window.chrome.webview.postMessage({ type: action });
        }
    }, true);
})();
";
        webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    private void ContentWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(message)) return;

            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "nextTab":
                    BeginInvoke(() => { NextTab(); });
                    break;
                case "prevTab":
                    BeginInvoke(() => { PrevTab(); });
                    break;
                case "newTab":
                    BeginInvoke(() => AddNewTab("https://origin.mozartt.workers.dev/"));
                    break;
                case "closeTab":
                    BeginInvoke(() => CloseActiveTab());
                    break;
                case "focusAddressBar":
                    BeginInvoke(() => FocusAddressBar());
                    break;
                case "reload":
                    BeginInvoke(() => GetActiveWebView()?.CoreWebView2?.Reload());
                    break;
                case "home":
                    BeginInvoke(() => NavigateActiveTab("https://origin.mozartt.workers.dev/"));
                    break;
                case "goBack":
                    BeginInvoke(() => GetActiveWebView()?.CoreWebView2?.GoBack());
                    break;
                case "goForward":
                    BeginInvoke(() => GetActiveWebView()?.CoreWebView2?.GoForward());
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Content shortcut error: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // Tab Management (Panel-based, explicit Bounds — no Dock on WebView2s)
    // ---------------------------------------------------------------------
    private async void AddNewTab(string url)
    {
        // Do NOT use Dock=Fill. Explicit Bounds prevents the 1-pixel white seam
        // and viewport offset that causes scroll bounce / hidden top.
        var webView = new WebView2
        {
            Visible = false,
            Margin = new Padding(0),
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30)
        };

        _contentPanel.Controls.Add(webView);

        // Size immediately so the first page loads with the correct viewport.
        webView.Bounds = _contentPanel.ClientRectangle;

        var tabId = _tabIdCounter++;
        await webView.EnsureCoreWebView2Async(_contentEnv);

        // Dark scrollbars for web content
        webView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

        InjectShortcutHandler(webView);
        webView.CoreWebView2.WebMessageReceived += ContentWebView_WebMessageReceived;

        // Inject overscroll fix + Vim scroll script
        await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetPageInjectionScript());

        webView.CoreWebView2.SourceChanged += (s, e) =>
        {
            if (_activeTabId == tabId && !_isNavigatingFromUI)
                Invoke(() => _ = UpdateChromeUIAsync());
        };

        webView.CoreWebView2.DocumentTitleChanged += (s, e) =>
        {
            Invoke(() => _ = UpdateChromeUIAsync());
        };

        webView.CoreWebView2.NavigationCompleted += (s, e) =>
        {
            if (e.IsSuccess && webView.CoreWebView2.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var url = webView.CoreWebView2.Source;
                var title = webView.CoreWebView2.DocumentTitle ?? string.Empty;
                OriginSettings.Instance.AddHistoryEntry(url, title);
            }
        };

        _tabWebViews[tabId] = webView;
        _tabOrder.Add(tabId);

        ActivateTab(tabId);

        webView.CoreWebView2.Navigate(url);
        _ = UpdateChromeUIAsync();
    }

    private void ActivateTab(int tabId)
    {
        if (_activeTabId == tabId) return;

        // Hide previous
        if (_activeTabId != -1 && _tabWebViews.TryGetValue(_activeTabId, out var oldView))
            oldView.Visible = false;

        // Show new — snap to exact panel bounds before making visible.
        if (_tabWebViews.TryGetValue(tabId, out var newView))
        {
            newView.Bounds = _contentPanel.ClientRectangle;
            newView.Visible = true;
            newView.BringToFront();
            newView.Focus();
        }

        _activeTabId = tabId;
    }

    private void CloseActiveTab()
    {
        if (_activeTabId != -1)
            CloseTabById(_activeTabId);
    }

    private void CloseTabById(int tabId)
    {
        if (!_tabWebViews.TryGetValue(tabId, out var webView)) return;

        _contentPanel.Controls.Remove(webView);
        webView.Dispose();
        _tabWebViews.Remove(tabId);

        int index = _tabOrder.IndexOf(tabId);
        _tabOrder.Remove(tabId);

        if (_activeTabId == tabId)
        {
            _activeTabId = -1;
            if (_tabOrder.Count > 0)
            {
                int newIndex = Math.Max(0, index - 1);
                ActivateTab(_tabOrder[newIndex]);
            }
            else
            {
                AddNewTab("https://origin.mozartt.workers.dev/");
                return;
            }
        }

        _ = UpdateChromeUIAsync();
    }

    private void SwitchToTab(int tabId)
    {
        if (_tabOrder.Contains(tabId))
        {
            ActivateTab(tabId);
            _ = UpdateChromeUIAsync();
        }
    }

    private void NextTab()
    {
        if (_tabOrder.Count == 0) return;
        int currentIndex = _tabOrder.IndexOf(_activeTabId);
        int nextIndex = (currentIndex + 1) % _tabOrder.Count;
        ActivateTab(_tabOrder[nextIndex]);
        _ = UpdateChromeUIAsync();
    }

    private void PrevTab()
    {
        if (_tabOrder.Count == 0) return;
        int currentIndex = _tabOrder.IndexOf(_activeTabId);
        int prevIndex = (currentIndex - 1 + _tabOrder.Count) % _tabOrder.Count;
        ActivateTab(_tabOrder[prevIndex]);
        _ = UpdateChromeUIAsync();
    }

    private WebView2? GetActiveWebView()
    {
        if (_activeTabId != -1 && _tabWebViews.TryGetValue(_activeTabId, out var webView))
            return webView;
        return null;
    }

    private void NavigateActiveTab(string input)
    {
        var webView = GetActiveWebView();
        if (webView?.CoreWebView2 == null) return;

        string url;
        if (IsLikelyUrl(input))
        {
            // Direct navigation
            if (!input.Contains("://") && !input.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                url = "https://" + input;
            else
                url = input;
        }
        else
        {
            // Omnibox search -> Google
            string query = Uri.EscapeDataString(input);
            url = $"https://www.google.com/search?q={query}";
        }

        _isNavigatingFromUI = true;
        webView.CoreWebView2.Navigate(url);
        _isNavigatingFromUI = false;
        _ = UpdateChromeUIAsync();
    }

    // ---------------------------------------------------------------------
    // Chrome UI Communication
    // ---------------------------------------------------------------------
    private async System.Threading.Tasks.Task UpdateChromeUIAsync()
    {
        try
        {
            var webView = GetActiveWebView();
            if (webView?.CoreWebView2 == null) return;

            var url = webView.CoreWebView2.Source;
            var canGoBack = webView.CoreWebView2.CanGoBack;
            var canGoForward = webView.CoreWebView2.CanGoForward;
            var title = webView.CoreWebView2.DocumentTitle ?? "Untitled";

            var tabs = _tabOrder.Select(id => new
            {
                id,
                title = _tabWebViews.TryGetValue(id, out var wv)
                    ? (wv.CoreWebView2?.DocumentTitle ?? "Untitled")
                    : "Untitled",
                active = _activeTabId == id
            }).ToList();

            var payload = new { type = "state", url, canGoBack, canGoForward, title, tabs };
            var json = JsonSerializer.Serialize(payload);

            _chromeWebView.CoreWebView2?.PostWebMessageAsJson(json);

            // Update window title
            var currentTitle = _tabWebViews.TryGetValue(_activeTabId, out var activeWv)
                ? (activeWv.CoreWebView2?.DocumentTitle ?? "Untitled")
                : "Untitled";
            BeginInvoke(() => { Text = currentTitle == "Untitled" ? "Origin" : $"Origin — {currentTitle}"; });
        }
        catch
        {
            // Silently ignore if chrome UI isn't ready
        }
    }

    private void ChromeWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(message)) return;

            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "nextTab":
                    BeginInvoke(() => { NextTab(); });
                    break;
                case "prevTab":
                    BeginInvoke(() => { PrevTab(); });
                    break;
                case "navigate":
                    var url = root.GetProperty("url").GetString() ?? "";
                    NavigateActiveTab(url);
                    break;

                case "newTab":
                    var newUrl = root.TryGetProperty("url", out var nu) ? nu.GetString() : "https://origin.mozartt.workers.dev/";
                    AddNewTab(newUrl ?? "https://origin.mozartt.workers.dev/");
                    break;

                case "closeTab":
                    if (root.TryGetProperty("tabId", out var ctId))
                        CloseTabById(ctId.GetInt32());
                    else
                        CloseActiveTab();
                    break;

                case "switchTab":
                    if (root.TryGetProperty("tabId", out var stId))
                        SwitchToTab(stId.GetInt32());
                    break;

                case "goBack":
                    GetActiveWebView()?.CoreWebView2?.GoBack();
                    break;

                case "goForward":
                    GetActiveWebView()?.CoreWebView2?.GoForward();
                    break;

                case "reload":
                    GetActiveWebView()?.CoreWebView2?.Reload();
                    break;

                case "home":
                    NavigateActiveTab("https://origin.mozartt.workers.dev/");
                    break;

                case "focusAddressBar":
                    FocusAddressBar();
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Chrome message error: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // Window-level shortcuts & WndProc
    // ---------------------------------------------------------------------
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11)
        {
            ToggleFullScreen();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Prevents the borderless window from being dragged by clicks inside the
    /// WebView2 content area. Forces those hits to be treated as client-area.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        const int HTCLIENT = 1;
        const int HTCAPTION = 2;

        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)HTCAPTION && _contentPanel != null)
            {
                int lParam = m.LParam.ToInt32();
                int x = lParam & 0xFFFF;
                int y = (lParam >> 16) & 0xFFFF;
                var pt = PointToClient(new System.Drawing.Point(x, y));
                if (_contentPanel.ClientRectangle.Contains(pt))
                    m.Result = (IntPtr)HTCLIENT;
            }
            return;
        }
        base.WndProc(ref m);
    }

    private void FocusAddressBar()
    {
        _chromeWebView.Focus();
        _ = _chromeWebView.ExecuteScriptAsync(@"
            const bar = document.querySelector('#addressBarWrapper input');
            if (bar) { bar.focus(); bar.select(); }
        ");
    }

    private void ToggleFullScreen()
    {
        if (FormBorderStyle == FormBorderStyle.None)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
    }

    // ---------------------------------------------------------------------
    // Page injection: disable overscroll bounce + dark bg + Vim shortcuts
    // ---------------------------------------------------------------------
    private static string GetPageInjectionScript()
    {
        return @"
(function() {
    if (window.__originInjected) return;
    window.__originInjected = true;

    // 1. Kill elastic overscroll / rubber-band and set a dark fallback background
    //    so the edge-flash is never white, even on pages without a background.
    var style = document.createElement('style');
    style.textContent = `
        html, body { 
            overscroll-behavior: none !important; 
            background-color: #1e1e1e !important; 
        }
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-track { background: #1a1a1a; }
        ::-webkit-scrollbar-thumb { background: #444; border-radius: 5px; }
        ::-webkit-scrollbar-thumb:hover { background: #555; }
    `;
    document.head.appendChild(style);

    // 2. Vim-style scroll shortcuts
    document.addEventListener('keydown', function(e) {
        var tag = e.target.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || e.target.isContentEditable) return;

        switch(e.key) {
            case 'j':
                if (!e.ctrlKey && !e.altKey && !e.metaKey) {
                    window.scrollBy({ top: 60, behavior: 'smooth' });
                    e.preventDefault();
                }
                break;
            case 'k':
                if (!e.ctrlKey && !e.altKey && !e.metaKey) {
                    window.scrollBy({ top: -60, behavior: 'smooth' });
                    e.preventDefault();
                }
                break;
            case 'g':
                if (e.shiftKey) {
                    window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' });
                } else {
                    window.scrollTo({ top: 0, behavior: 'smooth' });
                }
                e.preventDefault();
                break;
            case 'd':
                if (e.ctrlKey) {
                    window.scrollBy({ top: window.innerHeight / 2, behavior: 'smooth' });
                    e.preventDefault();
                }
                break;
            case 'u':
                if (e.ctrlKey) {
                    window.scrollBy({ top: -window.innerHeight / 2, behavior: 'smooth' });
                    e.preventDefault();
                }
                break;
        }
    });
})();
";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var sessionTabs = _tabOrder
            .Select(id => _tabWebViews.TryGetValue(id, out var wv) ? wv : null)
            .Where(wv => wv?.CoreWebView2 != null)
            .Select(wv => new TabSession
            {
                Url = wv!.CoreWebView2.Source,
                Title = wv.CoreWebView2.DocumentTitle ?? string.Empty
            })
            .ToList();

        OriginSettings.Instance.RecordSession(sessionTabs);

        foreach (var kvp in _tabWebViews.ToList())
        {
            try { kvp.Value.Dispose(); } catch { }
        }
        _tabWebViews.Clear();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Omnibox heuristic: determines whether raw user input is a URL
    /// or a search query. Mimics Chrome's address-bar logic.
    /// </summary>
    private bool IsLikelyUrl(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input)) return false;

        // Explicit scheme (https://, ftp://, file://, etc.)
        if (input.Contains("://")) return true;
        if (input.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;

        // Extract host part (ignore path/query/fragment for the check)
        int hostEnd = input.IndexOfAny(new[] { '/', '?', '#' });
        string host = hostEnd >= 0 ? input.Substring(0, hostEnd) : input;

        // Strip port if present (e.g. localhost:8080)
        int portIdx = host.LastIndexOf(':');
        string hostNoPort = portIdx >= 0 ? host.Substring(0, portIdx) : host;

        // localhost
        if (hostNoPort.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        // IPv4 address (with or without port)
        string[] ipParts = hostNoPort.Split('.');
        if (ipParts.Length == 4 &&
            ipParts.All(p => p.Length > 0 && p.Length <= 3 && p.All(char.IsDigit)))
            return true;

        // Contains spaces -> definitely a search query
        if (hostNoPort.Contains(' ')) return false;

        // Domain-like: has a dot and the TLD is 2+ letters
        int lastDot = hostNoPort.LastIndexOf('.');
        if (lastDot > 0 && lastDot < hostNoPort.Length - 1)
        {
            string tld = hostNoPort.Substring(lastDot + 1);
            if (tld.Length >= 2 && tld.All(char.IsLetter))
                return true;
        }

        return false;
    }
}

