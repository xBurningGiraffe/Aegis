namespace Aegis;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Prevent multiple instances
        using var mutex = new Mutex(true, @"Global\Aegis_SingleInstance_v1", out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                "Aegis is already running.\n\nLook for the icon in the system tray (bottom-right corner of the taskbar).",
                "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) =>
            MessageBox.Show($"Unhandled error:\n{e.Exception.Message}", "Aegis Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new TrayApplicationContext());
    }
}
