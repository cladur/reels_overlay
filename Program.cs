namespace ReelsOverlay;

static class Program
{
    [STAThread]
    static void Main()
    {
        // One instance per settings folder: two copies would fight over the WebView2 profile.
        var profile = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(Config.Dir.ToLowerInvariant())))[..16];
        using var mutex = new Mutex(true, $@"Local\ReelsOverlay.{profile}", out bool first);
        // A restart from Settings starts this process before the old one has exited.
        if (!first && Environment.GetCommandLineArgs().Contains("--restarted"))
        {
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            MessageBox.Show("Reels Overlay is already running (see the tray icon).", "Reels Overlay");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new OverlayForm(Config.Load()));
    }
}
