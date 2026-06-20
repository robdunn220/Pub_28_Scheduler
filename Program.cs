using Avalonia;
using System;

namespace PublicHouse28Scheduler;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            SelfTest.Run();
            return;
        }

        if (args.Contains("--ai-test"))
        {
            AiTest.Run(args);
            return;
        }

        if (args.Contains("--date-test"))
        {
            AiTest.RunDateTests();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
