using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;

namespace GlamourSorter;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Glamour Sorter";
    private const string Command = "/gsort";

    private readonly MainWindow _window;
    private readonly StoreExecutor _executor;
    private readonly DresserExtractor _extractor;
    private readonly WindowSystem _windowSystem = new("GlamourSorter");

    public Plugin(IDalamudPluginInterface pi)
    {
        ECommonsMain.Init(pi, this);

        var engine = new GlamourSortEngine(Svc.Data, Svc.Log);
        engine.BuildLookups();
        _executor = new StoreExecutor(Svc.Log, engine);
        _extractor = new DresserExtractor(Svc.Log, engine);

        // Fancy UI shell: the window draws its own title bar, so it is registered with a plain
        // WindowSystem instead of ECommons' ConfigWindow wrapper (this plugin has no settings to
        // persist).
        _window = new MainWindow(engine, _executor, _extractor);
        _windowSystem.AddWindow(_window);
        Svc.PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Glamour Sorter. Open your Glamour Dresser first, then sort.",
        });
    }

    private void OpenMainUi() => _window.IsOpen = true;

    private void OnCommand(string command, string args) => _window.Toggle();

    public void Dispose()
    {
        // Stop the framework-driven loops before ECommons tears down its services.
        _executor.Abort();
        _executor.Dispose();
        _extractor.Abort();
        Svc.PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        _windowSystem.RemoveAllWindows();
        Svc.Commands.RemoveHandler(Command);
        ECommonsMain.Dispose();
    }
}
