using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourSorter;

// DIAGNOSTIC ONLY: logs every ReceiveEvent the outfit-store confirm dialog gets, so we can
// read the exact event type + param the game fires when the user clicks the checkbox by hand.
// Once we know the real values, the checkbox can be driven with Callback.Fire directly and
// this probe is removed.
public sealed unsafe class CallbackProbe : IDisposable
{
    private const string Addon = "MiragePrismPrismSetConvertC";
    private readonly IPluginLog _log;
    private bool _registered;

    public CallbackProbe(IPluginLog log)
    {
        _log = log;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, Addon, OnReceiveEvent);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, Addon, OnReceiveEventPost);
        _registered = true;
        _log.Info("[GlamourSorter] CallbackProbe active on MiragePrismPrismSetConvertC. "
                  + "Open the confirm dialog and click the checkbox by hand; the event will be logged.");
    }

    private void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs e) return;
        // Read the checkbox state at the moment of the event so we can correlate.
        var addon = (AtkUnitBase*)e.Addon.Address;
        var checkedState = "?";
        try
        {
            var node = addon->GetComponentNodeById(4);
            if (node != null)
            {
                var cb = node->GetAsAtkComponentCheckBox();
                if (cb != null) checkedState = cb->IsChecked.ToString();
            }
        }
        catch { /* ignore */ }

        _log.Info($"[GlamourSorter][PROBE] PreReceiveEvent: AtkEventType={e.AtkEventType}, "
                  + $"EventParam={e.EventParam}, checkboxChecked={checkedState}");
    }

    private void OnReceiveEventPost(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs e) return;
        var addon = (AtkUnitBase*)e.Addon.Address;
        var checkedState = "?";
        try
        {
            var node = addon->GetComponentNodeById(4);
            if (node != null)
            {
                var cb = node->GetAsAtkComponentCheckBox();
                if (cb != null) checkedState = cb->IsChecked.ToString();
            }
        }
        catch { /* ignore */ }

        _log.Info($"[GlamourSorter][PROBE] PostReceiveEvent: AtkEventType={e.AtkEventType}, "
                  + $"EventParam={e.EventParam}, checkboxChecked={checkedState}");
    }

    public void Dispose()
    {
        if (!_registered) return;
        Svc.AddonLifecycle.UnregisterListener(OnReceiveEvent);
        Svc.AddonLifecycle.UnregisterListener(OnReceiveEventPost);
        _registered = false;
    }
}
