using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SephiriaBackpackOrganizer
{
    /// <summary>
    /// 手动方向绑定：按住 Ctrl 时鼠标中键点击护符，循环设置绑定方向。
    /// 绑定含义 = 整理前该方向相邻的那件护符是绑定对象，整理后本物品仍与它相邻且保持该相对方向
    /// （参照沙漏绑右侧魔法书、碎片绑左侧魔法书、指北针绑上方目标的做法，推广到任意四方向）。
    /// 每件物品独立循环：无绑定 → 右(→) → 左(←) → 上(↑) → 下(↓) → 左右(←→) → 无绑定。
    /// 左右(←→) 模式：本物品整理前左右两侧都紧邻护符时，整理后左右两侧仍保持是这两件（本物品被夹住不分离）。
    /// 图标右下角显示方向符号；未绑定的物品不参与任何约束，完全走原默认逻辑。
    /// </summary>
    internal enum ManualBindDirection
    {
        None = 0,
        Right = 1,   // 绑定右侧相邻物品（本物品保持在其左侧）
        Left = 2,    // 绑定左侧相邻物品（本物品保持在其右侧）
        Up = 3,      // 绑定上方相邻物品（本物品保持在其下方，类似指北针）
        Down = 4,    // 绑定下方相邻物品（本物品保持在其上方）
        Both = 5     // 同时绑定左右两侧相邻物品（本物品被夹在中间不分离）
    }

    internal static class DirectionBindingManager
    {
        private const int MaxDir = 5;

        /// <summary>instanceID -> 绑定方向（不在字典里=无绑定）。</summary>
        private static readonly Dictionary<int, ManualBindDirection> DirByInstance =
            new Dictionary<int, ManualBindDirection>();

        internal static int Count => DirByInstance.Count;

        /// <summary>点击一次循环推进：None→Right→Left→Up→Down→Both→None。返回新方向。</summary>
        internal static ManualBindDirection Toggle(int instanceId)
        {
            ManualBindDirection next = (ManualBindDirection)((int)GetDirection(instanceId) + 1);
            if ((int)next > MaxDir)
            {
                DirByInstance.Remove(instanceId);
                return ManualBindDirection.None;
            }
            DirByInstance[instanceId] = next;
            return next;
        }

        internal static ManualBindDirection GetDirection(int instanceId)
        {
            return DirByInstance.TryGetValue(instanceId, out ManualBindDirection d) ? d : ManualBindDirection.None;
        }

        internal static Dictionary<int, ManualBindDirection> PruneAndSnapshot(HashSet<int> presentIds)
        {
            if (presentIds != null)
            {
                var stale = new List<int>();
                foreach (KeyValuePair<int, ManualBindDirection> kv in DirByInstance)
                {
                    if (!presentIds.Contains(kv.Key))
                    {
                        stale.Add(kv.Key);
                    }
                }
                foreach (int id in stale)
                {
                    DirByInstance.Remove(id);
                }
            }
            return new Dictionary<int, ManualBindDirection>(DirByInstance);
        }

        internal static void Clear()
        {
            if (DirByInstance.Count == 0)
            {
                return;
            }
            DirByInstance.Clear();
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
                    if (DirectionBindingBadge.GetOrCreate(icon).Refresh())
                    {
                        shown++;
                    }
                }
            }
            return shown;
        }
    }

    /// <summary>图标右下角的方向绑定符号（→ ← ↑ ↓），尺寸使用图标 RectTransform 的比例锚点。</summary>
    internal sealed class DirectionBindingBadge : MonoBehaviour
    {
        private UI_NewInventoryIcon owner;
        private GameObject badgeRoot;
        private TextMeshProUGUI label;

        internal static DirectionBindingBadge GetOrCreate(UI_NewInventoryIcon icon)
        {
            DirectionBindingBadge badge = icon.GetComponent<DirectionBindingBadge>();
            if (badge == null)
            {
                badge = icon.gameObject.AddComponent<DirectionBindingBadge>();
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

            badgeRoot = new GameObject("DirectionBindingBadge", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            badgeRoot.transform.SetParent(owner.transform, false);
            badgeRoot.transform.SetAsLastSibling();

            RectTransform rect = (RectTransform)badgeRoot.transform;
            // 右下角
            rect.anchorMin = new Vector2(0.62f, 0.02f);
            rect.anchorMax = new Vector2(0.97f, 0.23f);
            rect.pivot = new Vector2(1f, 0f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            label = badgeRoot.GetComponent<TextMeshProUGUI>();
            if (owner.quantityText != null)
            {
                label.font = owner.quantityText.font;
            }
            label.text = ">";
            label.color = new Color(0.45f, 0.95f, 1f, 1f); // 青色，与手动优先级(金色)区分
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.enableAutoSizing = true;
            label.fontSizeMin = 6f;
            label.fontSizeMax = owner.quantityText != null ? Math.Max(9f, owner.quantityText.fontSize * 0.72f) : 14f;
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
            ManualBindDirection dir = item != null ? DirectionBindingManager.GetDirection(item.InstanceID) : ManualBindDirection.None;
            bool show = plugin != null && plugin.ManualPriorityEnabled.Value &&
                        plugin.ShowDirectionBindingBadge.Value &&
                        item != null && item.Charm != null && dir != ManualBindDirection.None;
            badgeRoot.SetActive(show);
            if (show)
            {
                label.text = DirSymbol(dir);
                badgeRoot.transform.SetAsLastSibling();
            }
            return show;
        }

        internal static string DirSymbol(ManualBindDirection dir)
        {
            switch (dir)
            {
                case ManualBindDirection.Right: return "→";
                case ManualBindDirection.Left: return "←";
                case ManualBindDirection.Up: return "↑";
                case ManualBindDirection.Down: return "↓";
                case ManualBindDirection.Both: return "←→";
                default: return "";
            }
        }
    }
}

