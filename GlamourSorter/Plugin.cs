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


    public Plugin(IDalamudPluginInterface pi)
    {
        ECommonsMain.Init(pi, this);

        var engine = new GlamourSortEngine(Svc.Data, Svc.Log);
        engine.BuildLookups();
        var executor = new StoreExecutor(Svc.Log, engine);
        // (checkbox=3, Yes=2, No=1). Those are now hardcoded; probe left disabled.

        _window = new MainWindow(engine, executor);
        EzConfigGui.Init(_window, windowType: EzConfigGui.WindowType.Main);

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Glamour Sorter. Open your Glamour Dresser first, then sort.",
        });
    }

    private void OnCommand(string command, string args) => EzConfigGui.Toggle();

    public void Dispose()
    {

        Svc.Commands.RemoveHandler(Command);
        ECommonsMain.Dispose();
    }
}
