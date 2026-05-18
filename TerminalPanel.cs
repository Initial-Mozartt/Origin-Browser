using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OriginBrowser;

public class TerminalPanel : Panel
{
    private sealed class ShellSession
    {
        public required string Id { get; init; }
        public required string ShellKind { get; init; }
        public required Process Process { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
    }

    private sealed class ShellProfile
    {
        public required string Kind { get; init; }
        public required string Title { get; init; }
        public required string FileName { get; init; }
        public required string Arguments { get; init; }
    }

    private const int HandleHeight = 8;
    private const int MinTerminalHeight = 140;
    private const int DefaultTerminalHeight = 320;
    private const int AnimationStep = 36;

    private readonly Panel _dragHandle;
    private readonly WebView2 _terminalView;
    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly Dictionary<string, ShellSession> _sessions = new();

    private bool _isOpen;
    private bool _isDragging;
    private bool _terminalReady;
    private int _dragStartY;
    private int _dragStartHeight;
    private int _targetHeight;
    private int _preferredHeight = DefaultTerminalHeight;
    private string? _activeSessionId;

    private readonly Color _backgroundColor = Color.FromArgb(30, 30, 30);

    public event EventHandler? TerminalHeightChanged;

    public bool IsOpen => _isOpen;

    public TerminalPanel()
    {
        BackColor = _backgroundColor;
        BorderStyle = BorderStyle.None;
        Height = 0;
        Visible = false;
        Margin = Padding.Empty;
        Padding = Padding.Empty;

        _dragHandle = new Panel
        {
            Dock = DockStyle.Top,
            Height = HandleHeight,
            BackColor = Color.FromArgb(45, 45, 45),
            Cursor = Cursors.SizeNS
        };
        _dragHandle.Paint += DragHandle_Paint;
        _dragHandle.MouseDown += DragHandle_MouseDown;
        _dragHandle.MouseMove += DragHandle_MouseMove;
        _dragHandle.MouseUp += DragHandle_MouseUp;

        _terminalView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = _backgroundColor,
            Margin = Padding.Empty
        };
        _terminalView.WebMessageReceived += TerminalView_WebMessageReceived;

        Controls.Add(_terminalView);
        Controls.Add(_dragHandle);

        _animationTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _animationTimer.Tick += AnimationTimer_Tick;

        _ = InitializeTerminalAsync();
    }

    public void Toggle()
    {
        if (_isOpen)
        {
            HideTerminal();
        }
        else
        {
            ShowTerminal();
        }
    }

    public void ShowTerminal()
    {
        if (_isOpen)
        {
            FocusInput();
            return;
        }

        _isOpen = true;
        Visible = true;
        BringToFront();
        EnsureDefaultSession();
        AnimateTo(_preferredHeight);
        FocusInput();
    }

    public void HideTerminal()
    {
        if (!_isOpen) return;

        _isOpen = false;
        _preferredHeight = Math.Max(MinTerminalHeight, Height);
        AnimateTo(0);
    }

    public void FocusInput()
    {
        if (!Visible) return;

        _terminalView.Focus();
        if (_terminalReady)
        {
            PostTerminalMessage(new { type = "focus", tabId = _activeSessionId });
        }
    }

    private async Task InitializeTerminalAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(Application.StartupPath, "OriginUserData", "Terminal");
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _terminalView.EnsureCoreWebView2Async(environment);

