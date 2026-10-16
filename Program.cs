using Avalonia;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (UpdateService.TryApplyUpdateCommand(args))
            return;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText("startup_crash.log", ex.ToString()); } catch { }
            Console.WriteLine(ex);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
