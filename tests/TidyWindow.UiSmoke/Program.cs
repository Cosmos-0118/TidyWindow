using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System.Text.Json;
using TidyWindow.Next.Core;
using TidyWindow.Next.Desktop;

namespace TidyWindow.Next.UiSmoke;

public sealed partial class SmokeApp : Application
{
    private Window? window;
    private readonly string report = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "smoke-result.txt");
    public SmokeApp()
    {
        UnhandledException += (_, e) => Finish("FAIL: " + e.Exception, 1);
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new Window { Title = "TidyWindow output regression check" };
        var root = new StackPanel { Padding = new Thickness(20) };
        window.Content = root;
        root.Loaded += async (_, _) =>
        {
            try
            {
                for (var i = 0; i < 12; i++)
                {
                    root.RequestedTheme = i % 2 == 0 ? ElementTheme.Light : ElementTheme.Dark;
                    var output = new CommandOutputView { FontSize = 12, Document = CommandOutputFormatter.Format([new("Downloading 50%\nInstalled successfully\nWARNING: Sample warning\nERROR: Sample error\n")]) };
                    var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    output.Loaded += (_, _) => loaded.TrySetResult();
                    output.Unloaded += (_, _) => unloaded.TrySetResult();
                    root.Children.Add(output);
                    await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    OutputPalette.Refresh();
                    output.Document = CommandOutputFormatter.Format([new("Uninstaller finished\n"), new("Real output error\n", CommandOutputKind.Error)]);
                    root.Children.Remove(output);
                    await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    OutputPalette.Refresh();
                }
                await CheckScoutSetupBinding(root);
                var live = Environment.GetCommandLineArgs().Skip(2).FirstOrDefault() is { } host ? await CheckLiveScout(host) : "";
                Finish("PASS: output control loaded/unloaded 12 times; both themes, live document replacement and shared color notifications passed. Scout setup button passed missing/indexing/ready/unavailable binding transitions. " + live + " No package commands executed.", 0);
            }
            catch (Exception ex) { Finish("FAIL: " + ex, 1); }
        };
        window.Activate();
    }
    private static async Task CheckScoutSetupBinding(StackPanel root)
    {
        var fakeHost = Path.GetTempFileName();
        try
        {
            var engine = new FixtureEngine(); var runner = new FixtureRunner();
            using var model = new ScoutViewModel(new ScoutService(runner, fakeHost, engine), new ActivityLog(), action => action());
            var button = new Button { DataContext = model };
            button.SetBinding(Button.ContentProperty, new Binding { Path = new PropertyPath("SetupLabel"), Mode = BindingMode.OneWay });
            button.SetBinding(UIElement.VisibilityProperty, new Binding { Path = new PropertyPath("SetupVisibility"), Mode = BindingMode.OneWay });
            button.SetBinding(Control.IsEnabledProperty, new Binding { Path = new PropertyPath("CanSetup"), Mode = BindingMode.OneWay });
            root.Children.Add(button);
            await model.CheckBackendAsync();
            Require(button.Content as string == "Enable instant search" && button.Visibility == Visibility.Visible, "missing engine setup binding");
            model.SetInstallationPending(true);
            Require(button.Content as string == "Installing…" && !button.IsEnabled, "queued setup must not offer another installation");
            model.SetInstallationPending(false);
            engine.State = new("Everything.exe", true); runner.Reply = new(false, [], Indexing: true, Error: "Indexing");
            await model.CheckBackendAsync();
            Require(button.Content as string == "Indexing…" && !button.IsEnabled && !model.NeedsInstallation, "installed/indexing binding");
            runner.Reply = new(true, []); await model.CheckBackendAsync();
            Require(model.Indexed && button.Visibility == Visibility.Collapsed, "ready setup button hidden");
            runner.Reply = new(false, [], Error: "Index unavailable"); await model.CheckBackendAsync();
            Require(button.Content as string == "Retry instant search" && button.Visibility == Visibility.Visible && button.IsEnabled && !model.NeedsInstallation, "installed/unavailable retry binding");
            await model.SearchAsync("fixture", null, ScoutItemKind.All, false);
            Require(!model.Error.Contains("Enable instant search", StringComparison.Ordinal), "installed This PC state must not ask for installation");
            root.Children.Remove(button);
        }
        finally { File.Delete(fakeHost); }
    }
    private static async Task<string> CheckLiveScout(string host)
    {
        using var service = new ScoutService(new ProcessRunner(), host);
        var state = await service.CheckBackendAsync();
        for (var attempt = 0; !state.Ready && state.Indexing && attempt < 30; attempt++)
        { await Task.Delay(1000); state = await service.CheckBackendAsync(); }
        Require(state.Installed && state.Ready, "live Everything startup: " + state.Error);
        var result = await service.SearchAsync(new("TidyWindow.slnx", null, ScoutItemKind.Files), true, null, CancellationToken.None);
        Require(result.Error is null && result.Entries.Any(x => x.Name.Equals("TidyWindow.slnx", StringComparison.OrdinalIgnoreCase)), "live This PC filename lookup: " + result.Error);
        return $"Live installed Everything startup and This PC search passed ({result.Entries.Count} matching names).";
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FixtureEngine : IScoutEngine
    {
        public ScoutEngineInstallation State { get; set; } = new(null, false);
        public ScoutEngineInstallation Inspect() => State;
        public void Start(string path) => throw new InvalidOperationException("Fixture must never start a real engine.");
    }
    private sealed class FixtureRunner : ICommandRunner
    {
        public ScoutReply Reply { get; set; } = new(false, []);
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) => Task.FromResult(new CommandResult(0, JsonSerializer.Serialize(Reply), ""));
    }
    private void Finish(string result, int code)
    {
        File.WriteAllText(report, result);
        Environment.ExitCode = code;
        window?.Close();
        Exit();
    }
}
