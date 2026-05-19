using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OriginBrowser;

public partial class BrowserForm : Form
{
    private SplashForm? _splashForm;
    private bool _browserReadyHandled = false;

    private const int ChromeHeight = 78;
    private const int FramelessResizeBorder = 8;
    private const int HiddenChromeDragStripHeight = 44;

    private WebView2 _chromeWebView = null!;
    private Panel _chromePanel = null!;
    private Panel _contentPanel = null!;
    private TerminalPanel _terminalPanel = null!;

    private CoreWebView2Environment? _chromeEnv;
    private CoreWebView2Environment? _contentEnv;

    private readonly Dictionary<int, WebView2> _tabWebViews = new();
    private readonly List<int> _tabOrder = new();
    private int _activeTabId = -1;
    private int _tabIdCounter = 0;

    private readonly string _chromeUiPath;
    private readonly string _userDataFolder;
    private bool _isNavigatingFromUI;
    private bool _isFullScreen;
    private FormBorderStyle _borderStyleBeforeFullScreen = FormBorderStyle.Sizable;
    private FormWindowState _windowStateBeforeFullScreen = FormWindowState.Normal;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Constructor that receives the splash form (called from Program.cs)
    public BrowserForm(SplashForm splashForm)
        : this()
    {
        _splashForm = splashForm;
    }

    // Default constructor – used by Designer and called by the constructor above
    // Default constructor – used by Designer and called by the constructor above
    public BrowserForm()
    {
        _userDataFolder = Path.Combine(Application.StartupPath, "OriginUserData");
        _chromeUiPath = Path.Combine(Application.StartupPath, "chrome-ui", "index.html");
        InitializeComponent();

        // Start minimized so the splash is the only window visible
        this.WindowState = FormWindowState.Minimized;

        this.Shown += async (s, e) =>
        {
            await InitializeAsync();
            _terminalPanel.Visible = false;
            AdjustContentForTerminal();

            // Wait until the first page has actually loaded before closing the splash
            WaitForBrowserReady();
        };
    }

    private async void WaitForBrowserReady()
    {
        // Safety timeout: force the browser to appear after 10 seconds no matter what
        var timeout = Task.Delay(10_000);

        while (!_browserReadyHandled)
        {
            var activeWv = GetActiveWebView();
            if (activeWv?.CoreWebView2 != null)
            {
                // Check if the page is already loaded
                string state = await activeWv.CoreWebView2.ExecuteScriptAsync(
                    "document.readyState");
                if (state?.Trim('"') == "complete")
                {
                    OnBrowserReady();
                    return;
                }

                // Subscribe to the NavigationCompleted event for the active tab
                activeWv.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    if (!_browserReadyHandled && e.IsSuccess)
                    {
                        OnBrowserReady();
                    }
                };
                break; // Wait for the event or timeout
            }

            await Task.Delay(200);
        }

