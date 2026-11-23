using System.Runtime.InteropServices;
using Avalonia;

namespace VideoSplitter;

internal static class Program
{
    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            try
            {
                MessageBoxW(0, $"Impossible de démarrer VideoSplitter.\n\n{ex.Message}", "VideoSplitter — Erreur fatale", MbIconError);
            }
            catch
            {
                // Ignorer si l'affichage Win32 echoue.
            }

            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
