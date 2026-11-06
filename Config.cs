using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelsOverlay;

// User settings, stored as %APPDATA%\reels_overlay\config.json. Written on first run so the
// user can edit hotkeys; rewritten when the overlay is moved/resized or volume changes.
sealed class Config
{
    public string Url { get; set; } = "https://www.instagram.com/reels/";

    // Mobile user agent so Instagram serves the phone-shaped reels layout.
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";

    // Window bounds in screen pixels. Null X/Y = bottom-right corner of the primary screen.
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 360;
    public int Height { get; set; } = 640;

    public double Volume { get; set; } = 0.3;
    public bool Muted { get; set; } = false;
    public double Zoom { get; set; } = 1.0;

    // GPU rendering for the overlay. Off by default: in a GPU-bound game, the overlay's GPU
    // work during scrolling caused FPS dips; software rendering costs a little CPU instead.
    public bool GpuRendering { get; set; } = false;

    // Hotkey strings: optional modifiers (Ctrl, Alt, Shift, Win) joined by '+' with a
    // System.Windows.Forms.Keys name, e.g. "F2", "Ctrl+Alt+F1", "Alt+PageDown".
    // An empty string disables that action. Registered hotkeys are consumed by Windows,
    // so neither the game nor any other app sees them while Reels Overlay runs.
    // A key without modifiers also fires while Shift is held (sprinting), so Shift+key is
    // taken too. Defaults avoid Ctrl, which is prone in Siege.
    public Dictionary<string, string> Keys { get; set; } = new()
    {
        ["PlayPause"] = "F1",
        ["Next"] = "F2",
        ["Previous"] = "F3",
        ["Mute"] = "F4",
        ["ShowHide"] = "Alt+F1",
        ["VolumeDown"] = "Alt+F2",
        ["VolumeUp"] = "Alt+F3",
        ["EditMode"] = "Alt+Shift+F1",
    };

    // True when no config file existed yet: the overlay starts in edit mode for logging in.
    [JsonIgnore] public bool FirstRun { get; private set; }

    // REELS_OVERLAY_DIR points at a separate settings/profile folder (used for testing).
    // Modifiers for the mouse: with them held, the wheel goes to next/previous reel (wheel
    // down = next) and the middle button toggles like. E.g. "Alt" or "Alt+Shift". Empty
    // disables the mouse hook. Note: Left Alt+Shift (and often Ctrl+Shift) is Windows'
    // keyboard-layout switch, which a wheel tick or click does not cancel.
    public string WheelModifiers { get; set; } = "Alt";

    public static string Dir => Environment.GetEnvironmentVariable("REELS_OVERLAY_DIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "reels_overlay");
    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Config Load()
    {
        Config config;
        try
        {
            config = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOptions) ?? new Config()
                : new Config { FirstRun = true };
        }
        catch (Exception e)
        {
            MessageBox.Show($"Could not read {FilePath}:\n{e.Message}\n\nUsing defaults.", "Reels Overlay");
            return new Config();
        }

        // Fill in actions added after the file was written.
        foreach (var (action, key) in new Config().Keys)
            config.Keys.TryAdd(action, key);
        config.Volume = Math.Clamp(config.Volume, 0, 1);
        config.Save();
        return config;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Settings are a convenience; failing to persist them must not break playback.
        }
    }
}
