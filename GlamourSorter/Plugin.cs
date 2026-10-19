using Dalamud.Game.Command;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using ECommons.SimpleGui;

namespace GlamourSorter;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Glamour Sorter";
    private const string Command = "/gsort";

    private readonly MainWindow _window;
    private readonly StoreExecutor _executor;

    public Plugin(IDalamudPluginInterface pi)
    {
        ECommonsMain.Init(pi, this);

        var engine = new GlamourSortEngine(Svc.Data, Svc.Log);
        engine.BuildLookups();
        _executor = new StoreExecutor(Svc.Log, engine);

        _window = new MainWindow(engine, _executor);
        EzConfigGui.Init(_window, windowType: EzConfigGui.WindowType.Main);

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Glamour Sorter. Open your Glamour Dresser first, then sort.",
        });
    }

    private void OnCommand(string command, string args) => EzConfigGui.Toggle();

    public void Dispose()
    {
        // Stop the framework-driven store loop before ECommons tears down its services.
        _executor.Abort();
        Svc.Commands.RemoveHandler(Command);
        ECommonsMain.Dispose();
    }
}
