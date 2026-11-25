using HarmonyLib;

namespace SephiriaBackpackOrganizer
{
    [HarmonyPatch(typeof(UI_BaseTooltip), "LateUpdate")]
    internal static class InventoryTooltipPatch
    {
        internal static void Postfix(UI_BaseTooltip __instance)
        {
            if (!__instance.IsOpened || !(__instance.Target is UI_NewInventoryIcon icon) || icon == null)
            {
                return;
            }

            // 背包交换的延迟回调也会重新打开提示，按来源清理而不依赖整理结束时刻。
            var panel = icon.GetComponentInParent<UI_CharacterStatusPanel>(true);
            if (panel != null && !panel.IsOpened)
            {
                __instance.Close();
            }
        }
    }
}