        // If the page never finished loading, force the browser to appear
        if (!_browserReadyHandled)
            OnBrowserReady();
    }

    private void OnBrowserReady()
    {
        if (_browserReadyHandled) return;
        _browserReadyHandled = true;

        // Close the splash and show the browser
        _splashForm?.Close();
        _splashForm = null;
        this.WindowState = FormWindowState.Normal;
        this.Activate();
    }

    private void InitializeComponent()
    {
        Text = "Origin";
        Size = new System.Drawing.Size(1400, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
        KeyPreview = true;

        // --- Top Chrome UI ---
        _chromePanel = new Panel
        {
            Location = new System.Drawing.Point(0, 0),
            Size = new System.Drawing.Size(ClientSize.Width, ChromeHeight),
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
        _chromePanel.Controls.Add(_chromeWebView);
        Controls.Add(_chromePanel);

        // --- Content Panel ---
        _contentPanel = new Panel
        {
            Location = new System.Drawing.Point(0, ChromeHeight),
            Size = new System.Drawing.Size(ClientSize.Width, ClientSize.Height - ChromeHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
            BorderStyle = BorderStyle.None,
            Padding = new Padding(0),
            Margin = new Padding(0),
            AutoScroll = false
        };
        Controls.Add(_contentPanel);

        // --- Terminal Panel ---
        _terminalPanel = new TerminalPanel
        {
            Dock = DockStyle.Bottom,
            Height = 0,
            Visible = false
        };
        _terminalPanel.TerminalHeightChanged += (s, e) => AdjustContentForTerminal();
        Controls.Add(_terminalPanel);

        // Bring content panel to front so terminal sits behind it when closed
        _contentPanel.BringToFront();

        _contentPanel.Resize += (s, e) => SnapActiveWebView();
        Resize += (s, e) => AdjustContentForTerminal();
    }

    private async System.Threading.Tasks.Task InitializeAsync()
    {
        try
        {
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

            // Apply frameless setting (title bar only)
            if (settings.FramelessWindow)
            {
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                SetContentFramelessState(true);
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                SetContentFramelessState(false);
            }

            // Chrome visibility is independent of frameless
            ApplyChromeVisibility(settings.ChromeVisible, save: false);

            Invalidate();
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize Origin: {ex.Message}", "Origin Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
        }
    }

    // … every other method from your original file (SnapActiveWebView through IsLikelyUrl)
    // stays exactly as you pasted them. They are unchanged, so I am not repeating them here.
    // The only changes are above: constructors merged, old Load handler removed, _loadingLabel removed.
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
    if (typeof window.__originTrueFrameless === 'undefined') window.__originTrueFrameless = false;

    document.addEventListener('keydown', function(e) {
        if (!e.ctrlKey) return;

        var action = null;
        switch (e.key) {
            case 'f': case 'F':
                if (e.altKey) {
                    if (e.shiftKey) action = 'fullFrameless';
                    else action = 'toggleFrameless';
                }
                break;
            case 'Tab':
                if (e.shiftKey) action = 'prevTab';
                else action = 'nextTab';
                break;
            case 't': case 'T':
                if (e.shiftKey) action = 'toggleTerminal';
                else action = 'newTab';
                break;
            case 'f': case 'F':
                if (e.altKey) action = 'toggleFrameless';
                break;
            case 'b': case 'B':
                if (e.shiftKey) action = 'toggleChromeVisibility';
                break;
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

    document.addEventListener('mousedown', function(e) {
        if (!window.__originTrueFrameless) return;
        if (e.button !== 0 || !window.chrome || !window.chrome.webview) return;

        var edge = 8;
        var dragStrip = 44;
        var x = e.clientX;
        var y = e.clientY;
        var w = window.innerWidth;
        var h = window.innerHeight;
        var left = x >= 0 && x < edge;
        var right = x <= w && x >= w - edge;
        var top = y >= 0 && y < edge;
        var bottom = y <= h && y >= h - edge;
        var hit = 0;

        if (top && left) hit = 13;
        else if (top && right) hit = 14;
        else if (bottom && left) hit = 16;
        else if (bottom && right) hit = 17;
        else if (left) hit = 10;
        else if (right) hit = 11;
        else if (top) hit = 12;
        else if (bottom) hit = 15;

        if (hit) {
            e.preventDefault();
            e.stopImmediatePropagation();
            window.chrome.webview.postMessage({ type: 'beginWindowResize', hitTest: hit });
            return;
        }

        if (y >= edge && y < dragStrip) {
            e.preventDefault();
            e.stopImmediatePropagation();
            window.chrome.webview.postMessage({ type: 'beginWindowDrag' });
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
                case "fullFrameless":
                    BeginInvoke(() => FullFramelessToggle());
                    break;
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
                case "toggleTerminal":
                    BeginInvoke(() =>
                    {
                        _terminalPanel.Toggle();
                        AdjustContentForTerminal();
                    });
                    break;
                case "toggleFrameless":
                    BeginInvoke(() => ToggleFrameless());
                    break;
                case "toggleChromeVisibility":
                    BeginInvoke(() => ToggleChromeVisibility());
                    break;
                case "beginWindowDrag":
                    BeginInvoke(() => BeginWindowDrag());
                    break;
                case "beginWindowResize":
                    BeginInvoke(() => BeginWindowResize(GetInt(root, "hitTest")));
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
            _ = webView.ExecuteScriptAsync(
                $"window.__originTrueFrameless = {(FormBorderStyle == FormBorderStyle.None && !_isFullScreen ? "true" : "false")};");

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
                case "fullFrameless":
                    BeginInvoke(() => FullFramelessToggle());
                    break;
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
                case "toggleTerminal":
                    BeginInvoke(() =>
                    {
                        _terminalPanel.Toggle();
                        AdjustContentForTerminal();
                    });
                    break;
                case "toggleFrameless":
                    BeginInvoke(() => ToggleFrameless());
                    break;
                case "toggleChromeVisibility":
                    BeginInvoke(() => ToggleChromeVisibility());
                    break;
                case "beginWindowDrag":
                    BeginInvoke(() => BeginWindowDrag());
                    break;
                case "beginWindowResize":
                    BeginInvoke(() => BeginWindowResize(GetInt(root, "hitTest")));
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
        // Ctrl+Shift+T toggles the terminal (works on all keyboards)
        if (keyData == (Keys.Control | Keys.Shift | Keys.T))
        {
            _terminalPanel.Toggle();
            AdjustContentForTerminal();
            return true;
        }

        // Ctrl+` is kept for compatibility on US layouts.
        // if (keyData == (Keys.Control | Keys.Oem3))
        // {
        //     _terminalPanel.Toggle();
        //     AdjustContentForTerminal();
        //     return true;
        // }

        if (keyData == Keys.F11)
        {
            ToggleFullScreen();
            return true;
        }

        // Title bar only toggle
        if (keyData == (Keys.Control | Keys.Alt | Keys.F))
        {
            ToggleFrameless();
            return true;
        }

        // Chrome visibility only toggle
        if (keyData == (Keys.Control | Keys.Shift | Keys.B))
        {
            ToggleChromeVisibility();
            return true;
        }

        // Full frameless convenience toggle (title bar + chrome)
        if (keyData == (Keys.Control | Keys.Shift | Keys.Alt | Keys.F))
        {
            FullFramelessToggle();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ToggleChromeVisibility()
    {
        // Allow toggling even when frameless
        ApplyChromeVisibility(!_chromePanel.Visible, save: true);
    }

    private void ApplyChromeVisibility(bool visible, bool save)
    {
        _chromePanel.Visible = visible;
        AdjustContentForTerminal();

        if (save)
        {
            var settings = OriginSettings.Instance;
            settings.ChromeVisible = visible;
            settings.Save();
        }
    }

    private void SetContentFramelessState(bool enabled)
    {
        string script = $"window.__originTrueFrameless = {(enabled ? "true" : "false")};";
        foreach (var webView in _tabWebViews.Values)
        {
            if (webView.CoreWebView2 != null)
            {
                _ = webView.ExecuteScriptAsync(script);
            }
        }
    }

    private void AdjustContentForTerminal()
    {
        int terminalHeight = _terminalPanel.Visible ? _terminalPanel.Height : 0;
        int contentTop = _chromePanel.Visible ? _chromePanel.Bottom : 0;
        int contentHeight = Math.Max(0, ClientSize.Height - contentTop - terminalHeight);

        _contentPanel.SetBounds(0, contentTop, ClientSize.Width, contentHeight);

        if (_terminalPanel.Visible && _terminalPanel.Height > 0)
        {
            _terminalPanel.BringToFront();
        }
        else
        {
            _contentPanel.BringToFront();
        }

        SnapActiveWebView();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        const int HTCLIENT = 1;
        const int HTCAPTION = 2;
        const int HTLEFT = 10;
        const int HTRIGHT = 11;
        const int HTTOP = 12;
        const int HTTOPLEFT = 13;
        const int HTTOPRIGHT = 14;
        const int HTBOTTOM = 15;
        const int HTBOTTOMLEFT = 16;
        const int HTBOTTOMRIGHT = 17;

        if (m.Msg == WM_NCHITTEST)
        {
            if (FormBorderStyle == FormBorderStyle.None && !_isFullScreen)
            {
                var pt = GetClientPointFromLParam(m.LParam);
                int grip = FramelessResizeBorder;
                bool left = pt.X >= 0 && pt.X < grip;
                bool right = pt.X <= ClientSize.Width && pt.X >= ClientSize.Width - grip;
                bool top = pt.Y >= 0 && pt.Y < grip;
                bool bottom = pt.Y <= ClientSize.Height && pt.Y >= ClientSize.Height - grip;

                if (top && left) { m.Result = (IntPtr)HTTOPLEFT; return; }
                if (top && right) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                if (bottom && left) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                if (bottom && right) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
                if (left) { m.Result = (IntPtr)HTLEFT; return; }
                if (right) { m.Result = (IntPtr)HTRIGHT; return; }
                if (top) { m.Result = (IntPtr)HTTOP; return; }
                if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }

                if (_chromePanel.Visible && _chromePanel.Bounds.Contains(pt))
                {
                    m.Result = (IntPtr)HTCAPTION;
                    return;
                }

                if (!_chromePanel.Visible && pt.Y >= grip && pt.Y < HiddenChromeDragStripHeight)
                {
                    m.Result = (IntPtr)HTCAPTION;
                    return;
                }

                m.Result = (IntPtr)HTCLIENT;
                return;
            }

            base.WndProc(ref m);
            if (m.Result == (IntPtr)HTCAPTION && _contentPanel != null)
            {
                var pt = GetClientPointFromLParam(m.LParam);
                if (_contentPanel.Bounds.Contains(pt))
                    m.Result = (IntPtr)HTCLIENT;
            }
            return;
        }
        base.WndProc(ref m);
    }

    private Point GetClientPointFromLParam(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        int x = unchecked((short)(value & 0xFFFF));
        int y = unchecked((short)((value >> 16) & 0xFFFF));
        return PointToClient(new Point(x, y));
    }

    private static int GetInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement element) && element.TryGetInt32(out int value)
            ? value
            : 0;
    }

    private void BeginWindowDrag()
    {
        if (FormBorderStyle != FormBorderStyle.None || _isFullScreen) return;

        const int WM_NCLBUTTONDOWN = 0x00A1;
        const int HTCAPTION = 2;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void BeginWindowResize(int hitTest)
    {
        if (FormBorderStyle != FormBorderStyle.None || _isFullScreen) return;
        if (hitTest < 10 || hitTest > 17) return;

        const int WM_NCLBUTTONDOWN = 0x00A1;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)hitTest, IntPtr.Zero);
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
        if (_isFullScreen)
        {
            WindowState = _windowStateBeforeFullScreen;
            FormBorderStyle = _borderStyleBeforeFullScreen;
            _isFullScreen = false;
        }
        else
        {
            _borderStyleBeforeFullScreen = FormBorderStyle;
            _windowStateBeforeFullScreen = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _isFullScreen = true;
        }
    }

    private void ToggleFrameless()
    {
        if (_isFullScreen)
            ToggleFullScreen();

        bool enableFrameless = FormBorderStyle != FormBorderStyle.None;
        Size previousSize = Size;
        Point previousClientScreenLocation = PointToScreen(Point.Empty);

        WindowState = FormWindowState.Normal;
        FormBorderStyle = enableFrameless ? FormBorderStyle.None : FormBorderStyle.Sizable;
        Size = previousSize;

        Point newClientScreenLocation = PointToScreen(Point.Empty);
        Location = new Point(
            Location.X - (newClientScreenLocation.X - previousClientScreenLocation.X),
            Location.Y - (newClientScreenLocation.Y - previousClientScreenLocation.Y));

        var settings = OriginSettings.Instance;
        settings.FramelessWindow = enableFrameless;
        settings.Save();

        SetContentFramelessState(enableFrameless);

        // Refresh layout — chrome visibility is preserved
        AdjustContentForTerminal();
        Invalidate();
        Refresh();
    }

    // Full frameless convenience toggle: hide both title bar and chrome
    private void FullFramelessToggle()
    {
        if (_isFullScreen)
            ToggleFullScreen();

        bool goFullFrameless = FormBorderStyle != FormBorderStyle.None || _chromePanel.Visible;

        if (goFullFrameless)
        {
            // Force frameless
            if (FormBorderStyle != FormBorderStyle.None)
                ToggleFrameless();
            // Hide chrome
            if (_chromePanel.Visible)
                ApplyChromeVisibility(false, save: true);
        }
        else
        {
            // Restore normal: title bar + chrome
            if (FormBorderStyle == FormBorderStyle.None)
                ToggleFrameless();
            if (!_chromePanel.Visible)
                ApplyChromeVisibility(true, save: true);
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
        _terminalPanel?.Dispose();

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