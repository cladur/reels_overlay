using System.Runtime.InteropServices;
using static ReelsOverlay.Native;

namespace ReelsOverlay;

// Edits the config: hotkeys (press-to-record), mouse modifiers, sound and GPU rendering.
// The overlay's hotkeys keep working while this window is open, except while a key box has
// focus: then they are released, so pressing a combination records it instead of triggering
// it. Save applies everything immediately; a GpuRendering change restarts the app.
sealed class SettingsForm : Form
{
    static readonly Color Back = Color.FromArgb(24, 25, 30);
    static readonly Color Field = Color.FromArgb(36, 38, 46);
    static readonly Color Line = Color.FromArgb(62, 65, 76);
    static readonly Color Text1 = Color.FromArgb(228, 229, 234);
    static readonly Color Text2 = Color.FromArgb(150, 153, 165);
    static readonly Color Accent = Color.FromArgb(76, 118, 230);
    static readonly Color Warn = Color.FromArgb(240, 170, 90);

    readonly Config config;
    readonly OverlayForm overlay;
    readonly Dictionary<string, KeyBox> keyBoxes = new();
    readonly CheckBox mAlt, mCtrl, mShift, mWin, muted, gpu;
    readonly TrackBar volume;
    readonly Label volumeValue, mouseNote, status;

    public SettingsForm(Config config, OverlayForm overlay)
    {
        this.config = config;
        this.overlay = overlay;
        Text = "Reels Overlay settings";
        Icon = OverlayForm.AppIcon(SystemInformation.IconSize);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Back;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 9.75f);
        KeyPreview = false;

        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(18, 12, 18, 14),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        Controls.Add(grid);

        // ---- keys
        Header(grid, "Keys");
        Note(grid, "Click a box and press the combination. Backspace clears it, Esc cancels.\n" +
                   "Keys without Ctrl/Alt/Win also work while Shift is held (sprinting).");
        foreach (var (name, label) in Hotkeys.Actions)
        {
            var box = new KeyBox(config.Keys.TryGetValue(name, out var k) ? k : "")
            {
                Width = 220,
                BackColor = Field,
                ForeColor = Text1,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(3, 3, 3, 3),
            };
            box.Recorded += (_, _) => OnRecorded(name, box);
            // Only a box that is recording silences the hotkeys; they work otherwise.
            box.GotFocus += (_, _) => overlay.PauseHotkeys();
            box.LostFocus += (_, _) => overlay.ResumeHotkeys();
            keyBoxes[name] = box;
            Row(grid, label, box);
        }

        // ---- mouse
        Header(grid, "Mouse");
        Note(grid, "Hold these, then use the wheel for next / previous reel or middle-click to like.\n" +
                   "None ticked turns the mouse controls off.");
        var mods = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var held = Hotkeys.MouseModifierVks(config.WheelModifiers, out _);
        mAlt = Check("Alt", held.Contains(VK_MENU));
        mCtrl = Check("Ctrl", held.Contains(VK_CONTROL));
        mShift = Check("Shift", held.Contains(VK_SHIFT));
        mWin = Check("Win", held.Contains(VK_LWIN));
        mods.Controls.AddRange([mAlt, mCtrl, mShift, mWin]);
        Row(grid, "Hold", mods);
        mouseNote = new Label { AutoSize = true, ForeColor = Warn, MaximumSize = new Size(460, 0), Margin = new Padding(3, 0, 3, 4) };
        grid.Controls.Add(mouseNote);
        grid.SetColumnSpan(mouseNote, 2);
        foreach (var c in new[] { mAlt, mCtrl, mShift, mWin }) c.CheckedChanged += (_, _) => UpdateMouseNote();
        UpdateMouseNote();

        // ---- sound
        Header(grid, "Sound");
        var volRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        volume = new TrackBar
        {
            Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10,
            Value = (int)Math.Round(config.Volume * 100), Width = 170, BackColor = Back,
            TickStyle = TickStyle.None, AutoSize = false, Height = 28,
        };
        volumeValue = new Label { AutoSize = true, ForeColor = Text1, Margin = new Padding(4, 6, 0, 0) };
        volumeValue.Text = $"{volume.Value}%";
        volRow.Controls.AddRange([volume, volumeValue]);
        Row(grid, "Volume", volRow);
        muted = Check("Muted", config.Muted);
        Row(grid, "", muted);
        // Heard immediately; dropped again unless saved.
        volume.ValueChanged += (_, _) =>
        {
            volumeValue.Text = $"{volume.Value}%";
            overlay.PreviewSound(volume.Value / 100.0, muted.Checked);
        };
        muted.CheckedChanged += (_, _) => overlay.PreviewSound(volume.Value / 100.0, muted.Checked);

        // ---- display
        Header(grid, "Display");
        gpu = Check("Render with the GPU", config.GpuRendering);
        Row(grid, "", gpu);
        Note(grid, "Off avoids FPS dips in games while reels scroll. Changing it restarts Reels Overlay.");

