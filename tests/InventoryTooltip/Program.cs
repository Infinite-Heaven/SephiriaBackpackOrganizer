using System;
using SephiriaBackpackOrganizer;

internal static class Program
{
    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Main()
    {
        CheckTooltipCleanup();
        Console.WriteLine("PASS: hidden inventory tooltip cleanup");
    }

    private static void CheckTooltipCleanup()
    {
        var panel = new UI_CharacterStatusPanel();
        var icon = new UI_NewInventoryIcon { Panel = panel };
        var tooltip = new UI_BaseTooltip();

        for (int update = 0; update < 3; update++)
        {
            tooltip.Open(icon);
            InventoryTooltipPatch.Postfix(tooltip);
            Expect(!tooltip.IsOpened && tooltip.Target == null && !icon.Showing && icon.LastTooltip == null,
                "A closed bag must not leave an item preview, including delayed refreshes after sorting.");
        }

        panel.IsOpened = true;
        tooltip.Open(icon);
        InventoryTooltipPatch.Postfix(tooltip);
        Expect(tooltip.IsOpened && icon.Showing && icon.LastTooltip == tooltip,
            "Opening the bag during sorting must preserve normal item previews.");

        panel.IsOpened = false;
        InventoryTooltipPatch.Postfix(tooltip);
        Expect(!tooltip.IsOpened, "Closing the bag during sorting must remove its item preview.");

        var shopIcon = new UI_NewInventoryIcon();
        tooltip.Open(shopIcon);
        InventoryTooltipPatch.Postfix(tooltip);
        Expect(tooltip.IsOpened && tooltip.Target == shopIcon,
            "An inventory icon outside the character bag must keep its preview.");

        var groundItem = new TooltipOpener();
        tooltip.Open(groundItem);
        InventoryTooltipPatch.Postfix(tooltip);
        Expect(tooltip.IsOpened && tooltip.Target == groundItem,
            "Ground-item and other non-inventory previews must remain open.");

        tooltip.Open(null);
        InventoryTooltipPatch.Postfix(tooltip);
        Expect(tooltip.IsOpened, "Unrelated previews without an inventory source must not be closed.");
    }
}
