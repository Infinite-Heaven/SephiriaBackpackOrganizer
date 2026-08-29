using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SephiriaBackpackOrganizer
{
    /// <summary>
    /// 当前游戏会话的手动优先级。每件物品独立循环：未点击=默认(0) → P1 → P2 → P3 → P4 → 未点击。
    /// 手动优先级（1~4）直接映射到插件优先级系统（priority 1~4），覆盖稀有度默认排序，
    /// 因此排序时完全复用原有的"优先级→排序/评分"逻辑，不需要额外提权加分。
    /// 只在点击、会话切换和整理快照构建时更新，不做每帧配置/背包扫描。
    /// </summary>
    internal static class ManualPriorityManager
    {
        private const int MaxRank = 4;

        /// <summary>instanceID -> 手动优先级（1~4；不在字典里=默认优先级）。</summary>
        private static readonly Dictionary<int, int> RankByInstance = new Dictionary<int, int>();
        internal static int Count => RankByInstance.Count;

        /// <summary>点击一次循环推进：0(默认)→1(P1)→2(P2)→3(P3)→4(P4)→0(取消)。返回新优先级。</summary>
        internal static int Toggle(int instanceId)
        {
            int next = GetRank(instanceId) + 1;
            if (next > MaxRank)
            {
                RankByInstance.Remove(instanceId);
                return 0;
            }
            RankByInstance[instanceId] = next;
            return next;
        }

        internal static int GetRank(int instanceId)
        {
            return RankByInstance.TryGetValue(instanceId, out int rank) ? rank : 0;
        }

        internal static Dictionary<int, int> PruneAndSnapshot(HashSet<int> presentCharmIds)
        {
            if (presentCharmIds != null)
            {
                var stale = new List<int>();
                foreach (KeyValuePair<int, int> kv in RankByInstance)
                {
                    if (!presentCharmIds.Contains(kv.Key))
                    {
                        stale.Add(kv.Key);
                    }
                }
                foreach (int id in stale)
                {
                    RankByInstance.Remove(id);
                }
            }

            return new Dictionary<int, int>(RankByInstance);
        }

        internal static void Clear()
        {
            if (RankByInstance.Count == 0)
            {
                return;
            }
            RankByInstance.Clear();
            RefreshVisibleBadges();
        }

        internal static int RefreshVisibleBadges()
        {
            int shown = 0;
            UI_NewInventoryIcon[] icons = Resources.FindObjectsOfTypeAll<UI_NewInventoryIcon>();
            foreach (UI_NewInventoryIcon icon in icons)
            {
                if (icon != null && icon.gameObject.scene.IsValid())
                {
                    if (ManualPriorityBadge.GetOrCreate(icon).Refresh())
                    {
                        shown++;
                    }
                }
            }
            return shown;
        }
    }

    /// <summary>图标左上角的分辨率无关 P1/P2 标记；尺寸使用图标 RectTransform 的比例锚点。</summary>
    internal sealed class ManualPriorityBadge : MonoBehaviour
    {
        private UI_NewInventoryIcon owner;
        private GameObject badgeRoot;
        private TextMeshProUGUI label;

        internal static ManualPriorityBadge GetOrCreate(UI_NewInventoryIcon icon)
        {
            ManualPriorityBadge badge = icon.GetComponent<ManualPriorityBadge>();
            if (badge == null)
            {
                badge = icon.gameObject.AddComponent<ManualPriorityBadge>();
            }
            badge.owner = icon;
            badge.EnsureVisual();
            return badge;
        }

        private void EnsureVisual()
        {
            if (badgeRoot != null || owner == null)
            {
                return;
            }

            badgeRoot = new GameObject("ManualPriorityBadge", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            badgeRoot.transform.SetParent(owner.transform, false);
            badgeRoot.transform.SetAsLastSibling();

            RectTransform rect = (RectTransform)badgeRoot.transform;
            rect.anchorMin = new Vector2(0.03f, 0.02f);
            rect.anchorMax = new Vector2(0.38f, 0.23f);
            rect.pivot = new Vector2(0f, 0f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            label = badgeRoot.GetComponent<TextMeshProUGUI>();
            if (owner.quantityText != null)
            {
                label.font = owner.quantityText.font;
            }
            label.text = "P1";
            label.color = new Color(1f, 0.82f, 0.20f, 1f);
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.enableAutoSizing = true;
            label.fontSizeMin = 4f;
            label.fontSizeMax = owner.quantityText != null ? Math.Max(7f, owner.quantityText.fontSize * 0.58f) : 11f;
            label.raycastTarget = false;
            badgeRoot.SetActive(false);
        }

        internal bool Refresh()
        {
            EnsureVisual();
            if (badgeRoot == null || owner == null)
            {
                return false;
            }

            Plugin plugin = Plugin.Instance;
            NewItemOwnInstance item = owner.Item;
            int rank = item != null ? ManualPriorityManager.GetRank(item.InstanceID) : 0;
            bool show = plugin != null && plugin.ManualPriorityEnabled.Value &&
                        plugin.ShowManualPriorityBadge.Value &&
                        item != null && item.Charm != null && rank > 0;
            badgeRoot.SetActive(show);
            if (show)
            {
                label.text = "P" + rank;
                badgeRoot.transform.SetAsLastSibling();
            }
            return show;
        }
    }

    [HarmonyPatch(typeof(UI_NewInventoryIcon), nameof(UI_NewInventoryIcon.OnPointerClick))]
    internal static class ManualPriorityClickPatch
    {
        private static bool Prefix(UI_NewInventoryIcon __instance, PointerEventData eventData)
        {
            if (eventData == null || eventData.button != PointerEventData.InputButton.Middle)
            {
                return true;
            }

            Plugin plugin = Plugin.Instance;
            if (plugin == null || !plugin.ManualPriorityEnabled.Value)
            {
                return true;
            }

            NewItemOwnInstance item = __instance != null ? __instance.Item : null;
            GridInventory inventory = __instance != null ? __instance.Inventory : null;
            if (__instance == null || !__instance.Showing || item == null || item.Charm == null ||
                inventory == null || !inventory.isLocalPlayer)
            {
                return true;
            }

            if (plugin.IsSorting)
            {
                Plugin.Log.LogInfo("整理进行中，已忽略本次中键提权操作。");
                return false;
            }

            int rank = ManualPriorityManager.Toggle(item.InstanceID);
            int shown = ManualPriorityManager.RefreshVisibleBadges();
            Plugin.Log.LogInfo(rank > 0
                ? $"手动优先级：instance={item.InstanceID} → P{rank}（已设置 {ManualPriorityManager.Count} 件，界面标记 {shown} 个）"
                : $"手动优先级：instance={item.InstanceID} → 已取消（恢复默认优先级；剩余 {ManualPriorityManager.Count} 件，界面标记 {shown} 个）");
            return false;
        }
    }

    [HarmonyPatch(typeof(UI_NewInventoryIcon), nameof(UI_NewInventoryIcon.SetItemReference))]
    internal static class ManualPrioritySetItemPatch
    {
        private static void Postfix(UI_NewInventoryIcon __instance)
        {
            ManualPriorityBadge.GetOrCreate(__instance).Refresh();
        }
    }

    [HarmonyPatch(typeof(UI_NewInventoryIcon), nameof(UI_NewInventoryIcon.UpdateIcon))]
    internal static class ManualPriorityUpdateIconPatch
    {
        private static void Postfix(UI_NewInventoryIcon __instance)
        {
            ManualPriorityBadge.GetOrCreate(__instance).Refresh();
        }
    }

    [HarmonyPatch(typeof(UI_NewInventoryIcon), "OnEnable")]
    internal static class ManualPriorityEnablePatch
    {
        private static void Postfix(UI_NewInventoryIcon __instance)
        {
            ManualPriorityBadge.GetOrCreate(__instance).Refresh();
        }
    }
}
