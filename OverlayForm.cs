using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using static ReelsOverlay.Native;

namespace ReelsOverlay;

// A borderless, always-on-top reels window. Locked (the default) it is click-through and can
// never take focus, so the game keeps keyboard and mouse; everything is driven by global
// hotkeys. Edit mode makes it a normal window for logging in, moving and resizing.
sealed class OverlayForm : Form
{
    const int Border = 6;
    static readonly Color LockedBack = Color.FromArgb(14, 14, 16);
    static readonly Color EditBack = Color.FromArgb(70, 110, 200);

    readonly Config config;
    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    readonly Label bar;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer topmostTimer = new() { Interval = 2000 };
    readonly List<string> inputProblems = new();
    ToolStripMenuItem editItem = null!, hideItem = null!;
    SettingsForm? settings;

    bool ready, paused, hidden, editMode;
    IntPtr returnFocusTo;

    InputHook? input;
    int stepSerial;

    public OverlayForm(Config config)
    {
        this.config = config;
        Text = "Reels Overlay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; // config bounds are raw pixels
        BackColor = LockedBack;
        Bounds = InitialBounds();
        MinimumSize = new Size(180, 240);

        Icon = AppIcon(SystemInformation.IconSize);
        bar = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Visible = false,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(230, 230, 235),
            BackColor = Color.FromArgb(28, 30, 36),
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.SizeAll,
        };
        bar.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
        };

        // Docking runs from the last-added control, so the bar takes the top first.
        Controls.Add(web);
        Controls.Add(bar);

        tray = BuildTray();
        topmostTimer.Tick += (_, _) => KeepOnTop();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Layered (opaque) is what lets WS_EX_TRANSPARENT pass clicks through to the game.
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            if (!editMode) cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    Rectangle InitialBounds()
    {
        var size = new Size(Math.Max(config.Width, 180), Math.Max(config.Height, 240));
        if (config.X is int x && config.Y is int y)
        {
            var saved = new Rectangle(new Point(x, y), size);
            if (Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(saved))) return saved;
        }
        // Right edge, vertically centred: clear of Siege's corner HUD (health, ammo, timer).
        var screen = Screen.PrimaryScreen!.Bounds;
        return new Rectangle(screen.Right - size.Width - 24, screen.Top + (screen.Height - size.Height) / 2,
            size.Width, size.Height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
        ApplyInput();
    }

    // Hands the hook the hotkeys and mouse modifiers from the config. Returns what could not
    // be used; also called after the settings window saves.
    public IReadOnlyList<string> ApplyInput()
    {
        inputProblems.Clear();
        // Callbacks run on the hook thread: BeginInvoke posts to the UI thread and returns at once.
        input ??= new InputHook(dir => BeginInvoke(() => Step(dir)), () => BeginInvoke(ToggleLike));
        if (!input.Installed) inputProblems.Add("keyboard/mouse hook (SetWindowsHookEx failed)");
        input.Bindings = BuildBindings();
        input.KeysEnabled = true;

        var vks = Hotkeys.MouseModifierVks(config.WheelModifiers, out bool valid);
        if (!valid) inputProblems.Add($"mouse modifiers \"{config.WheelModifiers}\" (unknown modifier)");
        // No modifiers = mouse controls off: a bare wheel or middle button would be taken
        // from every program, the game included.
        input.MouseModifierVks = valid ? vks : [];
        input.MouseEnabled = !hidden;

        bar.Text = $"drag to move · edges resize · {Key("EditMode")} locks";
        editItem.Text = MenuText("Edit mode", "EditMode");
        hideItem.Text = MenuText("Show / hide", "ShowHide");
        return inputProblems.ToList();
    }

    // While a settings key box records, the keys must reach it instead of firing here.
    public void PauseHotkeys()
    {
        if (input != null) input.KeysEnabled = false;
    }

    // Back to the saved keys (not whatever the settings window has unsaved).
    public void ResumeHotkeys()
    {
        if (input != null) input.KeysEnabled = true;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            string args = "--autoplay-policy=no-user-gesture-required";
            // Software rendering and decoding by default: the overlay then never touches the
            // GPU, so scrolling (a burst of full-page frames plus a new video decoder) cannot
            // stall the game's frames. The CPU work runs at below-normal priority.
            if (!config.GpuRendering) args += " --disable-gpu";
            // Opt-in remote DevTools for diagnosing Instagram's page (localhost only).
            if (int.TryParse(Environment.GetEnvironmentVariable("REELS_OVERLAY_DEVTOOLS_PORT"), out int port))
                args += $" --remote-debugging-port={port}";
            // After a restart from Settings the previous browser processes may still be
            // shutting down; a profile still in use with other arguments refuses to open.
            CoreWebView2Environment env = null!;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Config.Dir, "WebView2"),
                        new CoreWebView2EnvironmentOptions(args));
                    await web.EnsureCoreWebView2Async(env);
                    break;
                }
                catch (Exception) when (attempt < 20)
                {
                    await Task.Delay(500);
                }
            }
            env.ProcessInfosChanged += (_, _) => LowerBrowserPriority();
            LowerBrowserPriority();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 failed to start:\n{ex.Message}", "Reels Overlay");
            Close();
            return;
        }

        var core = web.CoreWebView2;
        core.Settings.UserAgent = config.UserAgent;
        core.Settings.IsStatusBarEnabled = false;
        // Links that want a new window stay in the overlay.
        core.NewWindowRequested += (_, a) => { a.Handled = true; core.Navigate(a.Uri); };
        core.DOMContentLoaded += (_, _) => Push();
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ReadControlScript());
        // A touch-capable page, like a phone: Instagram's swipe handling depends on it.
        await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled",
            """{"enabled":true,"maxTouchPoints":5}""");
        // The page always believes it has focus. Instagram pauses the reel when its window
        // blurs, which happens every time edit mode hands focus back to the game.
        await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled",
            """{"enabled":true}""");
        web.ZoomFactor = config.Zoom;
        web.ZoomFactorChanged += (_, _) => config.Zoom = web.ZoomFactor;
        ready = true;
        core.Navigate(config.Url);
    }

    // The browser processes (page, GPU, network, audio) run below normal priority, so a burst
    // of work while Instagram loads the next reels never takes CPU time from the game. A reel
    // may stutter under load; the game should not.
    void LowerBrowserPriority()
    {
        try
        {
            foreach (var info in web.CoreWebView2.Environment.GetProcessInfos())
            {
                try
                {
                    using var p = Process.GetProcessById(info.ProcessId);
                    if (p.PriorityClass != ProcessPriorityClass.BelowNormal)
                        p.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception)
                {
                    // The process exited between listing and opening it.
                }
            }
        }
        catch (Exception)
        {
            // WebView2 is shutting down.
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        topmostTimer.Start();
        var help = $"{Key("PlayPause")} pause · {Key("Next")}/{Key("Previous")} next/prev · " +
                   $"{Key("Mute")} mute · {Key("VolumeDown")}/{Key("VolumeUp")} volume · " +
                   $"{Key("ShowHide")} hide · {Key("EditMode")} edit mode" +
                   (input?.MouseModifierVks.Length > 0
                       ? $" · {config.WheelModifiers}+wheel scroll · {config.WheelModifiers}+middle-click like"
                       : "");
        if (config.FirstRun)
        {
            SetEditMode(true);
            help = "Log in to Instagram here, place the window, then press " + Key("EditMode") + " to lock it.";
        }
        if (inputProblems.Count > 0)
            help = "Not registered: " + string.Join(", ", inputProblems) + ". Change them in Settings.";
        if (Environment.GetCommandLineArgs().Contains("--settings")) OpenSettings();
        try { File.WriteAllText(Path.Combine(Config.Dir, "startup.log"), $"{DateTime.Now:s} {help}\n"); } catch { }
        tray.ShowBalloonTip(5000, "Reels Overlay", help, inputProblems.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        topmostTimer.Stop();
        settings?.Close();
        input?.Dispose();
        input = null;
        if (editMode) SaveBounds();
        config.Save();
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE when !editMode:
                m.Result = MA_NOACTIVATE;
                return;
            case WM_NCHITTEST when editMode:
                base.WndProc(ref m);
                if (m.Result == HTCLIENT) m.Result = ResizeHit(m.LParam);
                return;
        }
        base.WndProc(ref m);
    }

    IntPtr ResizeHit(IntPtr lParam)
    {
        long lp = lParam.ToInt64();
        var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
        bool left = p.X < Border, right = p.X >= ClientSize.Width - Border;
        bool top = p.Y < Border, bottom = p.Y >= ClientSize.Height - Border;
        int hit = (top, bottom, left, right) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (true, _, _, true) => HTTOPRIGHT,
            (_, true, true, _) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTTOP,
            (_, true, _, _) => HTBOTTOM,
            (_, _, true, _) => HTLEFT,
            (_, _, _, true) => HTRIGHT,
            _ => HTCLIENT,
        };
        return hit;
    }

    // ---- actions -------------------------------------------------------------------------

    void TogglePaused()
    {
        if (hidden) return;
        paused = !paused;
        Push(paused ? "Paused" : "Playing");
    }

    // A step is a touch swipe sent through DevTools: trusted input that goes straight to the
    // renderer without focus or the OS cursor. Instagram's mobile reels page only advances
    // (and fetches more reels) on swipes; wheel or scripted scrolling just slides the first
    // batch of about seven reels and then stops at its end.
    async void Step(int dir)
    {
        if (!ready || hidden) return;
        paused = false;
        Push(resume: false);
        int serial = ++stepSerial;
        try
        {
            // Instagram's URL names the current reel. A swipe that lands while it is loading
            // the next batch is dropped, so if the reel did not change, swipe once more,
            // unless another press has already taken over.
            string before = await web.CoreWebView2.ExecuteScriptAsync("location.pathname");
            await Swipe(dir);
            await Task.Delay(900);
            if (serial != stepSerial) return;
            if (await web.CoreWebView2.ExecuteScriptAsync("location.pathname") == before)
                await Swipe(dir);
        }
        catch (Exception)
        {
            // Navigation in progress or the page went away; the next press tries again.
        }
    }

    async Task Swipe(int dir)
    {
        string json = await web.CoreWebView2.ExecuteScriptAsync("window.__rc ? __rc.swipeTarget() : null");
        using var doc = JsonDocument.Parse(json);
        var target = doc.RootElement;
        if (target.ValueKind != JsonValueKind.Object) return;
        double x = target.GetProperty("x").GetDouble();
        double y = target.GetProperty("y").GetDouble();
        double h = target.GetProperty("dy").GetDouble();
        // Next: finger moves up from below the middle. Previous: down from above it.
        var swipe = new
        {
            x = (int)Math.Round(x),
            y = (int)Math.Round(y + dir * h * 0.3),
            yDistance = (int)Math.Round(-dir * h * 0.6),
            speed = 3000,
            gestureSourceType = "touch",
        };
        await web.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Input.synthesizeScrollGesture", JsonSerializer.Serialize(swipe));
    }

    // Taps the current reel's like button (a trusted touch tap, like the swipes), so a second
    // press unlikes. The toast reports what Instagram's button says afterwards.
    async void ToggleLike()
    {
        if (!ready || hidden) return;
        try
        {
            var before = await LikeTarget();
            if (before is null)
            {
                Push("No like button here");
                return;
            }
            // A raw touch down/up pair: Chromium turns it into the click the button listens for.
            // (Input.synthesizeTapGesture reaches the button but never produces that click.)
            var point = new { x = Math.Round(before.Value.X), y = Math.Round(before.Value.Y) };
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent",
                JsonSerializer.Serialize(new { type = "touchStart", touchPoints = new[] { point } }));
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent",
                JsonSerializer.Serialize(new { type = "touchEnd", touchPoints = Array.Empty<object>() }));
            await Task.Delay(500);
            var after = await LikeTarget();
            Push(after is null || after.Value.Liked == before.Value.Liked
                ? "Like didn't register"
                : after.Value.Liked ? "♥ Liked" : "Unliked");
        }
        catch (Exception)
        {
            // Navigation in progress or the page went away.
        }
    }

    async Task<(double X, double Y, bool Liked)?> LikeTarget()
    {
        string json = await web.CoreWebView2.ExecuteScriptAsync("window.__rc ? __rc.likeTarget() : null");
        using var doc = JsonDocument.Parse(json);
        var t = doc.RootElement;
        if (t.ValueKind != JsonValueKind.Object) return null;
        return (t.GetProperty("x").GetDouble(), t.GetProperty("y").GetDouble(), t.GetProperty("liked").GetBoolean());
    }

    void ToggleMute()
    {
        config.Muted = !config.Muted;
        config.Save();
        Push(config.Muted ? "Muted" : $"Sound {Percent(config.Volume)}");
    }

    void ChangeVolume(double delta)
    {
        config.Volume = Math.Round(Math.Clamp(config.Volume + delta, 0, 1), 2);
        config.Save();
        Push($"Volume {Percent(config.Volume)}" + (config.Muted ? " (muted)" : ""));
    }

    void ToggleHidden()
    {
        if (editMode) SetEditMode(false);
        hidden = !hidden;
        if (input != null) input.MouseEnabled = !hidden;
        if (hidden)
        {
            Push(); // pause before hiding
            Hide();
        }
        else
        {
            Show();
            KeepOnTop();
            Push();
        }
    }

    void SetEditMode(bool on)
    {
        if (on == editMode) return;
        if (on && hidden) ToggleHidden();
        editMode = on;

        long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        ex &= ~(long)(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        if (!on) ex |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED | (on ? 0u : SWP_NOACTIVATE));

        // The frame and bar grow the window outward, so the page keeps its size. Resizing the
        // page would shift Instagram's reel stack off its scroll position.
        Rectangle inner = on
            ? Bounds
            : new Rectangle(Left + Border, Top + Border + bar.Height,
                Width - 2 * Border, Height - 2 * Border - bar.Height);
        Rectangle outer = new(inner.Left - Border, inner.Top - Border - bar.Height,
            inner.Width + 2 * Border, inner.Height + 2 * Border + bar.Height);
        if (on)
        {
            // Keep the drag bar reachable when the overlay sits at a screen edge; moving the
            // whole window does not resize the page.
            var screen = Screen.FromRectangle(inner).Bounds;
            outer.X = Math.Clamp(outer.X, screen.Left, Math.Max(screen.Left, screen.Right - outer.Width));
            outer.Y = Math.Clamp(outer.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - outer.Height));
        }
        SuspendLayout();
        bar.Visible = on;
        Padding = on ? new Padding(Border) : Padding.Empty;
        BackColor = on ? EditBack : LockedBack;
        Bounds = on ? outer : inner;
        ResumeLayout();

        if (on)
        {
            returnFocusTo = GetForegroundWindow();
            Activate();
            web.Focus();
        }
        else
        {
            SaveBounds();
            // Hand focus back to whatever had it (the game) instead of leaving it on us.
            if (returnFocusTo != IntPtr.Zero && returnFocusTo != Handle) SetForegroundWindow(returnFocusTo);
            returnFocusTo = IntPtr.Zero;
        }
        Push(on ? "Edit mode" : "Locked");
    }

    void SaveBounds()
    {
        config.X = Left;
        config.Y = Top;
        config.Width = Width;
        config.Height = Height;
        config.Save();
    }

    void KeepOnTop()
    {
        // A borderless game that becomes foreground can rise above other topmost windows.
        if (Visible) SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // Sends the whole playback state to the page; the page enforces it on every reel.
    void Push(string? toast = null, bool resume = true)
    {
        if (!ready) return;
        var state = new
        {
            paused = paused || hidden,
            muted = previewMuted ?? config.Muted,
            volume = previewVolume ?? config.Volume,
            interactive = editMode,
        };
        _ = web.CoreWebView2.ExecuteScriptAsync(
            $"window.__rc && __rc.apply({JsonSerializer.Serialize(state)}, {JsonSerializer.Serialize(toast)}, {(resume ? "true" : "false")})");
    }

    // ---- hotkeys & tray ------------------------------------------------------------------

    InputHook.Binding[] BuildBindings()
    {
        var actions = new Dictionary<string, Action>
        {
            ["PlayPause"] = TogglePaused,
            ["Next"] = () => Step(+1),
            ["Previous"] = () => Step(-1),
            ["ShowHide"] = ToggleHidden,
            ["Mute"] = ToggleMute,
            ["EditMode"] = () => SetEditMode(!editMode),
            ["VolumeUp"] = () => ChangeVolume(+0.05),
            ["VolumeDown"] = () => ChangeVolume(-0.05),
        };
        foreach (var name in config.Keys.Keys.Where(k => !actions.ContainsKey(k)))
            inputProblems.Add($"unknown action \"{name}\"");

        var bindings = new List<InputHook.Binding>();
        foreach (var (name, _) in Hotkeys.Actions)
        {
            var action = actions[name];
            string text = Key(name);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!Hotkeys.TryParse(text, out uint mods, out Keys key))
            {
                inputProblems.Add($"{Label(name)} \"{text}\" (unknown key)");
                continue;
            }
            // Fire runs on the hook thread; the action itself runs on the UI thread.
            bindings.Add(new InputHook.Binding(mods, (int)key, () => BeginInvoke(action)));
        }
        return bindings.ToArray();
    }

    static string Label(string action) =>
        Hotkeys.Actions.FirstOrDefault(a => a.Name == action).Label ?? action;

    NotifyIcon BuildTray()
    {
        var menu = new ContextMenuStrip();
        var settingsItem = (ToolStripMenuItem)menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold); // the double-click action
        editItem = (ToolStripMenuItem)menu.Items.Add("Edit mode", null, (_, _) => SetEditMode(!editMode));
        hideItem = (ToolStripMenuItem)menu.Items.Add("Show / hide", null, (_, _) => ToggleHidden());
        menu.Items.Add("Reload page", null, (_, _) => { if (ready) web.Reload(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Close());

        var icon = new NotifyIcon
        {
            Icon = AppIcon(SystemInformation.SmallIconSize),
            Text = "Reels Overlay",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => OpenSettings();
        return icon;
    }

    string MenuText(string text, string action) =>
        Key(action) is { Length: > 0 } key ? $"{text}  ({key})" : text;

    void OpenSettings()
    {
        if (settings is { IsDisposed: false })
        {
            settings.Activate();
            return;
        }
        settings = new SettingsForm(config, this);
        settings.FormClosed += (_, _) => settings = null;
        settings.Show();
        settings.Activate();
    }

    // The settings window's volume and mute, heard while it is open but not saved yet.
    double? previewVolume;
    bool? previewMuted;

    public void PreviewSound(double volume, bool muted)
    {
        previewVolume = volume;
        previewMuted = muted;
        Push(muted ? "Muted" : $"Volume {Percent(volume)}");
    }

    // Back to the saved volume and mute (after Save they are the same values).
    public void EndSoundPreview()
    {
        if (previewVolume is null && previewMuted is null) return;
        previewVolume = null;
        previewMuted = null;
        Push();
    }

    public void Restart()
    {
        config.Save();
        Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--restarted") { UseShellExecute = false });
        Close();
    }

    public static Icon AppIcon(Size size)
    {
        using var stream = typeof(OverlayForm).Assembly.GetManifestResourceStream("app.ico")!;
        return new Icon(stream, size);
    }

    string Key(string action) => config.Keys.TryGetValue(action, out var k) ? k : "";

    static string Percent(double v) => $"{Math.Round(v * 100)}%";

    static string ReadControlScript()
    {
        using var stream = typeof(OverlayForm).Assembly.GetManifestResourceStream("control.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
