using System.Diagnostics;

namespace Editor.App;

public partial class App : Application
{
    private Window? _window;

    static App()
    {
        _ = StartupClock.StartedAt;
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            args.Handled = false;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var benchmarkPath = BenchmarkOutputPath();
        _window = new MainWindow(benchmarkPath);
        _window.Closed += (_, _) => Exit();
        _window.Activate();
    }

    private static string? BenchmarkOutputPath()
    {
        var argv = Environment.GetCommandLineArgs();
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (string.Equals(argv[i], "--startup-benchmark", StringComparison.OrdinalIgnoreCase))
                return argv[i + 1];
        }
        return null;
    }
}

/// <summary>Timestamp captured as early as the application type is initialized.</summary>
public static class StartupClock
{
    public static long StartedAt { get; } = Stopwatch.GetTimestamp();

    public static double ElapsedMilliseconds => Stopwatch.GetElapsedTime(StartedAt).TotalMilliseconds;
}