            _terminalView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _terminalView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _terminalView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "origin-terminal.local",
                Path.Combine(Application.StartupPath, "terminal-ui"),
                CoreWebView2HostResourceAccessKind.Allow);

            _terminalView.Source = new Uri("https://origin-terminal.local/terminal.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize terminal view: {ex.Message}", "Origin Terminal",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void TerminalView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = document.RootElement;
            string type = GetString(root, "type");

            switch (type)
            {
                case "ready":
                    _terminalReady = true;
                    RestoreShellSessions(root);
                    FocusInput();
                    break;

                case "createTab":
                    CreateShellSession(GetString(root, "shell", "powershell"), activate: true);
                    break;

                case "switchTab":
                    SetActiveSession(GetString(root, "tabId"));
                    break;

                case "closeTab":
                    CloseShellSession(GetString(root, "tabId"));
                    break;

                case "restartTab":
                    RestartShellSession(GetString(root, "tabId"));
                    break;

                case "input":
                    SendInputToShell(GetString(root, "tabId"), GetString(root, "data"));
                    break;

                case "interrupt":
                    SendInputToShell(GetString(root, "tabId"), "\u0003");
                    break;
            }
        }
        catch (Exception ex)
        {
            PostTerminalMessage(new { type = "bridgeError", data = $"[terminal bridge error] {ex.Message}" });
        }
    }

    private void EnsureDefaultSession()
    {
        if (!_terminalReady || _sessions.Count > 0) return;
        CreateShellSession("powershell", activate: true);
    }

    private void CreateShellSession(string shellKind, bool activate)
    {
        string id = Guid.NewGuid().ToString("N");
        ShellProfile profile;

        try
        {
            profile = ResolveShellProfile(shellKind);
        }
        catch (Exception ex)
        {
            PostTerminalMessage(new { type = "bridgeError", data = $"Failed to start {shellKind}: {ex.Message}" });
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = profile.FileName,
            Arguments = profile.Arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var cancellation = new CancellationTokenSource();
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        var session = new ShellSession
        {
            Id = id,
            ShellKind = profile.Kind,
            Process = process,
            Cancellation = cancellation
        };

        process.Exited += (_, _) =>
        {
            PostTerminalMessage(new { type = "output", tabId = id, data = "\r\n[process exited]\r\n" });
        };

        try
        {
            process.Start();
            process.StandardInput.AutoFlush = true;
            _sessions[id] = session;

            PostTerminalMessage(new
            {
                type = "tabCreated",
                tabId = id,
                title = profile.Title,
                shell = profile.Kind,
                active = activate
            });

            if (activate)
            {
                SetActiveSession(id);
            }

            _ = ReadShellStreamAsync(session, process.StandardOutput.BaseStream, cancellation.Token);
            _ = ReadShellStreamAsync(session, process.StandardError.BaseStream, cancellation.Token);
        }
        catch (Exception ex)
        {
            cancellation.Dispose();
            process.Dispose();
            PostTerminalMessage(new { type = "bridgeError", data = $"Failed to start {profile.Title}: {ex.Message}" });
        }
    }

    private async Task ReadShellStreamAsync(ShellSession session, Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0) break;

                int charCount = decoder.GetChars(buffer, 0, bytesRead, chars, 0);
                if (charCount > 0)
                {
                    PostTerminalMessage(new
                    {
                        type = "output",
                        tabId = session.Id,
                        data = new string(chars, 0, charCount)
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            PostTerminalMessage(new { type = "output", tabId = session.Id, data = $"\r\n[terminal read error] {ex.Message}\r\n" });
        }
    }

    private void SendInputToShell(string tabId, string data)
    {
        if (!_sessions.TryGetValue(tabId, out ShellSession? session)) return;

        try
        {
            if (session.Process.HasExited) return;

            session.Process.StandardInput.Write(data);
            session.Process.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            PostTerminalMessage(new { type = "output", tabId, data = $"\r\n[terminal input error] {ex.Message}\r\n" });
        }
    }

    private void SetActiveSession(string tabId)
    {
        if (!_sessions.ContainsKey(tabId)) return;

        _activeSessionId = tabId;
        PostTerminalMessage(new { type = "tabActivated", tabId });
        FocusInput();
    }

    private void CloseShellSession(string tabId, bool ensureReplacement = true)
    {
        if (!_sessions.TryGetValue(tabId, out ShellSession? session)) return;

        StopShell(session);
        _sessions.Remove(tabId);
        PostTerminalMessage(new { type = "tabClosed", tabId });

        if (_activeSessionId == tabId)
        {
            _activeSessionId = null;
            foreach (string nextId in _sessions.Keys)
            {
                SetActiveSession(nextId);
                break;
            }
        }

        if (ensureReplacement && _sessions.Count == 0 && _terminalReady)
        {
            CreateShellSession("powershell", activate: true);
        }
    }

    private void RestartShellSession(string tabId)
    {
        if (!_sessions.TryGetValue(tabId, out ShellSession? session)) return;

        string shellKind = session.ShellKind;
        bool wasActive = _activeSessionId == tabId;
        CloseShellSession(tabId, ensureReplacement: false);
        CreateShellSession(shellKind, activate: wasActive);
    }

    private void RestoreShellSessions(JsonElement root)
    {
        if (!_terminalReady || _sessions.Count > 0) return;

        if (!root.TryGetProperty("profiles", out JsonElement profilesElement) ||
            profilesElement.ValueKind != JsonValueKind.Array ||
            profilesElement.GetArrayLength() == 0)
        {
            EnsureDefaultSession();
            return;
        }

        int activeIndex = root.TryGetProperty("activeIndex", out JsonElement activeElement) && activeElement.TryGetInt32(out int index)
            ? index
            : 0;

        int profileIndex = 0;
        foreach (JsonElement profileElement in profilesElement.EnumerateArray())
        {
            string shellKind = profileElement.ValueKind == JsonValueKind.String
                ? profileElement.GetString() ?? "powershell"
                : "powershell";

            CreateShellSession(shellKind, activate: profileIndex == activeIndex);
            profileIndex++;
        }

        if (_sessions.Count == 0)
        {
            EnsureDefaultSession();
        }
        else if (_activeSessionId == null)
        {
            foreach (string nextId in _sessions.Keys)
            {
                SetActiveSession(nextId);
                break;
            }
        }
    }

    private static ShellProfile ResolveShellProfile(string shellKind)
    {
        shellKind = shellKind.ToLowerInvariant();

        if (shellKind == "cmd")
        {
            return new ShellProfile
            {
                Kind = "cmd",
                Title = "CMD",
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                Arguments = "/Q"
            };
        }

        if (shellKind == "wsl")
        {
            string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (!File.Exists(wsl))
            {
                throw new FileNotFoundException("wsl.exe was not found.");
            }

            return new ShellProfile
            {
                Kind = "wsl",
                Title = "WSL",
                FileName = wsl,
                Arguments = ""
            };
        }

        if (shellKind == "gitbash")
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return new ShellProfile
                    {
                        Kind = "gitbash",
                        Title = "Git Bash",
                        FileName = candidate,
                        Arguments = "--login -i"
                    };
                }
            }

            throw new FileNotFoundException("Git Bash was not found.");
        }

        string pwsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell",
            "7",
            "pwsh.exe");

        if (File.Exists(pwsh))
        {
            return new ShellProfile
            {
                Kind = "powershell",
                Title = "PowerShell",
                FileName = pwsh,
                Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass"
            };
        }

        string windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        if (File.Exists(windowsPowerShell))
        {
            return new ShellProfile
            {
                Kind = "powershell",
                Title = "PowerShell",
                FileName = windowsPowerShell,
                Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass"
            };
        }

        return ResolveShellProfile("cmd");
    }

    private void StopShell(ShellSession session)
    {
        session.Cancellation.Cancel();

        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.StandardInput.WriteLine("exit");
                if (!session.Process.WaitForExit(500))
                {
                    session.Process.Kill();
                }
            }
        }
        catch
        {
        }

        session.Process.Dispose();
        session.Cancellation.Dispose();
    }

    private void StopAllShells()
    {
        foreach (ShellSession session in _sessions.Values)
        {
            StopShell(session);
        }

        _sessions.Clear();
        _activeSessionId = null;
    }

    private static string GetString(JsonElement root, string propertyName, string fallback = "")
    {
        return root.TryGetProperty(propertyName, out JsonElement element)
            ? element.GetString() ?? fallback
            : fallback;
    }

    private void PostTerminalMessage(object message)
    {
        if (IsDisposed || _terminalView == null || _terminalView.IsDisposed || _terminalView.CoreWebView2 == null) return;

        if (InvokeRequired)
        {
            BeginInvoke(() => PostTerminalMessage(message));
            return;
        }

        _terminalView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void AnimateTo(int height)
    {
        _targetHeight = Math.Max(0, height);
        _animationTimer.Start();
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (Height == _targetHeight)
        {
            _animationTimer.Stop();
            if (!_isOpen && Height == 0)
            {
                Visible = false;
                TerminalHeightChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        int direction = Height < _targetHeight ? 1 : -1;
        int nextHeight = Height + AnimationStep * direction;

        if ((direction > 0 && nextHeight > _targetHeight) ||
            (direction < 0 && nextHeight < _targetHeight))
        {
            nextHeight = _targetHeight;
        }

        Height = nextHeight;
        TerminalHeightChanged?.Invoke(this, EventArgs.Empty);
        PostTerminalMessage(new { type = "fit" });
    }

    private void DragHandle_Paint(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(90, 90, 90));
        int y = HandleHeight / 2;
        e.Graphics.DrawLine(pen, Width / 2 - 32, y, Width / 2 + 32, y);
    }

    private void DragHandle_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        _isDragging = true;
        _dragStartY = Cursor.Position.Y;
        _dragStartHeight = Height;
        _animationTimer.Stop();
        _dragHandle.Capture = true;
    }

    private void DragHandle_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        if ((Control.MouseButtons & MouseButtons.Left) == 0)
        {
            EndDrag();
            return;
        }

        int maxHeight = Parent == null
            ? 700
            : Math.Max(MinTerminalHeight, Parent.ClientSize.Height - 120);
        int delta = _dragStartY - Cursor.Position.Y;
        int newHeight = Math.Max(MinTerminalHeight, Math.Min(maxHeight, _dragStartHeight + delta));

        if (Height == newHeight) return;

        Height = newHeight;
        _preferredHeight = newHeight;
        TerminalHeightChanged?.Invoke(this, EventArgs.Empty);
        PostTerminalMessage(new { type = "fit" });
    }

    private void DragHandle_MouseUp(object? sender, MouseEventArgs e)
    {
        EndDrag();
    }

    private void EndDrag()
    {
        _isDragging = false;
        _dragHandle.Capture = false;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _dragHandle?.Invalidate();
        TerminalHeightChanged?.Invoke(this, EventArgs.Empty);
        PostTerminalMessage(new { type = "fit" });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
            StopAllShells();
            _terminalView.Dispose();
        }

        base.Dispose(disposing);
    }
}