        // ---- buttons
        status = new Label { AutoSize = true, ForeColor = Warn, MaximumSize = new Size(460, 0), Margin = new Padding(3, 10, 3, 0) };
        grid.Controls.Add(status);
        grid.SetColumnSpan(status, 2);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 0),
        };
        var save = Button("Save", Accent);
        var cancel = Button("Cancel", Field);
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        buttons.Controls.AddRange([save, cancel]);
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);
        AcceptButton = null; // Enter must be recordable in a key box
        CancelButton = null;

        FormClosed += (_, _) =>
        {
            overlay.ResumeHotkeys();
            overlay.EndSoundPreview(); // back to the saved volume and mute
        };
    }

    // Nothing focused at first: a focused key box would already be recording.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1; // dark title bar
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    void OnRecorded(string name, KeyBox box)
    {
        status.Text = "";
        if (box.Value.Length == 0) return;
        Hotkeys.TryParse(box.Value, out uint mods, out Keys key);
        if (Hotkeys.Problem(mods, key) is string problem)
        {
            status.Text = problem + ".";
            box.Revert();
            return;
        }
        // One combination, one action: take it away from whichever action had it.
        foreach (var (other, otherBox) in keyBoxes)
        {
            if (other == name || !string.Equals(otherBox.Value, box.Value, StringComparison.OrdinalIgnoreCase)) continue;
            otherBox.SetValue("");
            status.Text = $"{box.Value} moved here from \"{LabelOf(other)}\".";
        }
    }

    void UpdateMouseNote()
    {
        mouseNote.Text = (mAlt.Checked, mShift.Checked, mCtrl.Checked) switch
        {
            (true, true, _) => "Left Alt+Shift also switches the Windows keyboard layout.",
            (_, _, true) => "Ctrl is prone in Siege.",
            _ => "",
        };
        mouseNote.Visible = mouseNote.Text.Length > 0;
    }

    void Save()
    {
        bool gpuChanged = gpu.Checked != config.GpuRendering;
        foreach (var (name, box) in keyBoxes) config.Keys[name] = box.Value;
        var mods = new List<string>();
        if (mCtrl.Checked) mods.Add("Ctrl");
        if (mAlt.Checked) mods.Add("Alt");
        if (mShift.Checked) mods.Add("Shift");
        if (mWin.Checked) mods.Add("Win");
        config.WheelModifiers = string.Join("+", mods);
        config.Volume = volume.Value / 100.0;
        config.Muted = muted.Checked;
        config.GpuRendering = gpu.Checked;
        config.Save();

        var problems = overlay.ApplyInput();
        overlay.EndSoundPreview(); // the saved values are now the previewed ones
        if (gpuChanged)
        {
            Close();
            overlay.Restart();
            return;
        }
        if (problems.Count > 0)
        {
            // Stay open so the user can pick other keys; the working ones are live meanwhile.
            status.Text = "Saved, but these could not be used: " + string.Join(", ", problems) + ".";
            return;
        }
        Close();
    }

    static string LabelOf(string name) => Hotkeys.Actions.First(a => a.Name == name).Label;

    // ---- layout helpers

    static void Header(TableLayoutPanel grid, string text)
    {
        var l = new Label
        {
            Text = text, AutoSize = true, ForeColor = Text1,
            Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 12, 0, 2),
        };
        grid.Controls.Add(l);
        grid.SetColumnSpan(l, 2);
    }

    static void Note(TableLayoutPanel grid, string text)
    {
        var l = new Label { Text = text, AutoSize = true, ForeColor = Text2, MaximumSize = new Size(470, 0), Margin = new Padding(0, 0, 0, 6) };
        grid.Controls.Add(l);
        grid.SetColumnSpan(l, 2);
    }

    static void Row(TableLayoutPanel grid, string label, Control control)
    {
        grid.Controls.Add(new Label
        {
            Text = label, AutoSize = true, ForeColor = Text1, Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 16, 6),
        });
        grid.Controls.Add(control);
    }

    static CheckBox Check(string text, bool on) => new()
    {
        Text = text, Checked = on, AutoSize = true, ForeColor = Text1, FlatStyle = FlatStyle.Flat,
        Margin = new Padding(3, 5, 12, 5),
    };

    static Button Button(string text, Color back)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(90, 32), BackColor = back, ForeColor = Text1,
            FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 0, 0, 0), UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = Line;
        return b;
    }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

// A read-only box that records the next key combination pressed while it has focus.
sealed class KeyBox : TextBox
{
    string value, before;
    public event EventHandler? Recorded;

    public KeyBox(string value)
    {
        this.value = before = value;
        ReadOnly = true;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
        ShowValue();
    }

    public string Value => value;

    public void SetValue(string v)
    {
        value = before = v;
        ShowValue();
    }

    public void Revert()
    {
        value = before;
        ShowValue();
    }

    void ShowValue() => Text = value.Length > 0 ? value : "—";

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        before = value;
        Text = "press keys…";
        HideCaret(Handle);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        ShowValue();
    }

    // Every key goes here first, including Alt combinations, F10 and Tab.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        uint mods = 0;
        if ((keyData & Keys.Control) != 0) mods |= MOD_CONTROL;
        if ((keyData & Keys.Alt) != 0) mods |= MOD_ALT;
        if ((keyData & Keys.Shift) != 0) mods |= MOD_SHIFT;
        if ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) mods |= MOD_WIN;

        if (Hotkeys.IsModifierKey(key))
        {
            Text = Hotkeys.Format(mods, Keys.None) + "+…";
            return true;
        }
        if (mods == 0 && key == Keys.Escape)
        {
            value = before;
            StopRecording();
            return true;
        }
        if (mods == 0 && key is Keys.Back or Keys.Delete)
            value = "";
        else
            value = Hotkeys.Format(mods, key);
        ShowValue();
        Recorded?.Invoke(this, EventArgs.Empty);
        before = value;
        StopRecording();
        return true;
    }

    // Focus leaves the box (to the form itself), so the next box does not start recording.
    void StopRecording()
    {
        if (FindForm() is Form form) form.ActiveControl = null;
        ShowValue();
    }

    // Releasing a modifier without a key shows the stored value again.
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (Focused && !e.Alt && !e.Control && !e.Shift) Text = "press keys…";
    }

    [DllImport("user32.dll")]
    static extern bool HideCaret(IntPtr hWnd);
}
