using System;

// Minimal UI models; these tests do not replace in-game acceptance.
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string method) { }
    }
}

internal interface IUITooltipOpener
{
    bool Showing { get; set; }
    UI_BaseTooltip LastTooltip { get; set; }
}

internal class TooltipOpener : IUITooltipOpener
{
    public bool Showing { get; set; }
    public UI_BaseTooltip LastTooltip { get; set; }
}

internal sealed class UI_CharacterStatusPanel
{
    public bool IsOpened;
}

internal sealed class UI_NewInventoryIcon : TooltipOpener
{
    public UI_CharacterStatusPanel Panel;

    public T GetComponentInParent<T>(bool includeInactive) where T : class
    {
        return Panel != null && (includeInactive || Panel.IsOpened) ? Panel as T : null;
    }
}

internal sealed class UI_BaseTooltip
{
    public bool IsOpened;
    public IUITooltipOpener Target;

    public void Open(IUITooltipOpener target)
    {
        Close();
        IsOpened = true;
        Target = target;
        if (target != null)
        {
            target.Showing = true;
            target.LastTooltip = this;
        }
    }

    public void Close()
    {
        IsOpened = false;
        if (Target != null)
        {
            Target.Showing = false;
            Target.LastTooltip = null;
        }
        Target = null;
    }
}
