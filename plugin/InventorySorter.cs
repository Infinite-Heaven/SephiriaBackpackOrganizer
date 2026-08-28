using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mirror;
using UnityEngine;

namespace SephiriaBackpackOrganizer
{
    public enum SortMode
    {
        Vanilla,
        Enhanced
    }

    /// <summary>
    /// 背包整理引擎 v2.0 —— 全离线智能模型。
    ///
    /// v2.0 相对 v1.x 的关键升级：
    /// 1. 【性能】评估从"每次跑游戏权限周期"改为纯离线模型：预计算石板效果模板、
    ///    读取真实 levelMatrix 反推基础等级、附魔等级逐物品计入；单次评估微秒级，
    ///    后期满背包也不再卡顿（原版十几秒 -> 亚秒）。
    /// 2. 【行星望远镜】Charm_PlanetModule 启用时，周围八格 PLANET 分类藏品获得加成
    ///    —— 评分加入"行星聚簇"奖励，搜索会把行星藏品摆到望远镜周围。
    /// 3. 【凯尔萨德尼钥匙】按已有最多的坚固/余烬/冰川/魔法科技羁绊选择周期行，
    ///    并可在第 1/5/9 行这类同余行中选择其他加成最优的格子。
    /// 4. 【指北针】整理前已指向的伤害类藏品会按实例锁定，整理时整条竖链一起移动，
    ///    保证指北针仍在原目标正下方；原先未配对的针仍可自动寻找目标并支持链式叠加。
    /// 5. 【白纸】统计连击当前数量与最高效果档位，把白纸夹进数量最大且未满的连击中，补位后计入评分。
    /// 6. 【附魔】物品自身附魔等级(Enchant)随物品走，计入格位等级。
    /// 7. 【石板位置条件】旗帜(最左列)/遮阳(最上行)等通过 conditionQuery 边界 token
    ///    表达，智能摆位按游戏解析器语义评估条件，不满足条件的效果不计入。
    /// 8. 【心之重担等负面藏品】按 LocalizedString key 识别，直接塞进负数加成格子。
    /// 9. 【稀有度优先】高稀有度藏品优先获得等级加成，必要时低级藏品被牺牲进负格。
    /// 10. 安全兜底：最终结果按离线评分与原始布局比较，绝不更差。
    /// </summary>
    public class InventorySorter
    {
        private static readonly string[] CyclicRowCategories =
        {
            "STURDY", "EMBER", "GLACIER", "MAGITECH"
        };

        private static readonly string[] CyclicRowCategoryNames =
        {
            "坚固", "余烬", "冰川", "魔法科技"
        };

        private const double ManualPriorityRankDecay = 200d;

        private readonly Plugin plugin;
        private bool busy;
        private float sessionStartTime = -1f;
        private Task<SearchOutcome> pendingSearch;
        private PendingEnhancedSort pendingEnhanced;

        public bool Busy => busy;

        /// <summary>会话断开时重置初始化计时（下次进入新会话重新等待背包就绪）。</summary>
        public void ResetSessionClock()
        {
            sessionStartTime = -1f;
        }

        public InventorySorter(Plugin plugin)
        {
            this.plugin = plugin;
        }

        // ---------------------------------------------------------------- 单格状态

        private sealed class Slot
        {
            public bool hasItem;
            public int instanceID;
            public int entityID;
            public sbyte quantity;
            public Charm_Basic charm;
            public StoneTablet tablet;
            public int rotation;

            public Slot Clone()
            {
                return new Slot
                {
                    hasItem = hasItem,
                    instanceID = instanceID,
                    entityID = entityID,
                    quantity = quantity,
                    charm = charm,
                    tablet = tablet,
                    rotation = rotation
                };
            }

            public static Slot Empty() => new Slot();
        }

        // ---------------------------------------------------------------- 护符位置条件

        private enum CharmPositionKind
        {
            None,
            Top,
            Bottom,
            Side,
            Inside,
            Outlined,
            BothSidesEmpty,
            BothSideCharm,
            NeighborsFull,
            NearMagicBook,
            FullHp
        }

        private static CharmPositionKind GetPositionKind(Charm_Basic charm)
        {
            if (charm == null || charm.criteria == null)
            {
                return CharmPositionKind.None;
            }

            if (charm.criteria is CharmActivateCriteria_TopInInventory) return CharmPositionKind.Top;
            if (charm.criteria is CharmActivateCriteria_BottomInInventory) return CharmPositionKind.Bottom;
            if (charm.criteria is CharmActivateCriteria_SideEnd) return CharmPositionKind.Side;
            if (charm.criteria is CharmActivateCriteria_Inside) return CharmPositionKind.Inside;
            if (charm.criteria is CharmActivateCriteria_Outlined) return CharmPositionKind.Outlined;
            if (charm.criteria is CharmActivateCriteria_BothSidesAreEmpty) return CharmPositionKind.BothSidesEmpty;
            if (charm.criteria is CharmActivateCriteria_BothSideCharm) return CharmPositionKind.BothSideCharm;
            if (charm.criteria is CharmActivateCriteria_NeighborsAreFull) return CharmPositionKind.NeighborsFull;
            if (charm.criteria is CharmActivateCriteria_Near8MagicBook) return CharmPositionKind.NearMagicBook;
            return CharmPositionKind.None;
        }

        private static bool IsSatisfyingCell(CharmPositionKind kind, int x, int y, int index, int storage, int width)
        {
            switch (kind)
            {
                case CharmPositionKind.Top:
                    return y == 0;
                case CharmPositionKind.Bottom:
                    return index >= storage - 6;
                case CharmPositionKind.Side:
                    return x == 0 || x >= width - 1;
                case CharmPositionKind.Inside:
                    return x > 0 && y > 0 && x < width - 1 && index + 7 <= storage - 1;
                case CharmPositionKind.Outlined:
                    return x <= 0 || y <= 0 || x >= width - 1 || index >= storage - 6;
                default:
                    return true;
            }
        }

        private static int KindPriority(CharmPositionKind kind)
        {
            switch (kind)
            {
                case CharmPositionKind.Top:
                case CharmPositionKind.Bottom:
                case CharmPositionKind.Side:
                    return 3;
                case CharmPositionKind.Inside:
                case CharmPositionKind.Outlined:
                    return 2;
                default:
                    return 1;
            }
        }

        private static readonly ItemPosition[] Neighbor8 =
        {
            new ItemPosition(-1, 0), new ItemPosition(1, 0),
            new ItemPosition(0, -1), new ItemPosition(0, 1),
            new ItemPosition(-1, -1), new ItemPosition(1, -1),
            new ItemPosition(-1, 1), new ItemPosition(1, 1)
        };

        // ---------------------------------------------------------------- 物品信息与石板效果模板

        private sealed class ItemInfo
        {
            public int index;
            public Slot slot;
            public bool isStele;
            public bool isCharm;
            public bool weaponOk = true;
            public bool tabletRotatable;
            public int steleImportance;
            public int manualPriorityRank; // 0=未提权，1=最后提权（最高），2=次高……
            public bool isBurden;
            public bool isPlanetCategory;   // Entity.categories.Contains("PLANET")
            public bool excludeFromPlanetCluster; // 行星分类但不参与望远镜聚簇（如乐谱银河）
            public bool isPlanetModule;     // Charm_PlanetModule（行星望远镜）
            public bool isHarmonyCrystal;   // Charm_NearLevelDamage（和谐之晶：周围8格护符等级和→伤害放大）
            public bool isDedicationBadge;  // Charm_CompanionChaos（奉献徽章：加成同一横排的同伴）
            public bool isDedicationCompanion; // ICompanionCharm（同伴：金色手铃/迷你弩炮/灵魂粉末等）
            public bool isHourglass;         // Charm_RightSpellCooldownHelper（发光的沙漏：右边格魔法书 CD 变短）
            public bool isMagicBook;         // Charm_Magic（魔法书：ContainedMagic 提供 CD 与伤害）
            public float magicCd;            // 魔法书 CD 秒数（ActiveSkillEntity.cooldownTime）
            public bool isRayShard;          // 雷伊星碎片（Item_DoubleMagic_Name：放在耗蓝最高的魔法书右侧）
            public int magicMpCost;          // 魔法书耗蓝量（mpCostsByLevel 最大值）
            public bool isCompass;          // Charm_UpCharmDamage（指北针）
            public bool isAttackable;       // IAttackableCharm
            public bool isWhitePaper;       // Charm_WhitePaper（白纸：左右相邻神器共享分类时复制该连击）
            public bool isBelt;             // Charm_WoodenBox 类（多用途腰带/木箱：最上行每件神器效果叠加一次）
            public bool lowLevelValue;      // 等级价值低（闪烁的眼睛/蜥蜴板甲等：等级分打折，只保证启用）
            public int minDesiredLevel;     // 必须优先达到的最低有效等级（谱子「银河」=2）
            public float levelScoreFactor = 1f; // 等级分系数（低价值物品 <1）
            public HashSet<string> comboCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public CharmPositionKind kind;
            public EItemRarity rarity;
            public int priority = 4;   // 用户优先级：1最高~4最低（传说/羁绊=1 稀有=2 高级=3 普通=4，特定藏品强制1）
            public bool preferIgnoreCells; // 优先利用豁免格解除位置限制（如冰冷的锁）
            public bool isRowLocked;   // 固定行或周期行约束
            public int lockRow;        // 固定行号，或 lockRowCycle > 0 时的目标余数
            public int lockRowCycle;   // >0 时 lockRow 是周期余数：例如 0/4 允许第1、5、9行
            public bool isCyclicRowCategory; // Charm_3Elemental_ByRow（凯尔萨德尼钥匙）
            public string originalRowCategory;
            public string targetRowCategory;
            public bool isEternalEclipse;  // Charm_FireIceWeapon（永恒蚀：按冰霜武具/太阳剑数量决定左/右三列）
            public bool isOpposingScale;   // Charm_FireIce（对立之秤：左侧Fire右侧Ice，按冰川/余烬数量决定最左/最右列）
            public int enchant;
            public int maxLevel;
            public ItemEntity entity;
        }

        /// <summary>
        /// 整理前已经成立的“目标神器 → 指北针（正下方）”竖向链。
        /// 链首是被指向的神器，后续成员均为跟随它移动的指北针；支持多枚指北针纵向叠加。
        /// </summary>
        private sealed class CompassChain
        {
            public int originalRootCell;
            public readonly List<int> instanceIDs = new List<int>();
        }

        private sealed class WhitePaperComboTarget
        {
            public string category;
            public string displayName;
            public int baseCount;
            public int cap;
        }

        private struct EffectEntry
        {
            public int cell;      // 格子索引
            public byte kind;     // 0=加等级 1=禁用 2=豁免 3=乘等级
            public int value;     // 等级增量/乘数
        }

        private struct ConditionEntry
        {
            public int cell;                          // 格子索引
            public StoneTablet.CriteriaType type;     // 条件类型
        }

        /// <summary>一块石板在某个(格子,旋转)下的效果模板与条件（预计算）。</summary>
        private sealed class StelePattern
        {
            public int cell;
            public int rotation;
            public List<EffectEntry> effects = new List<EffectEntry>();
            public List<ConditionEntry> conditions = new List<ConditionEntry>();
            public bool hasPlacedCondition;
        }

        /// <summary>一次整理的全部预计算上下文。</summary>
        private sealed class SearchContext
        {
            public GridInventory inv;
            public int storage;
            public int width;
            public int height;
            public int[] baseLevel;               // 无石板贡献、无附魔的基础格位等级
            public List<Slot> original;           // 原始布局
            public List<ItemInfo> items = new List<ItemInfo>();
            public List<ItemInfo> steles = new List<ItemInfo>();
            public List<ItemInfo> charms = new List<ItemInfo>();
            public List<ItemInfo> whitePapers = new List<ItemInfo>();
            public List<ItemInfo> burdens = new List<ItemInfo>();
            public List<ItemInfo> others = new List<ItemInfo>();
            public Dictionary<int, Dictionary<int, StelePattern>> stelePatterns; // tabletInstanceID -> cell*4+rot -> pattern
            public Dictionary<int, ItemInfo> itemByInstance = new Dictionary<int, ItemInfo>(); // instanceID -> ItemInfo
            // 仅记录整理前已经配对的指北针：compass instanceID -> 原先正上方目标 instanceID。
            public Dictionary<int, int> compassTargetByInstance = new Dictionary<int, int>();
            public List<CompassChain> compassChains = new List<CompassChain>();
            public HashSet<int> compassChainInstances = new HashSet<int>();
            public Dictionary<int, int> compassPositionScratch = new Dictionary<int, int>();
            public HashSet<int> compassReservedScratch = new HashSet<int>();
            public int[] compassRootScratch = new int[0];
            public List<WhitePaperComboTarget> whitePaperTargets = new List<WhitePaperComboTarget>();
            public int[] whitePaperAssignmentScratch = new int[0];
            public bool hasBelt;              // 背包里存在多用途腰带类藏品（Charm_WoodenBox）
            public int[] mysticFactor = new int[0]; // 神秘地块等级倍率（默认 1；神秘藏品≥2时1格×2，≥5时4格×2）
            public int frostCount;        // 冰霜武具(FROST)分类藏品数（永恒蚀摆位依据）
            public int flameSwordCount;   // 太阳剑(FLAMESWORD)分类藏品数（永恒蚀摆位依据）
            public int glacierCount;      // 冰川(GLACIER)分类藏品数（对立之秤摆位依据）
            public int emberCount;        // 余烬(EMBER)分类藏品数（对立之秤摆位依据）
            public int mysticCount;                  // 神秘分类藏品数量（游戏组合计数）
            public int mysticActiveCells;            // 实际生效的 ×2 地块数
            public int[] cellLevel = new int[0];  // 评估时复用缓冲
            public bool[] disabled = new bool[0];
            public bool[] ignore = new bool[0];
            public bool[] compassPairedScratch = new bool[0];
            public int[] itemIndexScratch = new int[0];
            public int[] emptyIndexScratch = new int[0];
            public readonly List<int> moveScratchA = new List<int>();
            public readonly List<int> moveScratchB = new List<int>();
            public readonly List<int> moveScratchC = new List<int>();
            public readonly List<int> moveScratchD = new List<int>();
            public readonly HashSet<int> instanceSetScratch = new HashSet<int>();
            public int annealEvaluations;
            public int annealStarts;
            public int annealStartsCompleted;
            public bool searchBudgetReached;
            public int manualPriorityCount;
            public double manualPriorityStrength;
        }

        private sealed class SearchOutcome
        {
            public List<Slot> layout;
            public double beforeScore;
            public double bestScore;
        }

        private sealed class PendingEnhancedSort
        {
            public GridInventory inv;
            public List<Slot> original;
            public SearchContext ctx;
            public float beforeGameScore;
            public System.Diagnostics.Stopwatch stopwatch;
            public SearchOutcome outcome;
            public List<Slot> target;
            public List<Slot> expected;
            public readonly List<(int a, int b)> swaps = new List<(int a, int b)>();
            public readonly List<(int pos, int count)> rotations = new List<(int pos, int count)>();
            public int swapIndex;
            public int rotationIndex;
            public int rotationRemaining;
            public bool applying;
            public bool rollingBack;
            public bool awaitingObservedState;
            public readonly System.Diagnostics.Stopwatch acknowledgement = new System.Diagnostics.Stopwatch();
            public int swapsPerFrame;
            public int rotationClicksPerFrame;
            public double frameBudgetMs;
            public int acknowledgementTimeoutMs;
        }

        // ---------------------------------------------------------------- 石板查询解析

        private static List<StoneTablet.AdditionMetadata> ParseQuerySafe(
            StoneTablet tablet, ItemPosition origin, int rotation, int width, int height, int storage, bool condition)
        {
            try
            {
                string q = condition
                    ? tablet.GetConditionQuery(tablet.instanceID)
                    : tablet.GetQuery(tablet.instanceID);
                if (string.IsNullOrEmpty(q))
                {
                    return new List<StoneTablet.AdditionMetadata>();
                }
                return StoneTablet.ParseQuery(q, width, height, storage, origin, rotation, out _);
            }
            catch
            {
                return new List<StoneTablet.AdditionMetadata>();
            }
        }

        private static bool InBounds(int x, int y, int width, int height)
        {
            return x >= 0 && y >= 0 && x < width && y < height;
        }

        /// <summary>安全格子访问：越界（含 storage 尾部不完整行）返回 null。</summary>
        private static Slot At(List<Slot> slots, int x, int y, int width, int storage)
        {
            if (x < 0 || y < 0 || x >= width)
            {
                return null;
            }
            int idx = y * width + x;
            if (idx < 0 || idx >= storage || idx >= slots.Count)
            {
                return null;
            }
            return slots[idx];
        }

        // ---------------------------------------------------------------- 上下文构建

        private SearchContext BuildContext(GridInventory inv, List<Slot> original)
        {
            int storage = inv.CurrentInventoryStorage;
            int w = inv.Width;
            int h = inv.GetHeight(storage);

            var ctx = new SearchContext
            {
                inv = inv,
                storage = storage,
                width = w,
                height = h,
                original = original,
                baseLevel = new int[storage],
                cellLevel = new int[storage],
                disabled = new bool[storage],
                ignore = new bool[storage],
                compassPairedScratch = new bool[storage],
                itemIndexScratch = new int[storage],
                emptyIndexScratch = new int[storage],
                mysticFactor = new int[storage],
                manualPriorityStrength = Math.Max(1d, plugin.ManualPriorityStrength.Value)
            };

            var presentCharmIds = new HashSet<int>();
            for (int i = 0; i < original.Count; i++)
            {
                Slot slot = original[i];
                if (slot != null && slot.hasItem && slot.charm != null)
                {
                    presentCharmIds.Add(slot.instanceID);
                }
            }
            Dictionary<int, int> manualPriorityRanks = ManualPriorityManager.PruneAndSnapshot(presentCharmIds);
            ctx.manualPriorityCount = manualPriorityRanks.Count;

            // 分类物品
            for (int i = 0; i < original.Count && i < storage; i++)
            {
                Slot s = original[i];
                if (s == null || !s.hasItem)
                {
                    continue;
                }

                var info = new ItemInfo { index = i, slot = s, isStele = s.tablet != null, isCharm = s.charm != null };
                manualPriorityRanks.TryGetValue(s.instanceID, out info.manualPriorityRank);
                if (info.isStele)
                {
                    // GetQuery 属于游戏对象访问，只在主线程构建快照时读取。
                    info.steleImportance = SteleImportance(s.tablet);
                }
                if (info.isCharm)
                {
                    info.kind = GetPositionKind(s.charm);
                    info.maxLevel = s.charm.maxLevel;
                    info.isPlanetModule = s.charm is Charm_PlanetModule;
                    info.isHarmonyCrystal = s.charm is Charm_NearLevelDamage;
                    info.isDedicationBadge = s.charm is Charm_CompanionChaos;
                    info.isDedicationCompanion = s.charm is ICompanionCharm;
                    info.isHourglass = s.charm is Charm_RightSpellCooldownHelper;
                    info.isMagicBook = s.charm is Charm_Magic;
                    if (info.isMagicBook && s.charm is Charm_Magic mg && mg.ContainedMagic != null)
                    {
                        info.magicCd = mg.ContainedMagic.cooldownTime;
                        if (mg.ContainedMagic.mpCostsByLevel != null)
                        {
                            int maxCost = 0;
                            foreach (int c in mg.ContainedMagic.mpCostsByLevel)
                            {
                                if (c > maxCost)
                                {
                                    maxCost = c;
                                }
                            }
                            info.magicMpCost = maxCost;
                        }
                    }
                    info.isEternalEclipse = s.charm is Charm_FireIceWeapon;
                    info.isOpposingScale = s.charm is Charm_FireIce;
                    info.isCompass = s.charm is Charm_UpCharmDamage;
                    info.isAttackable = s.charm is IAttackableCharm ac && ac.IsAttackableCharm();
                    info.isWhitePaper = s.charm is Charm_WhitePaper;
                    info.isCyclicRowCategory = s.charm is Charm_3Elemental_ByRow;
                    info.isBelt = s.charm is Charm_WoodenBox;
                    if (s.charm.isWeaponRelatedCharm)
                    {
                        var wc = s.charm.WeaponController;
                        info.weaponOk = wc != null && wc.currentWeapon != null &&
                                        wc.currentWeapon.weaponType == s.charm.relatedWeapon;
                    }
                }

                try
                {
                    info.entity = ItemDatabase.FindItemById(s.entityID);
                    if (info.entity != null)
                    {
                        info.rarity = info.entity.rarity;
                        if (info.entity.categories != null)
                        {
                            info.isPlanetCategory = info.entity.categories.Contains("PLANET");
                            // 行星分类但不参与望远镜聚簇的藏品（如乐谱银河）
                            if (info.isPlanetCategory && info.entity.aName != null &&
                                !string.IsNullOrEmpty(info.entity.aName.key))
                            {
                                foreach (string key in plugin.PlanetClusterExcludedItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.excludeFromPlanetCluster = true;
                                        break;
                                    }
                                }
                            }
                        }
                        // 负面藏品识别（心之重担等，按 LocalizedString key）
                        if (info.entity.aName != null && !string.IsNullOrEmpty(info.entity.aName.key))
                        {
                            foreach (string key in plugin.BurdenItemKeys.Value.Split(new[] { ',', ';' },
                                         StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (info.entity.aName.key.Trim() == key.Trim())
                                {
                                    info.isBurden = true;
                                    break;
                                }
                            }

                            // 多用途腰带类识别（Charm_WoodenBox 类已自动覆盖；key 支持扩展，如木箱 Item_WoodenBox_Name）
                            foreach (string key in plugin.BeltItems.Value.Split(new[] { ',', ';' },
                                         StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (MatchesItemKey(info, key))
                                {
                                    info.isBelt = true;
                                    break;
                                }
                            }

                            // 用户优先级：稀有度映射 + 强制最高优先级物品
                            if (plugin.PriorityEnable.Value)
                            {
                                info.priority = RarityToPriority(info.rarity);
                                foreach (string key in plugin.PriorityFixedItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.priority = 1;
                                        break;
                                    }
                                }
                                // 低等级价值物品（闪烁的眼睛/故障探测针：等级分打折 + 优先级降到4级）
                                foreach (string key in plugin.PriorityLowValueItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (MatchesItemKey(info, key))
                                    {
                                        info.lowLevelValue = true;
                                        info.priority = 4;
                                        info.levelScoreFactor = Mathf.Clamp(plugin.LowValueLevelFactor.Value, 0f, 1f);
                                        break;
                                    }
                                }
                                // 最低等级目标（谱子「银河」=2：强制最高优先级 + 等级不足额外扣分）
                                foreach (string kv in plugin.PriorityMinLevelItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    string[] parts = kv.Split('=');
                                    if (parts.Length == 2 && MatchesItemKey(info, parts[0]) &&
                                        int.TryParse(parts[1].Trim(), out int minLvl) && minLvl > 0)
                                    {
                                        info.minDesiredLevel = minLvl;
                                        info.priority = 1;
                                        break;
                                    }
                                }
                                // 强制指定优先级（格式 key:数字，覆盖稀有度映射与强制1级；最后匹配生效）
                                foreach (string pair in plugin.ForcedPriorityItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    string[] kv = pair.Split(':');
                                    if (kv.Length == 2 && info.entity.aName.key.Trim() == kv[0].Trim() &&
                                        int.TryParse(kv[1], out int fp) && fp >= 1 && fp <= 4)
                                    {
                                        info.priority = fp;
                                        break;
                                    }
                                }
                                // 优先豁免格物品（如冰冷的锁：有解锁石板就尽可能利用）
                                foreach (string key in plugin.IgnoreCellPreferredItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.preferIgnoreCells = true;
                                        break;
                                    }
                                }
                                // 用户额外指定的固定行物品。凯尔萨德尼钥匙在后续会被自动周期行逻辑覆盖。
                                foreach (string key in plugin.RowLockedItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.isRowLocked = true;
                                        info.lockRow = i / w; // 记录用户摆放时的行（i 为原始格子索引）
                                        break;
                                    }
                                }
                                // 和谐之晶类（可扩展；类识别已覆盖默认物品）
                                foreach (string key in plugin.HarmonyCrystalItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.isHarmonyCrystal = true;
                                        break;
                                    }
                                }
                                // 奉献徽章类（可扩展；类识别已覆盖默认物品）
                                foreach (string key in plugin.DedicationBadgeItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.isDedicationBadge = true;
                                        break;
                                    }
                                }
                                // 发光的沙漏类（可扩展；类识别已覆盖默认物品）
                                foreach (string key in plugin.HourglassItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.isHourglass = true;
                                        break;
                                    }
                                }
                                // 雷伊星碎片类（可扩展；默认 key Item_DoubleMagic_Name，也支持类名 token）
                                foreach (string token in plugin.RayShardItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (MatchesItemKey(info, token))
                                    {
                                        info.isRayShard = true;
                                        break;
                                    }
                                }
                                // 永恒蚀类（可扩展；类识别已覆盖默认物品）
                                foreach (string key in plugin.EclipseItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.isEternalEclipse = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // 忽略数据异常
                }

                // 记录整理开始时每件非白纸神器实际提供的连击分类。动态分类神器优先读
                // GetItemCategory；普通神器在失败时回退到 ItemEntity.categories。
                if (info.isCharm && !info.isWhitePaper)
                {
                    try
                    {
                        foreach (string category in s.charm.GetItemCategory())
                        {
                            if (!string.IsNullOrEmpty(category))
                            {
                                info.comboCategories.Add(category);
                            }
                        }
                    }
                    catch
                    {
                    }
                    if (info.comboCategories.Count == 0 && info.entity != null && info.entity.categories != null)
                    {
                        foreach (string category in info.entity.categories)
                        {
                            if (!string.IsNullOrEmpty(category))
                            {
                                info.comboCategories.Add(category);
                            }
                        }
                    }
                }

                // 附魔等级（物品自身携带，随物品走）
                try
                {
                    string enchantStr = DungeonManager.Instance != null
                        ? DungeonManager.Instance.GetGlobalItemStatValue(s.instanceID, "Enchant")
                        : "";
                    if (int.TryParse(enchantStr, out int e) && e != 0)
                    {
                        info.enchant = e;
                    }
                }
                catch
                {
                }

                ctx.items.Add(info);
                ctx.itemByInstance[s.instanceID] = info;
                if (info.isBelt) ctx.hasBelt = true;
                if (info.isWhitePaper) ctx.whitePapers.Add(info);
                if (info.isBurden) ctx.burdens.Add(info);
                else if (info.isStele) ctx.steles.Add(info);
                else if (info.isCharm) ctx.charms.Add(info);
                else ctx.others.Add(info);
            }

            ConfigureCyclicRowCategories(ctx);
            CaptureCompassBindings(ctx, original);

            // 被北向的金色针（指北针）锁定的目标神器强制最高优先级：无论稀有度，优先拉满等级。
            // 已绑定的针只会指向整理前的同一实例，因此这里提升的就是该实例。
            if (plugin.PriorityEnable.Value && plugin.CompassTargetForcedHigh.Value)
            {
                foreach (int targetId in ctx.compassTargetByInstance.Values)
                {
                    if (ctx.itemByInstance.TryGetValue(targetId, out ItemInfo target) && target != null)
                    {
                        target.priority = 1;
                    }
                }
            }

            BuildWhitePaperTargets(ctx);

            // 预计算每块石板的全部摆放模板（以石板实例ID为键）
            ctx.stelePatterns = new Dictionary<int, Dictionary<int, StelePattern>>();
            foreach (ItemInfo stele in ctx.steles)
            {
                bool rotatable = DungeonManager.IsTabletRotatable(stele.slot.tablet.instanceID, stele.slot.tablet.isRotatable);
                stele.tabletRotatable = rotatable;
                int rotations = rotatable ? 4 : 1;
                var byCell = new Dictionary<int, StelePattern>();
                for (int cell = 0; cell < storage; cell++)
                {
                    ItemPosition origin = inv.IdxToPos(cell);
                    for (int rot = 0; rot < rotations; rot++)
                    {
                        byCell[cell * 4 + rot] = BuildStelePattern(ctx, stele.slot.tablet, origin, rot);
                    }
                }
                ctx.stelePatterns[stele.slot.tablet.instanceID] = byCell;
            }

            // 神秘地块：神秘分类藏品≥2时 1 格 ×2，≥5时 4 格 ×2（ComboEffect_Mystic 在 mysticPositions 上放 ×2 固定附魔）
            for (int i = 0; i < storage; i++)
            {
                ctx.mysticFactor[i] = 1;
            }
            if (plugin.MysticEnable.Value)
            {
                ctx.mysticCount = 0;
                try
                {
                    // 权威来源：游戏组合计数（键与 categories 标签一致）
                    foreach (var kv in inv.currentSetEffectCount)
                    {
                        if (string.Equals(kv.Key, plugin.MysticCategory.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.mysticCount = kv.Value;
                            break;
                        }
                    }
                }
                catch
                {
                }
                if (ctx.mysticCount <= 0)
                {
                    // 兜底：直接统计护符标签（大小写不敏感）
                    foreach (ItemInfo info in ctx.items)
                    {
                        if (info.isCharm && info.entity != null && info.entity.categories != null &&
                            info.entity.categories.Any(c =>
                                string.Equals(c, plugin.MysticCategory.Value, StringComparison.OrdinalIgnoreCase)))
                        {
                            ctx.mysticCount++;
                        }
                    }
                }
                int active = ctx.mysticCount >= 5 ? 4 : (ctx.mysticCount >= 2 ? 1 : 0);
                ctx.mysticActiveCells = 0;
                if (active > 0 && inv.mysticPositions != null && inv.mysticPositions.Count > 0)
                {
                    int factor = Math.Max(1, (int)plugin.MysticMultiplier.Value);
                    int limit = Math.Min(active, inv.mysticPositions.Count);
                    for (int k = 0; k < limit; k++)
                    {
                        ItemPosition mp = inv.mysticPositions[k];
                        int cell = inv.PosToIdx(mp);
                        if (cell >= 0 && cell < storage)
                        {
                            ctx.mysticFactor[cell] = factor;
                            ctx.mysticActiveCells++;
                        }
                    }
                }
            }

            // 基础等级 = 真实 levelMatrix - 当前石板贡献 - 当前物品附魔（神秘×2地块先除倍率再减）
            int[] currentStele = ComputeSteleContribution(ctx, original);
            for (int i = 0; i < storage; i++)
            {
                int real = 0;
                if (inv.levelMatrix.TryGetValue(inv.IdxToPos(i), out int v))
                {
                    real = v;
                }
                int enchant = 0;
                if (original[i] != null && original[i].hasItem)
                {
                    foreach (ItemInfo info in ctx.items)
                    {
                        if (info.index == i)
                        {
                            enchant = info.enchant;
                            break;
                        }
                    }
                }
                int factor = ctx.mysticFactor[i];
                ctx.baseLevel[i] = (factor > 1 ? real / factor : real) - currentStele[i] - enchant;
            }

            // 统计冰霜武具(FROST)/太阳剑(FLAMESWORD)分类藏品数（永恒蚀摆位依据）
            foreach (ItemInfo it in ctx.items)
            {
                if (it.entity == null || it.entity.categories == null)
                {
                    continue;
                }
                if (it.entity.categories.Contains(EclipseFrostCategory))
                {
                    ctx.frostCount++;
                }
                if (it.entity.categories.Contains(EclipseFlameSwordCategory))
                {
                    ctx.flameSwordCount++;
                }
                if (it.entity.categories.Contains(ScaleGlacierCategory))
                {
                    ctx.glacierCount++;
                }
                if (it.entity.categories.Contains(ScaleEmberCategory))
                {
                    ctx.emberCount++;
                }
            }

            return ctx;
        }

        /// <summary>永恒蚀摆位用的分类标签：冰霜武具 / 太阳剑（与游戏 categories 标签一致）。</summary>
        internal const string EclipseFrostCategory = "FROST";
        internal const string EclipseFlameSwordCategory = "FLAMESWORD";
        /// <summary>对立之秤摆位用的分类标签：冰川 / 余烬。</summary>
        internal const string ScaleGlacierCategory = "GLACIER";
        internal const string ScaleEmberCategory = "EMBER";

        /// <summary>
        /// 凯尔萨德尼钥匙（Charm_3Elemental_ByRow）按行号每四行循环：
        /// 坚固 / 余烬 / 冰川 / 魔法科技。整理前先统计除钥匙和白纸外的实际神器分类，
        /// 选择数量最多的羁绊，之后搜索可在所有对应同余行中自由选最优格。
        /// </summary>
        private static void ConfigureCyclicRowCategories(SearchContext ctx)
        {
            List<ItemInfo> cyclicItems = ctx.charms.FindAll(item => item.isCyclicRowCategory);
            if (cyclicItems.Count == 0)
            {
                return;
            }

            int[] counts = new int[CyclicRowCategories.Length];
            try
            {
                foreach (var pair in ctx.inv.currentSetEffectCount)
                {
                    for (int i = 0; i < CyclicRowCategories.Length; i++)
                    {
                        if (string.Equals(pair.Key, CyclicRowCategories[i], StringComparison.OrdinalIgnoreCase))
                        {
                            counts[i] = pair.Value;
                            break;
                        }
                    }
                }
            }
            catch
            {
            }

            // 游戏当前计数含有白纸和钥匙自身；选目标时先扣掉这些可变因素，
            // 否则钥匙可能因为自己当前在某行，就永远偏向原分类。
            foreach (ItemInfo item in ctx.whitePapers)
            {
                if (!(item.slot.charm is Charm_WhitePaper paper))
                {
                    continue;
                }
                try
                {
                    foreach (string category in paper.assignedCategory)
                    {
                        for (int i = 0; i < CyclicRowCategories.Length; i++)
                        {
                            if (string.Equals(category, CyclicRowCategories[i], StringComparison.OrdinalIgnoreCase))
                            {
                                counts[i] = Math.Max(0, counts[i] - 1);
                                break;
                            }
                        }
                    }
                }
                catch
                {
                }
            }
            foreach (ItemInfo item in cyclicItems)
            {
                int originalIndex = (item.index / ctx.width) % CyclicRowCategories.Length;
                counts[originalIndex] = Math.Max(0, counts[originalIndex] - 1);
            }

            int[] physicalCounts = new int[CyclicRowCategories.Length];
            foreach (ItemInfo item in ctx.items)
            {
                if (!item.isCharm || item.isWhitePaper || item.isCyclicRowCategory)
                {
                    continue;
                }
                foreach (string category in item.comboCategories)
                {
                    for (int i = 0; i < CyclicRowCategories.Length; i++)
                    {
                        if (string.Equals(category, CyclicRowCategories[i], StringComparison.OrdinalIgnoreCase))
                        {
                            physicalCounts[i]++;
                            break;
                        }
                    }
                }
            }
            for (int i = 0; i < counts.Length; i++)
            {
                counts[i] = Math.Max(counts[i], physicalCounts[i]);
            }

            // 数量并列时优先保留第一把钥匙整理前的羁绊，减少无意义换行。
            int preferredIndex = (cyclicItems[0].index / ctx.width) % CyclicRowCategories.Length;
            int bestIndex = -1;
            int bestCount = int.MinValue;
            for (int i = 0; i < CyclicRowCategories.Length; i++)
            {
                if (i * ctx.width >= ctx.storage)
                {
                    continue; // 当前背包还没有这个余数对应的行。
                }
                if (counts[i] > bestCount ||
                    (counts[i] == bestCount && i == preferredIndex && bestIndex != preferredIndex))
                {
                    bestIndex = i;
                    bestCount = counts[i];
                }
            }
            if (bestIndex < 0)
            {
                bestIndex = preferredIndex;
            }

            foreach (ItemInfo item in cyclicItems)
            {
                int originalRow = item.index / ctx.width;
                int originalIndex = originalRow % CyclicRowCategories.Length;
                item.originalRowCategory = CyclicRowCategories[originalIndex];
                item.targetRowCategory = CyclicRowCategories[bestIndex];
                item.isRowLocked = true;
                item.lockRow = bestIndex;
                item.lockRowCycle = CyclicRowCategories.Length;

                // 后续白纸评分应使用钥匙整理后将要提供的分类，而不是原行的分类。
                item.comboCategories.Clear();
                item.comboCategories.Add(item.targetRowCategory);
            }

            string rows = string.Join("/", Enumerable.Range(0, ctx.height)
                .Where(row => row % CyclicRowCategories.Length == bestIndex && row * ctx.width < ctx.storage)
                .Select(row => (row + 1).ToString()).ToArray());
            Plugin.Log.LogInfo(
                $"凯尔萨德尼钥匙：坚固{counts[0]} 余烬{counts[1]} 冰川{counts[2]} 魔法科技{counts[3]}" +
                $" -> 选择{CyclicRowCategoryNames[bestIndex]}，允许第{rows}行。");
        }

        private static bool IsAllowedLockedRow(ItemInfo item, int row)
        {
            if (item == null || !item.isRowLocked)
            {
                return true;
            }
            if (item.lockRowCycle > 0)
            {
                return row % item.lockRowCycle == item.lockRow;
            }
            return row == item.lockRow;
        }

        /// <summary>
        /// 记录按下整理键时已经成立的指北针配对。之后优化器只允许这些指北针继续跟随同一个
        /// 物品实例，不再因为背包里有多个伤害神器而重新选择目标。
        /// </summary>
        private static void CaptureCompassBindings(SearchContext ctx, List<Slot> original)
        {
            for (int cell = ctx.width; cell < ctx.storage && cell < original.Count; cell++)
            {
                Slot compassSlot = original[cell];
                if (compassSlot == null || !compassSlot.hasItem ||
                    !ctx.itemByInstance.TryGetValue(compassSlot.instanceID, out ItemInfo compass) ||
                    compass == null || !compass.isCompass)
                {
                    continue;
                }

                Slot targetSlot = original[cell - ctx.width];
                if (targetSlot == null || !targetSlot.hasItem || targetSlot.charm == null ||
                    !ctx.itemByInstance.TryGetValue(targetSlot.instanceID, out ItemInfo target) || target == null)
                {
                    continue;
                }

                // 游戏只允许指向伤害类神器或另一枚指北针；未配对的针仍交给旧逻辑自行配对。
                if (target.isAttackable || target.isCompass)
                {
                    ctx.compassTargetByInstance[compassSlot.instanceID] = targetSlot.instanceID;
                }
            }

            if (ctx.compassTargetByInstance.Count == 0)
            {
                return;
            }

            // 原始背包中每件物品正下方最多只有一枚指北针，因此绑定天然组成若干条竖向链。
            var compassBelowTarget = new Dictionary<int, int>();
            foreach (var pair in ctx.compassTargetByInstance)
            {
                compassBelowTarget[pair.Value] = pair.Key;
            }

            var visitedCompasses = new HashSet<int>();
            foreach (var pair in ctx.compassTargetByInstance)
            {
                int root = pair.Value;
                if (ctx.compassTargetByInstance.ContainsKey(root))
                {
                    continue; // 目标本身也跟随更上方物品，由更上方的链首统一处理。
                }
                AddCompassChain(ctx, root, compassBelowTarget, visitedCompasses);
            }

            // 防御性兜底：正常竖向布局不会成环；若数据异常，仍把尚未收录的绑定建成链。
            foreach (var pair in ctx.compassTargetByInstance)
            {
                if (!visitedCompasses.Contains(pair.Key))
                {
                    AddCompassChain(ctx, pair.Value, compassBelowTarget, visitedCompasses);
                }
            }
            ctx.compassRootScratch = new int[ctx.compassChains.Count];
        }

        private static void AddCompassChain(SearchContext ctx, int rootInstanceID,
            Dictionary<int, int> compassBelowTarget, HashSet<int> visitedCompasses)
        {
            if (!ctx.itemByInstance.TryGetValue(rootInstanceID, out ItemInfo rootInfo) || rootInfo == null)
            {
                return;
            }

            var chain = new CompassChain { originalRootCell = rootInfo.index };
            chain.instanceIDs.Add(rootInstanceID);
            ctx.compassChainInstances.Add(rootInstanceID);

            int current = rootInstanceID;
            while (compassBelowTarget.TryGetValue(current, out int compassInstanceID) &&
                   visitedCompasses.Add(compassInstanceID))
            {
                chain.instanceIDs.Add(compassInstanceID);
                ctx.compassChainInstances.Add(compassInstanceID);
                current = compassInstanceID;
            }

            if (chain.instanceIDs.Count > 1)
            {
                ctx.compassChains.Add(chain);
            }
        }

        /// <summary>
        /// 建立白纸可补位的连击候选。基础数量优先采用游戏已计算的 currentSetEffectCount，并扣除
        /// 白纸当前临时复制出来的分类；“上限”取该连击效果数据中的最高触发档位。
        /// </summary>
        private static void BuildWhitePaperTargets(SearchContext ctx)
        {
            if (ctx.whitePapers.Count == 0)
            {
                return;
            }

            var physicalCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ItemInfo item in ctx.items)
            {
                if (!item.isCharm || item.isWhitePaper)
                {
                    continue;
                }
                foreach (string category in item.comboCategories)
                {
                    physicalCounts[category] = physicalCounts.TryGetValue(category, out int count)
                        ? count + 1
                        : 1;
                }
            }

            var baseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var pair in ctx.inv.currentSetEffectCount)
                {
                    baseCounts[pair.Key] = pair.Value;
                }
            }
            catch
            {
            }

            // currentSetEffectCount 已包含白纸当前复制的分类，先扣掉，得到“不靠白纸”的真实基础数量。
            foreach (ItemInfo item in ctx.whitePapers)
            {
                if (!(item.slot.charm is Charm_WhitePaper paper))
                {
                    continue;
                }
                try
                {
                    foreach (string category in paper.assignedCategory)
                    {
                        if (baseCounts.TryGetValue(category, out int count))
                        {
                            baseCounts[category] = Math.Max(0, count - 1);
                        }
                    }
                }
                catch
                {
                }
            }

            // currentSetEffectCount 还是整理前的值；钥匙若将换到另一种羁绊行，
            // 先扣除它原来的分类，再由下方 physicalCounts 补入新分类。
            foreach (ItemInfo item in ctx.items)
            {
                if (!item.isCyclicRowCategory || string.IsNullOrEmpty(item.originalRowCategory))
                {
                    continue;
                }
                if (baseCounts.TryGetValue(item.originalRowCategory, out int count))
                {
                    baseCounts[item.originalRowCategory] = Math.Max(0, count - 1);
                }
            }

            // 客户端同步尚未刷新、或钥匙整理后分类已改变时，实际神器数量是最低保证。
            foreach (var pair in physicalCounts)
            {
                baseCounts[pair.Key] = baseCounts.TryGetValue(pair.Key, out int count)
                    ? Math.Max(count, pair.Value)
                    : pair.Value;
            }

            foreach (var pair in baseCounts)
            {
                if (pair.Value < 2 ||
                    !physicalCounts.TryGetValue(pair.Key, out int physicalCount) || physicalCount < 2)
                {
                    continue; // 白纸左右至少需要两件真正带该分类的神器。
                }

                int cap = GetComboEffectCap(ctx, pair.Key, out string displayName);
                if (cap <= pair.Value)
                {
                    continue; // 已经达到最高触发档位，不再浪费白纸。
                }

                ctx.whitePaperTargets.Add(new WhitePaperComboTarget
                {
                    category = pair.Key,
                    displayName = displayName,
                    baseCount = pair.Value,
                    cap = cap
                });
            }

            // 用户期望：优先当前数量最大的未满连击；同数量时优先离上限更近的。
            ctx.whitePaperTargets.Sort((a, b) =>
            {
                int count = b.baseCount.CompareTo(a.baseCount);
                if (count != 0)
                {
                    return count;
                }
                int gap = (a.cap - a.baseCount).CompareTo(b.cap - b.baseCount);
                if (gap != 0)
                {
                    return gap;
                }
                return string.Compare(a.category, b.category, StringComparison.OrdinalIgnoreCase);
            });
            ctx.whitePaperAssignmentScratch = new int[ctx.whitePaperTargets.Count];
        }

        private static int GetComboEffectCap(SearchContext ctx, string category, out string displayName)
        {
            displayName = category;
            int cap = 0;
            try
            {
                ItemCategoryEntity categoryEntity = ItemDatabase.FindItemCategory(category);
                if (categoryEntity == null)
                {
                    return 0;
                }
                if (!string.IsNullOrEmpty(categoryEntity.Name))
                {
                    displayName = categoryEntity.Name;
                }
                if (categoryEntity.setStatus != null)
                {
                    foreach (ItemCategoryEntity.SetTarget target in categoryEntity.setStatus)
                    {
                        if (target != null)
                        {
                            cap = Math.Max(cap, target.itemCount);
                        }
                    }
                }

                ComboEffectBase effect = null;
                try
                {
                    ctx.inv.lastAppliedComboEffects.TryGetValue(category, out effect);
                }
                catch
                {
                }
                if (effect == null && categoryEntity.comboEffectPrefab != null)
                {
                    effect = categoryEntity.comboEffectPrefab.GetComponent<ComboEffectBase>();
                }
                if (effect != null)
                {
                    if (effect.addStatByCombo != null)
                    {
                        foreach (ComboEffectBase.ComboStat stat in effect.addStatByCombo)
                        {
                            if (stat != null)
                            {
                                cap = Math.Max(cap, stat.comboCount);
                            }
                        }
                    }
                    try
                    {
                        List<ComboEffectElement> elements = effect.RequestComboData(ctx.inv.UnitAvatar);
                        if (elements != null)
                        {
                            foreach (ComboEffectElement element in elements)
                            {
                                if (element != null)
                                {
                                    cap = Math.Max(cap, element.comboCount);
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
                return 0;
            }
            return cap;
        }

        private static StelePattern BuildStelePattern(SearchContext ctx, StoneTablet tablet, ItemPosition origin, int rotation)
        {
            var pattern = new StelePattern
            {
                cell = ctx.inv.PosToIdx(new ItemPosition(origin.x, origin.y)),
                rotation = rotation
            };

            var metas = ParseQuerySafe(tablet, origin, rotation, ctx.width, ctx.height, ctx.storage, condition: false);
            foreach (var meta in metas)
            {
                var eff = new StoneTablet.AdditionEffectData(meta);
                if (eff.position.x < 0 || eff.position.y < 0 ||
                    eff.position.x >= ctx.width || eff.position.y >= ctx.height)
                {
                    continue; // 越界格：无害
                }
                int cell = ctx.inv.PosToIdx(new ItemPosition(eff.position.x, eff.position.y));
                switch (eff.effectType)
                {
                    case StoneTablet.EffectType.IncreaseConstLevel:
                        pattern.effects.Add(new EffectEntry { cell = cell, kind = 0, value = eff.levelParam });
                        break;
                    case StoneTablet.EffectType.Disable:
                        pattern.effects.Add(new EffectEntry { cell = cell, kind = 1 });
                        break;
                    case StoneTablet.EffectType.IgnoreCriteria:
                        pattern.effects.Add(new EffectEntry { cell = cell, kind = 2 });
                        break;
                    case StoneTablet.EffectType.MultiplyConstLevel:
                        pattern.effects.Add(new EffectEntry { cell = cell, kind = 3, value = eff.levelParam });
                        break;
                }
            }

            var condMetas = ParseQuerySafe(tablet, origin, rotation, ctx.width, ctx.height, ctx.storage, condition: true);
            foreach (var meta in condMetas)
            {
                var crit = new StoneTablet.AdditionCriteriaData(meta);
                if (crit.effectType == StoneTablet.CriteriaType.None)
                {
                    continue;
                }
                if (crit.position.x < 0 || crit.position.y < 0 ||
                    crit.position.x >= ctx.width || crit.position.y >= ctx.height)
                {
                    continue;
                }
                int cell = ctx.inv.PosToIdx(new ItemPosition(crit.position.x, crit.position.y));
                pattern.conditions.Add(new ConditionEntry { cell = cell, type = crit.effectType });
                if (crit.effectType == StoneTablet.CriteriaType.Placed)
                {
                    pattern.hasPlacedCondition = true;
                }
            }

            return pattern;
        }

        /// <summary>石板条件是否满足（镜像游戏 ApplyEffect 判定）。</summary>
        private static bool ConditionsOk(StelePattern pattern, List<Slot> slots)
        {
            if (pattern.conditions.Count == 0)
            {
                return true;
            }

            bool allOk = true;
            bool hasPlaced = false;
            bool placedHit = false;

            foreach (ConditionEntry c in pattern.conditions)
            {
                Slot s = c.cell >= 0 && c.cell < slots.Count ? slots[c.cell] : null;
                bool ok;
                switch (c.type)
                {
                    case StoneTablet.CriteriaType.AnyItem:
                        ok = s != null && s.hasItem;
                        break;
                    case StoneTablet.CriteriaType.OnlyCharm:
                        ok = s != null && s.hasItem && s.charm != null;
                        break;
                    case StoneTablet.CriteriaType.Placed:
                        ok = true;
                        hasPlaced = true;
                        placedHit |= c.cell == pattern.cell;
                        break;
                    default:
                        ok = true;
                        break;
                }
                allOk &= ok;
            }

            if (!allOk)
            {
                return false;
            }
            if (hasPlaced && !placedHit)
            {
                return false;
            }
            return true;
        }

        private static void ApplyPatternEffects(SearchContext ctx, List<Slot> slots,
            StelePattern pattern, int[] level, bool[] disabled, bool[] ignore)
        {
            if (!ConditionsOk(pattern, slots))
            {
                return;
            }
            // 先应用加等级/禁用/豁免，再统一应用乘法（镜像游戏：全部 adds 后 multiply）
            foreach (EffectEntry e in pattern.effects)
            {
                if (e.cell < 0 || e.cell >= ctx.storage)
                {
                    continue;
                }
                switch (e.kind)
                {
                    case 0:
                        level[e.cell] += e.value;
                        break;
                    case 1:
                        if (disabled != null) disabled[e.cell] = true;
                        break;
                    case 2:
                        if (ignore != null) ignore[e.cell] = true;
                        break;
                }
            }
            foreach (EffectEntry e in pattern.effects)
            {
                if (e.kind == 3 && e.cell >= 0 && e.cell < ctx.storage)
                {
                    level[e.cell] *= e.value;
                }
            }
        }

        /// <summary>独立计算某布局下石板对格位的总贡献（adds 求和后再乘），用于反推基础等级。</summary>
        private static int[] ComputeSteleContribution(SearchContext ctx, List<Slot> slots)
        {
            int[] level = new int[ctx.storage];
            int[] mul = new int[ctx.storage];
            for (int i = 0; i < ctx.storage; i++)
            {
                mul[i] = 1;
            }

            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot s = slots[cell];
                if (s == null || !s.hasItem || s.tablet == null)
                {
                    continue;
                }
                if (!ctx.stelePatterns.TryGetValue(s.tablet.instanceID, out var byCell) ||
                    !byCell.TryGetValue(cell * 4 + s.rotation, out var pattern) ||
                    !ConditionsOk(pattern, slots))
                {
                    continue;
                }
                foreach (EffectEntry e in pattern.effects)
                {
                    if (e.cell < 0 || e.cell >= ctx.storage)
                    {
                        continue;
                    }
                    if (e.kind == 0)
                    {
                        level[e.cell] += e.value;
                    }
                    else if (e.kind == 3)
                    {
                        mul[e.cell] *= e.value;
                    }
                }
            }

            for (int i = 0; i < ctx.storage; i++)
            {
                level[i] *= mul[i];
            }
            return level;
        }

        // ---------------------------------------------------------------- 离线评分

        private static bool HasItemAt(List<Slot> slots, int x, int y, int width, int height)
        {
            if (!InBounds(x, y, width, height))
            {
                return false;
            }
            int idx = y * width + x;
            return idx >= 0 && idx < slots.Count && slots[idx] != null && slots[idx].hasItem;
        }

        private static bool HasCharmAt(List<Slot> slots, int x, int y, int width, int height)
        {
            if (!InBounds(x, y, width, height))
            {
                return false;
            }
            int idx = y * width + x;
            return idx >= 0 && idx < slots.Count && slots[idx] != null && slots[idx].hasItem && slots[idx].charm != null;
        }

        /// <summary>护符条件是否满足（含布局依赖型，基于候选布局与当前格子判定；ignored=豁免格）。</summary>
        private static bool CriteriaSatisfied(SearchContext ctx, ItemInfo info, List<Slot> slots, bool ignored, int cell)
        {
            if (ignored)
            {
                return true;
            }

            int x = cell % ctx.width;
            int y = cell / ctx.width;

            switch (info.kind)
            {
                case CharmPositionKind.Top:
                    return y == 0;
                case CharmPositionKind.Bottom:
                    return cell >= ctx.storage - 6;
                case CharmPositionKind.Side:
                    return x == 0 || x >= ctx.width - 1;
                case CharmPositionKind.Inside:
                    return x > 0 && y > 0 && x < ctx.width - 1 && cell + 7 <= ctx.storage - 1;
                case CharmPositionKind.Outlined:
                    return x <= 0 || y <= 0 || x >= ctx.width - 1 || cell >= ctx.storage - 6;

                case CharmPositionKind.BothSidesEmpty:
                    return x > 0 && x < ctx.width - 1 &&
                           !HasItemAt(slots, x - 1, y, ctx.width, ctx.height) &&
                           !HasItemAt(slots, x + 1, y, ctx.width, ctx.height);

                case CharmPositionKind.BothSideCharm:
                    return x > 0 && x < ctx.width - 1 &&
                           HasCharmAt(slots, x - 1, y, ctx.width, ctx.height) &&
                           HasCharmAt(slots, x + 1, y, ctx.width, ctx.height);

                case CharmPositionKind.NeighborsFull:
                    for (int i = 0; i < 8; i++)
                    {
                        if (!HasItemAt(slots, x + Neighbor8[i].x, y + Neighbor8[i].y, ctx.width, ctx.height))
                        {
                            return false;
                        }
                    }
                    return true;

                case CharmPositionKind.NearMagicBook:
                    for (int i = 0; i < 8; i++)
                    {
                        if (HasCharmAt(slots, x + Neighbor8[i].x, y + Neighbor8[i].y, ctx.width, ctx.height) &&
                            slots[y * ctx.width + (x + Neighbor8[i].x)].charm is Charm_Magic)
                        {
                            return true;
                        }
                    }
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>
        /// 指北针当前是否配对。整理前已配对的针必须仍在同一目标实例正下方；未配对的针沿用
        /// 游戏规则，可指向任意伤害类神器或另一枚指北针。
        /// </summary>
        private static bool IsCompassPaired(SearchContext ctx, List<Slot> slots, int cell, Slot compassSlot)
        {
            int x = cell % ctx.width;
            int y = cell / ctx.width;
            Slot above = At(slots, x, y - 1, ctx.width, ctx.storage);
            if (above == null || !above.hasItem || above.charm == null)
            {
                return false;
            }

            if (ctx.compassTargetByInstance.TryGetValue(compassSlot.instanceID, out int targetInstanceID))
            {
                return above.instanceID == targetInstanceID;
            }

            return ctx.itemByInstance.TryGetValue(above.instanceID, out ItemInfo aboveInfo) &&
                   aboveInfo != null && (aboveInfo.isCompass || aboveInfo.isAttackable);
        }

        private static int CountBrokenCompassBindings(SearchContext ctx, List<Slot> slots)
        {
            if (ctx.compassTargetByInstance.Count == 0)
            {
                return 0;
            }

            int broken = 0;
            int found = 0;
            int storage = Math.Min(ctx.storage, slots != null ? slots.Count : 0);
            for (int cell = 0; cell < storage; cell++)
            {
                Slot slot = slots[cell];
                if (slot == null || !slot.hasItem ||
                    !ctx.compassTargetByInstance.TryGetValue(slot.instanceID, out int targetInstanceID))
                {
                    continue;
                }
                found++;
                int aboveCell = cell - ctx.width;
                if (aboveCell < 0 || aboveCell >= storage ||
                    slots[aboveCell] == null || !slots[aboveCell].hasItem ||
                    slots[aboveCell].instanceID != targetInstanceID)
                {
                    broken++;
                }
            }
            return broken + (ctx.compassTargetByInstance.Count - found);
        }

        private static bool CompassBindingsSatisfied(SearchContext ctx, List<Slot> slots)
        {
            return CountBrokenCompassBindings(ctx, slots) == 0;
        }

        private static bool WhitePaperMatchesTarget(SearchContext ctx, List<Slot> slots,
            int paperCell, int targetIndex)
        {
            if (paperCell < 0 || paperCell >= ctx.storage || targetIndex < 0 ||
                targetIndex >= ctx.whitePaperTargets.Count)
            {
                return false;
            }
            int x = paperCell % ctx.width;
            if (x <= 0 || x >= ctx.width - 1)
            {
                return false;
            }
            int leftCell = paperCell - 1;
            int rightCell = paperCell + 1;
            if (rightCell >= ctx.storage)
            {
                return false;
            }
            Slot left = slots[leftCell];
            Slot right = slots[rightCell];
            if (left == null || right == null || !left.hasItem || !right.hasItem ||
                left.charm == null || right.charm == null ||
                !ctx.itemByInstance.TryGetValue(left.instanceID, out ItemInfo leftInfo) || leftInfo == null ||
                !ctx.itemByInstance.TryGetValue(right.instanceID, out ItemInfo rightInfo) || rightInfo == null ||
                leftInfo.isWhitePaper || rightInfo.isWhitePaper)
            {
                return false;
            }
            string category = ctx.whitePaperTargets[targetIndex].category;
            return leftInfo.comboCategories.Contains(category) && rightInfo.comboCategories.Contains(category);
        }

        /// <summary>统计候选布局中每种连击被多少张白纸复制，并返回没有形成任何有效连击的白纸数量。</summary>
        private static int RefreshWhitePaperAssignments(SearchContext ctx, List<Slot> slots)
        {
            int[] assignments = ctx.whitePaperAssignmentScratch;
            Array.Clear(assignments, 0, assignments.Length);
            int unmatched = 0;
            for (int cell = 0; cell < ctx.storage && cell < slots.Count; cell++)
            {
                Slot slot = slots[cell];
                if (slot == null || !slot.hasItem ||
                    !ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo info) ||
                    info == null || !info.isWhitePaper)
                {
                    continue;
                }

                bool matched = false;
                for (int i = 0; i < ctx.whitePaperTargets.Count; i++)
                {
                    if (WhitePaperMatchesTarget(ctx, slots, cell, i))
                    {
                        assignments[i]++;
                        matched = true;
                    }
                }
                if (!matched)
                {
                    unmatched++;
                }
            }
            return unmatched;
        }

        private double EvaluateWhitePaperSynergy(SearchContext ctx, List<Slot> slots)
        {
            if (plugin.WhitePaperComboBonus.Value <= 0f || ctx.whitePaperTargets.Count == 0)
            {
                return 0d;
            }

            int unmatched = RefreshWhitePaperAssignments(ctx, slots);
            double unit = plugin.WhitePaperComboBonus.Value;
            double score = 0d;
            for (int i = 0; i < ctx.whitePaperTargets.Count; i++)
            {
                WhitePaperComboTarget target = ctx.whitePaperTargets[i];
                int assigned = ctx.whitePaperAssignmentScratch[i];
                int remaining = Math.Max(0, target.cap - target.baseCount);
                int useful = Math.Min(assigned, remaining);
                // 以“整理前的基础数量”为每张白纸的主权重，保证多张白纸时也是先把
                // 数量最大的连击补满，再转向下一个。若按补位后的 newCount 累加，
                // 9/10 + 8/10 两张白纸会与两张都塞给 8/10 得分相同，可能留下 9/10 未满。
                double rankTie = ctx.whitePaperTargets.Count > 0
                    ? 4d * (ctx.whitePaperTargets.Count - i) / ctx.whitePaperTargets.Count
                    : 0d;
                for (int n = 0; n < useful; n++)
                {
                    int newCount = target.baseCount + n + 1;
                    // 当前数量是第一优先级；同数量按候选排序破局，补满再给小额奖励。
                    score += unit * (target.baseCount * 10d + rankTie);
                    if (newCount == target.cap)
                    {
                        score += unit * 5d; // 小于“当前数量差 1”的权重，保持数量最大为第一排序。
                    }
                }
                if (assigned > useful)
                {
                    score -= unit * 20d * (assigned - useful); // 已满后继续堆白纸属于浪费。
                }
            }
            score -= unit * 20d * unmatched;
            return score;
        }

        /// <summary>离线评估布局，返回综合评分（越大越好）。
        /// 结构：游戏评分公式（等级/启用/禁用/负格/溢出）+ 稀有度微调 + 行星聚簇 + 指北针原目标绑定 + 负担强制负格。
        /// 注意：完全按"格子"遍历，物品位置以当前布局为准（勿用 ItemInfo.index，那只是原始位置）。</summary>
        private double EvaluateLayout(SearchContext ctx, List<Slot> slots)
        {
            int storage = Math.Min(ctx.storage, slots != null ? slots.Count : 0);
            int[] level = ctx.cellLevel;
            bool[] disabled = ctx.disabled;
            bool[] ignore = ctx.ignore;

            Array.Copy(ctx.baseLevel, level, storage);
            Array.Clear(disabled, 0, storage);
            Array.Clear(ignore, 0, storage);

            // 石板效果（按当前布局中的石板位置/旋转）
            for (int cell = 0; cell < storage; cell++)
            {
                Slot s = slots[cell];
                if (s == null || !s.hasItem || s.tablet == null)
                {
                    continue;
                }
                if (ctx.stelePatterns.TryGetValue(s.tablet.instanceID, out var byCell) &&
                    byCell.TryGetValue(cell * 4 + s.rotation, out var pattern))
                {
                    ApplyPatternEffects(ctx, slots, pattern, level, disabled, ignore);
                }
            }

            double score = 0;

            // 用户在整理前已经选定的指北针目标属于硬约束。给任何断开的绑定施加远高于
            // 全背包正常评分上限的惩罚，保证搜索不会为了其他局部收益改指向。
            int brokenCompassBindings = CountBrokenCompassBindings(ctx, slots);
            if (brokenCompassBindings > 0)
            {
                score -= brokenCompassBindings * 1000000000d;
            }

            // 负担惩罚基准：格位最低等级（负担应待在最低/负等级格；无负格时放最低格不罚）
            int minLevel = int.MaxValue;
            bool hasBurden = ctx.burdens.Count > 0 && plugin.BurdenPenalty.Value > 0f;
            if (hasBurden)
            {
                for (int i = 0; i < storage; i++)
                {
                    if (level[i] < minLevel)
                    {
                        minLevel = level[i];
                    }
                }
            }

            // 罗盘配对状态：已绑定的针只认整理前的目标；其余针按原游戏规则判定。
            bool[] compassPaired = null;
            if (plugin.CompassBonus.Value > 0f)
            {
                compassPaired = ctx.compassPairedScratch;
                Array.Clear(compassPaired, 0, storage);
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot cs = slots[cell];
                    if (cs == null || !cs.hasItem || cs.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(cs.instanceID, out ItemInfo compass) || compass == null || !compass.isCompass)
                    {
                        continue;
                    }
                    compassPaired[cell] = IsCompassPaired(ctx, slots, cell, cs);
                }
            }

            // 护符/负担评分（按当前布局）
            for (int cell = 0; cell < storage; cell++)
            {
                Slot s = slots[cell];
                if (s == null || !s.hasItem || s.charm == null)
                {
                    continue;
                }
                if (!ctx.itemByInstance.TryGetValue(s.instanceID, out ItemInfo info) || info == null)
                {
                    continue;
                }

                if (info.isBurden)
                {
                    // 负担：不计护符分；所在格越高（离最低格越远）罚越重
                    if (hasBurden)
                    {
                        double excess = level[cell] - minLevel;
                        if (excess > 0)
                        {
                            score -= plugin.BurdenPenalty.Value * excess;
                        }
                    }
                    continue;
                }

                int lvl = (level[cell] + info.enchant) * ctx.mysticFactor[cell];
                // 武器相关护符：游戏内"武器匹配才启用"（flag3 = !isWeaponRelatedCharm || 当前武器类型匹配）
                bool enabled = !disabled[cell] && lvl >= 0 &&
                               CriteriaSatisfied(ctx, info, slots, ignore[cell], cell) &&
                               info.weaponOk;

                // 固定行物品必须保持原行；凯尔萨德尼钥匙则必须处于选中羁绊的同余行。
                if (!IsAllowedLockedRow(info, cell / ctx.width))
                {
                    score -= 100000000d;
                }

                // 永恒蚀（Charm_FireIceWeapon）：冰霜武具>太阳剑 时须在右三列，少于时须在左三列（强约束，同行锁定同级）
                if (info.isEternalEclipse && ctx.frostCount != ctx.flameSwordCount)
                {
                    int ex = cell % ctx.width;
                    bool eclipseRight = ctx.frostCount > ctx.flameSwordCount;
                    if ((eclipseRight && ex < ctx.width / 2) || (!eclipseRight && ex >= ctx.width / 2))
                    {
                        score -= 100000000d;
                    }
                }

                // 对立之秤（Charm_FireIce）：冰川>余烬 须在最右列，少于须在最左列；相等时只能最左或最右列（禁止中间）
                if (info.isOpposingScale)
                {
                    int sx = cell % ctx.width;
                    bool leftOk = sx == 0;
                    bool rightOk = sx == ctx.width - 1;
                    if (ctx.glacierCount > ctx.emberCount)
                    {
                        if (!rightOk) score -= 100000000d;
                    }
                    else if (ctx.glacierCount < ctx.emberCount)
                    {
                        if (!leftOk) score -= 100000000d;
                    }
                    else if (!leftOk && !rightOk)
                    {
                        score -= 100000000d;
                    }
                }

                // 受限护符位置偏好（引导搜索方向）：
                // 冰锁类（preferIgnoreCells）站豁免格 +5000；普通受限站自然满足位置 +500
                if (KindPriority(info.kind) >= 2)
                {
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    bool natural = IsSatisfyingCell(info.kind, x, y, cell, ctx.storage, ctx.width);
                    if (info.preferIgnoreCells)
                    {
                        if (ignore[cell])
                        {
                            score += 5000;
                        }
                        else if (natural)
                        {
                            score += 500;
                        }
                    }
                    else if (natural && !ignore[cell])
                    {
                        score += 500;
                    }
                }

                if (enabled)
                {
                    int eff = Mathf.Clamp(lvl, 0, info.maxLevel);
                    // 指北针：效果只在配对时生效，未配对时等级分大幅打折
                    double levelScore = eff * 10000 * PriorityWeight(info.priority) * info.levelScoreFactor;
                    if (info.manualPriorityRank > 0)
                    {
                        // 后点的排名更高：P1 获得完整强度，P2/P3…每级固定递减 200 分。
                        // 使用独立加分而非乘稀有度权重，确保手动提权始终有效。
                        // 只增加“等级价值”，不会绕过位置、禁用、固定行等硬约束。
                        double rank = info.manualPriorityRank;
                        double manualPerLevel = Math.Max(
                            0d,
                            ctx.manualPriorityStrength - (rank - 1d) * ManualPriorityRankDecay);
                        levelScore += eff * manualPerLevel;
                    }
                    if (info.isCompass && compassPaired != null && !compassPaired[cell])
                    {
                        levelScore *= plugin.CompassUnpairedFactor.Value;
                    }
                    score += levelScore + 1000;
                    if (lvl > info.maxLevel)
                    {
                        score += lvl - info.maxLevel; // 溢出小奖励（镜像游戏）
                    }
                    // 最低等级目标（谱子「银河」=2）：有效等级不足时按缺额额外扣分，保证优先拉到目标等级。
                    if (info.minDesiredLevel > 0 && eff < info.minDesiredLevel)
                    {
                        score -= (info.minDesiredLevel - eff) * 10000d * PriorityWeight(info.priority);
                    }
                }
                else
                {
                    score -= 750;
                }

                // 负等级是扣分：提权神器按其排名的每级提权分扣除，不能被 enabled 门槛吞掉。
                if (info.manualPriorityRank > 0 && lvl < 0)
                {
                    double rank = info.manualPriorityRank;
                    double manualPerLevel = Math.Max(
                        0d,
                        ctx.manualPriorityStrength - (rank - 1d) * ManualPriorityRankDecay);
                    score += lvl * manualPerLevel;
                }

                if (lvl < 0)
                {
                    score -= 250 * -lvl; // 负等级暴露惩罚
                }
            }

            // 行星望远镜：启用时周围八格 PLANET 藏品加成
            if (plugin.PlanetBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot ms = slots[cell];
                    if (ms == null || !ms.hasItem || ms.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(ms.instanceID, out ItemInfo module) || module == null || !module.isPlanetModule)
                    {
                        continue;
                    }
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    bool moduleEnabled = !disabled[cell] &&
                                         ((level[cell] + module.enchant) * ctx.mysticFactor[cell]) >= 0 &&
                                         CriteriaSatisfied(ctx, module, slots, ignore[cell], cell);
                    if (!moduleEnabled)
                    {
                        continue;
                    }
                    int adjacentPlanets = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + Neighbor8[i].x;
                        int ny = y + Neighbor8[i].y;
                        Slot neighbor = At(slots, nx, ny, ctx.width, ctx.storage);
                        if (neighbor != null && neighbor.hasItem &&
                            ctx.itemByInstance.TryGetValue(neighbor.instanceID, out ItemInfo it) &&
                            it != null && it.isPlanetCategory && !it.excludeFromPlanetCluster)
                        {
                            // 聚簇奖励：要求行星自身启用（格位等级≥0，未被禁用）
                            int idx = ny * ctx.width + nx;
                            if (!disabled[idx] && ((level[idx] + it.enchant) * ctx.mysticFactor[idx]) >= 0)
                            {
                                adjacentPlanets++;
                            }
                        }
                    }
                    if (adjacentPlanets > 0)
                    {
                        score += plugin.PlanetBonus.Value * adjacentPlanets;
                    }
                }
            }

            // 和谐之晶：周围8格护符的有效等级之和越高，伤害放大越大（须启用才生效）
            if (plugin.HarmonyLevelBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot hs = slots[cell];
                    if (hs == null || !hs.hasItem || hs.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(hs.instanceID, out ItemInfo harmony) || harmony == null || !harmony.isHarmonyCrystal)
                    {
                        continue;
                    }
                    // 启用检查（与护符一致）
                    bool hEnabled = !disabled[cell] &&
                                    ((level[cell] + harmony.enchant) * ctx.mysticFactor[cell]) >= 0 &&
                                    CriteriaSatisfied(ctx, harmony, slots, ignore[cell], cell) &&
                                    harmony.weaponOk;
                    if (!hEnabled)
                    {
                        continue;
                    }
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    int levelSum = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + Neighbor8[i].x;
                        int ny = y + Neighbor8[i].y;
                        Slot neighbor = At(slots, nx, ny, ctx.width, ctx.storage);
                        if (neighbor == null || !neighbor.hasItem || neighbor.charm == null)
                        {
                            continue;
                        }
                        if (!ctx.itemByInstance.TryGetValue(neighbor.instanceID, out ItemInfo ni) || ni == null || ni.isBurden)
                        {
                            continue;
                        }
                        int nIdx = ny * ctx.width + nx;
                        int nLvl = (level[nIdx] + ni.enchant) * ctx.mysticFactor[nIdx];
                        int eff = Mathf.Clamp(nLvl, 0, ni.maxLevel);
                        levelSum += eff;
                    }
                    if (levelSum > 0)
                    {
                        score += plugin.HarmonyLevelBonus.Value * levelSum;
                    }
                }
            }

            // 奉献徽章：同一横排内的同伴(ICompanionCharm)越多，加成越大（徽章须启用）
            if (plugin.DedicationCompanionBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot ds = slots[cell];
                    if (ds == null || !ds.hasItem || ds.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(ds.instanceID, out ItemInfo badge) || badge == null || !badge.isDedicationBadge)
                    {
                        continue;
                    }
                    bool bEnabled = !disabled[cell] &&
                                    ((level[cell] + badge.enchant) * ctx.mysticFactor[cell]) >= 0 &&
                                    CriteriaSatisfied(ctx, badge, slots, ignore[cell], cell) &&
                                    badge.weaponOk;
                    if (!bEnabled)
                    {
                        continue;
                    }
                    int row = cell / ctx.width;
                    int companions = 0;
                    int rowStart = row * ctx.width;
                    int rowEnd = Math.Min(storage, rowStart + ctx.width);
                    for (int c = rowStart; c < rowEnd; c++)
                    {
                        if (c == cell)
                        {
                            continue;
                        }
                        Slot co = slots[c];
                        if (co == null || !co.hasItem || co.charm == null)
                        {
                            continue;
                        }
                        if (ctx.itemByInstance.TryGetValue(co.instanceID, out ItemInfo ci) &&
                            ci != null && ci.isDedicationCompanion)
                        {
                            companions++;
                        }
                    }
                    if (companions > 0)
                    {
                        score += plugin.DedicationCompanionBonus.Value * companions;
                    }
                }
            }

            // 发光的沙漏（Charm_RightSpellCooldownHelper）：右边一格是魔法书(Charm_Magic)时，
            // 魔法书 CD 恢复速度 +30/60/100%（按沙漏等级）。评分奖励按魔法书 CD 秒数——
            // CD 越长收益越大，搜索会自动把沙漏放到 CD 最长的魔法书左边（沙漏须启用）。
            if (plugin.HourglassBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot gs = slots[cell];
                    if (gs == null || !gs.hasItem || gs.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(gs.instanceID, out ItemInfo hour) || hour == null || !hour.isHourglass)
                    {
                        continue;
                    }
                    bool gEnabled = !disabled[cell] &&
                                    ((level[cell] + hour.enchant) * ctx.mysticFactor[cell]) >= 0 &&
                                    CriteriaSatisfied(ctx, hour, slots, ignore[cell], cell) &&
                                    hour.weaponOk;
                    if (!gEnabled)
                    {
                        continue;
                    }
                    int gx = cell % ctx.width;
                    int gy = cell / ctx.width;
                    Slot right = At(slots, gx + 1, gy, ctx.width, ctx.storage);
                    if (right == null || !right.hasItem || right.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(right.instanceID, out ItemInfo magic) ||
                        magic == null || !magic.isMagicBook)
                    {
                        continue;
                    }
                    score += plugin.HourglassBonus.Value * Math.Max(0f, magic.magicCd);
                }
            }

            // 雷伊星碎片（Item_DoubleMagic_Name）：左侧一格是魔法书(Charm_Magic)时生效（与沙漏方向相反）。
            // 评分奖励按魔法书耗蓝量——耗蓝越高收益越大，搜索会把碎片放到耗蓝最高的魔法书右侧。
            if (plugin.RayShardBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot rs = slots[cell];
                    if (rs == null || !rs.hasItem || rs.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(rs.instanceID, out ItemInfo shard) || shard == null || !shard.isRayShard)
                    {
                        continue;
                    }
                    bool sEnabled = !disabled[cell] &&
                                    ((level[cell] + shard.enchant) * ctx.mysticFactor[cell]) >= 0 &&
                                    CriteriaSatisfied(ctx, shard, slots, ignore[cell], cell) &&
                                    shard.weaponOk;
                    if (!sEnabled)
                    {
                        continue;
                    }
                    int rx = cell % ctx.width;
                    int ry = cell / ctx.width;
                    Slot left = At(slots, rx - 1, ry, ctx.width, ctx.storage);
                    if (left == null || !left.hasItem || left.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(left.instanceID, out ItemInfo magic2) ||
                        magic2 == null || !magic2.isMagicBook)
                    {
                        continue;
                    }
                    score += plugin.RayShardBonus.Value * Math.Max(0, magic2.magicMpCost);
                }
            }

            // 多用途腰带（Charm_WoodenBox 类）：启用时，背包最上行(y=0)每有一件神器，效果叠加一次。
            // 评分奖励第一行神器数量，引导搜索把神器堆满第一行（多块腰带只按一块计，避免重复叠加）。
            if (ctx.hasBelt && plugin.BeltRowBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot bs = slots[cell];
                    if (bs == null || !bs.hasItem || bs.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(bs.instanceID, out ItemInfo belt) ||
                        belt == null || !belt.isBelt)
                    {
                        continue;
                    }
                    bool bEnabled = !disabled[cell] &&
                                    ((level[cell] + belt.enchant) * ctx.mysticFactor[cell]) >= 0 &&
                                    CriteriaSatisfied(ctx, belt, slots, ignore[cell], cell) &&
                                    belt.weaponOk;
                    if (!bEnabled)
                    {
                        continue;
                    }
                    int firstRowArtifacts = 0;
                    int rowEnd = Math.Min(storage, ctx.width);
                    for (int c = 0; c < rowEnd; c++)
                    {
                        Slot rs = slots[c];
                        if (rs != null && rs.hasItem && rs.charm != null &&
                            ctx.itemByInstance.TryGetValue(rs.instanceID, out ItemInfo ri) &&
                            ri != null && !ri.isBurden)
                        {
                            firstRowArtifacts++;
                        }
                    }
                    score += plugin.BeltRowBonus.Value * firstRowArtifacts;
                    break;
                }
            }

            // 指北针：整理前已配对的针只给原目标加成，并随原目标移动；未配对的针仍可自动
            // 寻找任意伤害类藏品或另一块指北针（可链式叠加）。
            // 注意：游戏 OnRequestCharmDamageBonus 无 IsEffectEnabled 检查——指北针即使在负等级格上也给上方藏品加成，
            // 因此配对判定不要求指北针自身启用。
            if (plugin.CompassBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot cs = slots[cell];
                    if (cs == null || !cs.hasItem || cs.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(cs.instanceID, out ItemInfo compass) || compass == null || !compass.isCompass)
                    {
                        continue;
                    }
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    Slot above = At(slots, x, y - 1, ctx.width, ctx.storage);
                    if (above != null && above.hasItem && above.charm != null &&
                        IsCompassPaired(ctx, slots, cell, cs))
                    {
                        // 按上方目标的优先级加权；对于已绑定的针，这里必定是整理前的同一实例。
                        double w = 1.0;
                        if (ctx.itemByInstance.TryGetValue(above.instanceID, out ItemInfo aboveInfo) && aboveInfo != null)
                        {
                            w = PriorityWeight(aboveInfo.priority);
                        }
                        score += plugin.CompassBonus.Value * w;
                    }
                }
            }

            score += EvaluateWhitePaperSynergy(ctx, slots);

            return score;
        }

        /// <summary>物品是否匹配配置 token：LocalizedString key 或护符类名（大小写不敏感，如 Charm_ShadowEye）。</summary>
        private static bool MatchesItemKey(ItemInfo info, string token)
        {
            token = token.Trim();
            if (token.Length == 0)
            {
                return false;
            }
            if (info.entity != null && info.entity.aName != null &&
                string.Equals(info.entity.aName.key, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (info.slot != null && info.slot.charm != null &&
                string.Equals(info.slot.charm.GetType().Name, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        /// <summary>稀有度 → 用户优先级（1最高~4最低）：传说/羁绊(永恒)=1 稀有=2 高级=3 普通=4。</summary>
        private int RarityToPriority(EItemRarity rarity)
        {
            switch (rarity)
            {
                case EItemRarity.Legend: return Mathf.Clamp(plugin.PriorityLegend.Value, 1, 4);
                case EItemRarity.Eternal: return Mathf.Clamp(plugin.PriorityEternal.Value, 1, 4);
                case EItemRarity.Rare: return Mathf.Clamp(plugin.PriorityRare.Value, 1, 4);
                case EItemRarity.Uncommon: return Mathf.Clamp(plugin.PriorityUncommon.Value, 1, 4);
                default: return Mathf.Clamp(plugin.PriorityCommon.Value, 1, 4);
            }
        }

        /// <summary>优先级 → 等级分权重（1级最高）。</summary>
        private double PriorityWeight(int priority)
        {
            if (!plugin.PriorityEnable.Value)
            {
                return 1.0;
            }
            switch (priority)
            {
                case 1: return plugin.PriorityWeight1.Value;
                case 2: return plugin.PriorityWeight2.Value;
                case 3: return plugin.PriorityWeight3.Value;
                default: return plugin.PriorityWeight4.Value;
            }
        }

        /// <summary>输出完整布局网格图（物品类型缩写 + 格位等级），用于定位摆放问题。</summary>
        private void LogLayoutGrid(SearchContext ctx, List<Slot> slots, string tag)
        {
            EvaluateLayout(ctx, slots); // 刷新等级缓冲
            var sb = new System.Text.StringBuilder();
            sb.Append($"{tag} 布局图(等级/物品)：");
            for (int y = 0; y < ctx.height; y++)
            {
                for (int x = 0; x < ctx.width; x++)
                {
                    int cell = y * ctx.width + x;
                    if (cell >= ctx.storage)
                    {
                        break;
                    }
                    int lvl = ctx.cellLevel[cell];
                    char kind = '.';
                    Slot s = slots[cell];
                    if (s != null && s.hasItem)
                    {
                        if (s.tablet != null) kind = 'T';
                        else if (s.charm != null)
                        {
                            if (ctx.itemByInstance.TryGetValue(s.instanceID, out ItemInfo it) && it != null)
                            {
                                if (it.isBurden) kind = 'B';
                                else if (it.isPlanetModule) kind = 'M';
                                else if (it.isCyclicRowCategory) kind = 'K';
                                else if (it.isWhitePaper) kind = 'W';
                                else if (it.isCompass) kind = 'C';
                                else if (it.isPlanetCategory) kind = 'P';
                                else kind = 'c';
                            }
                            else kind = 'c';
                        }
                        else kind = 'o';
                    }
                    sb.Append($"[{lvl,2}{kind}]");
                }
                sb.Append(" | ");
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>输出最终布局中特殊机制的落地情况（望远镜聚星/指北针绑定/负担负格），用于验证。</summary>
        private static void LogLayoutAnalysis(SearchContext ctx, List<Slot> slots, string tag)
        {
            int w = ctx.width;

            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot s = slots[cell];
                if (s == null || !s.hasItem)
                {
                    continue;
                }
                if (!ctx.itemByInstance.TryGetValue(s.instanceID, out ItemInfo it) || it == null)
                {
                    continue;
                }
                int x = cell % w;
                int y = cell / w;

                if (it.isPlanetModule)
                {
                    int planets = 0;
                    string list = "";
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + Neighbor8[i].x;
                        int ny = y + Neighbor8[i].y;
                        Slot o = At(slots, nx, ny, w, ctx.storage);
                        if (o != null && o.hasItem &&
                            ctx.itemByInstance.TryGetValue(o.instanceID, out ItemInfo oi) &&
                            oi != null && oi.isPlanetCategory)
                        {
                            planets++;
                            list += $"({nx},{ny}) ";
                        }
                    }
                    Plugin.Log.LogInfo($"{tag} 望远镜@{x},{y}：相邻行星 {planets} 颗 {list.Trim()}");
                }

                if (it.isHarmonyCrystal)
                {
                    int levelSum = 0;
                    string list = "";
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + Neighbor8[i].x;
                        int ny = y + Neighbor8[i].y;
                        Slot o = At(slots, nx, ny, w, ctx.storage);
                        if (o != null && o.hasItem && o.charm != null &&
                            ctx.itemByInstance.TryGetValue(o.instanceID, out ItemInfo oi) && oi != null)
                        {
                            int oIdx = ny * w + nx;
                            int eff = Mathf.Clamp((ctx.cellLevel[oIdx] + oi.enchant) * ctx.mysticFactor[oIdx], 0, oi.maxLevel);
                            levelSum += eff;
                            list += $"({nx},{ny}:{eff}) ";
                        }
                    }
                    Plugin.Log.LogInfo($"{tag} 和谐之晶@{x},{y}：周围8格等级和={levelSum} {list.Trim()}");
                }

                if (it.isDedicationBadge)
                {
                    int companions = 0;
                    string list = "";
                    for (int i = 0; i < w; i++)
                    {
                        int idx = y * w + i;
                        if (idx >= ctx.storage || idx == cell)
                        {
                            continue;
                        }
                        Slot o = slots[idx];
                        if (o != null && o.hasItem &&
                            ctx.itemByInstance.TryGetValue(o.instanceID, out ItemInfo oi) &&
                            oi != null && oi.isDedicationCompanion)
                        {
                            companions++;
                            list += $"({i},{y}) ";
                        }
                    }
                    Plugin.Log.LogInfo($"{tag} 奉献徽章@{x},{y}：同行同伴 {companions} 个 {list.Trim()}");
                }

                if (it.isHourglass)
                {
                    Slot right = At(slots, x + 1, y, w, ctx.storage);
                    if (right != null && right.hasItem && right.charm is Charm_Magic)
                    {
                        float cd = 0f;
                        if (ctx.itemByInstance.TryGetValue(right.instanceID, out ItemInfo ri) && ri != null)
                        {
                            cd = ri.magicCd;
                        }
                        Plugin.Log.LogInfo($"{tag} 沙漏@{x},{y}：右边魔法书 CD={cd:F1}s");
                    }
                    else
                    {
                        Plugin.Log.LogInfo($"{tag} 沙漏@{x},{y}：右边无魔法书（未配对）");
                    }
                }

                if (it.isRayShard)
                {
                    Slot left = At(slots, x - 1, y, w, ctx.storage);
                    if (left != null && left.hasItem && left.charm is Charm_Magic)
                    {
                        int mp = 0;
                        if (ctx.itemByInstance.TryGetValue(left.instanceID, out ItemInfo ri2) && ri2 != null)
                        {
                            mp = ri2.magicMpCost;
                        }
                        Plugin.Log.LogInfo($"{tag} 雷伊星碎片@{x},{y}：左侧魔法书耗蓝={mp}");
                    }
                    else
                    {
                        Plugin.Log.LogInfo($"{tag} 雷伊星碎片@{x},{y}：左侧无魔法书（未配对）");
                    }
                }

                if (it.isEternalEclipse)
                {
                    string rule;
                    if (ctx.frostCount == ctx.flameSwordCount)
                    {
                        rule = "无限制（冰霜武具=太阳剑）";
                    }
                    else
                    {
                        bool right = ctx.frostCount > ctx.flameSwordCount;
                        rule = $"{(right ? "右三列" : "左三列")}（冰霜武具{ctx.frostCount} > 太阳剑{ctx.flameSwordCount} = {(right ? "是" : "否")}）";
                    }
                    string pos = x < ctx.width / 2 ? "左三列" : "右三列";
                    Plugin.Log.LogInfo($"{tag} 永恒蚀@{x},{y}（{pos}）：{rule}");
                }

                if (it.isOpposingScale)
                {
                    string rule;
                    if (ctx.glacierCount > ctx.emberCount)
                    {
                        rule = $"最右列（冰川{ctx.glacierCount} > 余烬{ctx.emberCount}）";
                    }
                    else if (ctx.glacierCount < ctx.emberCount)
                    {
                        rule = $"最左列（冰川{ctx.glacierCount} < 余烬{ctx.emberCount}）";
                    }
                    else
                    {
                        rule = $"最左/最右列（冰川{ctx.glacierCount} = 余烬{ctx.emberCount}）";
                    }
                    string pos = x == 0 ? "最左列" : (x == ctx.width - 1 ? "最右列" : "中间");
                    Plugin.Log.LogInfo($"{tag} 对立之秤@{x},{y}（{pos}）：{rule}");
                }

                if (it.isBelt)
                {
                    int firstRow = 0;
                    for (int c = 0; c < Math.Min(ctx.storage, w); c++)
                    {
                        Slot rs = slots[c];
                        if (rs != null && rs.hasItem && rs.charm != null &&
                            ctx.itemByInstance.TryGetValue(rs.instanceID, out ItemInfo ri) &&
                            ri != null && !ri.isBurden)
                        {
                            firstRow++;
                        }
                    }
                    Plugin.Log.LogInfo($"{tag} 腰带@{x},{y}：第一行神器 {firstRow} 件");
                }

                if (it.isCyclicRowCategory)
                {
                    int categoryIndex = y % CyclicRowCategories.Length;
                    string categoryName = CyclicRowCategoryNames[categoryIndex];
                    string expected = string.Equals(CyclicRowCategories[categoryIndex], it.targetRowCategory,
                        StringComparison.OrdinalIgnoreCase) ? "目标✓" : $"目标应为{it.targetRowCategory}";
                    Plugin.Log.LogInfo($"{tag} 凯尔萨德尼钥匙@{x},{y}：第{y + 1}行={categoryName} {expected}");
                }

                if (it.isCompass)
                {
                    string above = "空";
                    string abovePrio = "";
                    string binding = "";
                    Slot a = At(slots, x, y - 1, w, ctx.storage);
                    if (a != null && a.hasItem && a.charm != null)
                    {
                        above = a.charm is Charm_UpCharmDamage ? "指北针" :
                                (a.charm is IAttackableCharm ac && ac.IsAttackableCharm()) ? "伤害类" : "其他";
                        if (ctx.itemByInstance.TryGetValue(a.instanceID, out ItemInfo ai) && ai != null)
                        {
                            abovePrio = $" P{ai.priority}";
                        }
                    }
                    if (ctx.compassTargetByInstance.TryGetValue(s.instanceID, out int targetInstanceID))
                    {
                        binding = a != null && a.hasItem && a.instanceID == targetInstanceID
                            ? " 原目标✓"
                            : $" 原目标绑定异常(期望实例{targetInstanceID})";
                    }
                    Plugin.Log.LogInfo($"{tag} 罗盘@{x},{y}：上方={above}{abovePrio}{binding}");
                }

                if (it.isWhitePaper)
                {
                    var matches = new System.Text.StringBuilder();
                    for (int i = 0; i < ctx.whitePaperTargets.Count; i++)
                    {
                        if (WhitePaperMatchesTarget(ctx, slots, cell, i))
                        {
                            WhitePaperComboTarget target = ctx.whitePaperTargets[i];
                            matches.Append($" {target.displayName}({target.baseCount}→{Math.Min(target.cap, target.baseCount + 1)}/{target.cap})");
                        }
                    }
                    Plugin.Log.LogInfo(matches.Length > 0
                        ? $"{tag} 白纸@{x},{y}：复制连击{matches}"
                        : $"{tag} 白纸@{x},{y}：左右未形成可补位的同连击");
                }

                if (it.isBurden)
                {
                    int lvl = ctx.cellLevel[cell];
                    Plugin.Log.LogInfo($"{tag} 负担@{x},{y}：格等级={lvl}（{(lvl < 0 ? "负格✓" : "非负格")}）");
                }
            }
        }

        /// <summary>输出本次整理识别到的特殊物品统计（望远镜/罗盘/负担/附魔/稀有度），便于排查与验证。</summary>
        private static void LogItemIdentification(SearchContext ctx)
        {
            int telescopes = 0, compasses = 0, burdens = 0, attackable = 0, planets = 0, enchanted = 0, enchantSum = 0;
            int weaponRelated = 0, weaponMatched = 0;
            int dedicationBadges = 0, dedicationCompanions = 0;
            int hourglasses = 0, magicBooks = 0, eclipses = 0, scales = 0, rayShards = 0;
            int belts = 0, lowValue = 0, minLevel = 0;
            int[] rarityCount = new int[5];
            int[] priorityCount = new int[5];
            foreach (ItemInfo it in ctx.items)
            {
                if (it.isBurden) burdens++;
                if (it.isPlanetModule) telescopes++;
                if (it.isCompass) compasses++;
                if (it.isAttackable && !it.isCompass) attackable++;
                if (it.isPlanetCategory) planets++;
                if (it.enchant != 0) { enchanted++; enchantSum += it.enchant; }
                if (it.isDedicationBadge) dedicationBadges++;
                if (it.isDedicationCompanion) dedicationCompanions++;
                if (it.isHourglass) hourglasses++;
                if (it.isMagicBook) magicBooks++;
                if (it.isRayShard) rayShards++;
                if (it.isEternalEclipse) eclipses++;
                if (it.isOpposingScale) scales++;
                if (it.isBelt) belts++;
                if (it.lowLevelValue) lowValue++;
                if (it.minDesiredLevel > 0) minLevel++;
                if (it.isCharm && !it.isBurden)
                {
                    rarityCount[(int)it.rarity]++;
                    if (it.priority >= 1 && it.priority <= 4) priorityCount[it.priority]++;
                    if (it.slot.charm.isWeaponRelatedCharm)
                    {
                        weaponRelated++;
                        var wc = it.slot.charm.WeaponController;
                        if (wc != null && wc.currentWeapon != null &&
                            wc.currentWeapon.weaponType == it.slot.charm.relatedWeapon)
                        {
                            weaponMatched++;
                        }
                    }
                }
            }
            Plugin.Log.LogInfo(
                $"识别：存储{ctx.storage}格({ctx.width}x{ctx.height}) 石板{ctx.steles.Count} 护符{ctx.charms.Count}" +
                $" 望远镜{telescopes} 罗盘{compasses}(锁定原目标{ctx.compassTargetByInstance.Count})" +
                $" 伤害类{attackable} 行星类{planets} 负担{burdens}" +
                $" 神秘{ctx.mysticCount}个/×2地块{ctx.mysticActiveCells}格" +
                $" 附魔{enchanted}件(+{enchantSum}) 武器相关{weaponRelated}(匹配{weaponMatched})" +
                $" 奉献徽章{dedicationBadges} 同伴{dedicationCompanions}" +
                $" 沙漏{hourglasses} 魔法书{magicBooks} 雷伊星碎片{rayShards} 白纸{ctx.whitePapers.Count}" +
                $" 永恒蚀{eclipses}(冰霜武具{ctx.frostCount}/太阳剑{ctx.flameSwordCount})" +
                $" 对立之秤{scales}(冰川{ctx.glacierCount}/余烬{ctx.emberCount})" +
                $" 腰带{belts} 低等级价值{lowValue} 最低等级目标{minLevel}" +
                $" 优先级[P1:{priorityCount[1]} P2:{priorityCount[2]} P3:{priorityCount[3]} P4:{priorityCount[4]}]" +
                $" 稀有度[普通{rarityCount[0]} 优秀{rarityCount[1]} 稀有{rarityCount[2]} 传说{rarityCount[3]} 永恒{rarityCount[4]}]");

            // 诊断：打印全部护符的 LocalizedString key 与类名（用于往配置里填 key）
            var tagList = new System.Text.StringBuilder();
            foreach (ItemInfo it in ctx.charms)
            {
                string typeName = it.slot != null && it.slot.charm != null ? it.slot.charm.GetType().Name : "?";
                string key = it.entity != null && it.entity.aName != null ? it.entity.aName.key : "?";
                tagList.Append($" [{key}|{typeName}]");
            }
            if (tagList.Length > 0)
            {
                Plugin.Log.LogInfo($"护符清单（key|类名）:{tagList}");
            }
            if (ctx.whitePapers.Count > 0)
            {
                if (ctx.whitePaperTargets.Count == 0)
                {
                    Plugin.Log.LogInfo("白纸：未找到同时具备两件神器且尚未达到最高档位的连击。");
                }
                else
                {
                    var targets = new System.Text.StringBuilder();
                    int limit = Math.Min(5, ctx.whitePaperTargets.Count);
                    for (int i = 0; i < limit; i++)
                    {
                        WhitePaperComboTarget target = ctx.whitePaperTargets[i];
                        targets.Append($" [{target.displayName}:{target.baseCount}/{target.cap}]");
                    }
                    Plugin.Log.LogInfo($"白纸补位候选（按优先级）:{targets}");
                }
            }
        }

        // ---------------------------------------------------------------- 智能初始布局

        private List<Slot> BuildSmartStart(SearchContext ctx)
        {
            int storage = ctx.storage;
            Slot[] result = new Slot[storage];
            bool[] occupied = new bool[storage];

            // 1) 石板：有负效果的优先摆，逐格逐旋转打分（含条件检查）。
            // 已放石板的效果格留给神器，避免后续石板吞掉正等级加成。
            var steles = new List<ItemInfo>(ctx.steles);
            steles.Sort((a, b) => b.steleImportance.CompareTo(a.steleImportance));
            var steleEffectCells = new HashSet<int>();
            foreach (ItemInfo stele in steles)
            {
                int bestIdx = -1;
                int bestRot = stele.slot.rotation;
                float bestScore = float.MinValue;

                int rotations = stele.tabletRotatable ? 4 : 1;
                for (int cell = 0; cell < storage; cell++)
                {
                    if (occupied[cell])
                    {
                        continue;
                    }
                    for (int rot = 0; rot < rotations; rot++)
                    {
                        // stelePatterns 以石板 instanceID 为键；不能使用背包格子索引。
                        if (ctx.stelePatterns.TryGetValue(stele.slot.instanceID, out var byCell) &&
                            byCell.TryGetValue(cell * 4 + rot, out var pattern))
                        {
                            float sc = EvaluateStelePattern(ctx, pattern, result, occupied, steleEffectCells);
                            if (sc > bestScore)
                            {
                                bestScore = sc;
                                bestIdx = cell;
                                bestRot = rot;
                            }
                        }
                    }
                }

                if (bestIdx >= 0)
                {
                    // 智能起点必须保持纯函数：不能修改 ItemInfo.slot（它属于整理前校验快照）。
                    Slot placedStele = stele.slot.Clone();
                    placedStele.rotation = bestRot;
                    result[bestIdx] = placedStele;
                    occupied[bestIdx] = true;
                    if (ctx.stelePatterns.TryGetValue(stele.slot.instanceID, out var byCell) &&
                        byCell.TryGetValue(bestIdx * 4 + bestRot, out var placedPattern))
                    {
                        foreach (EffectEntry effect in placedPattern.effects)
                        {
                            if (effect.cell >= 0 && effect.cell < storage)
                            {
                                steleEffectCells.Add(effect.cell);
                            }
                        }
                    }
                }
            }

            // 预评布局（用于给护符选格）：先评估一次，刷新等级/禁用/豁免缓冲
            var slotsNow = SlotsFromArray(result, storage);
            EvaluateLayout(ctx, slotsNow);

            // 2) 护符放置顺序：受限护符(位置条件) → 望远镜 → 行星聚簇 → 指北针原目标绑定 → 其余(稀有度)
            var remaining = new List<ItemInfo>(ctx.charms);

            // 2a) 位置条件受限的护符（必须满足条件才生效；同条件等级内按用户优先级）
            var restricted = remaining.FindAll(x => KindPriority(x.kind) >= 2);
            restricted.Sort((a, b) =>
            {
                int p = KindPriority(b.kind).CompareTo(KindPriority(a.kind));
                if (p != 0)
                {
                    return p;
                }
                p = CompareManualPriority(a, b);
                return p != 0 ? p : a.priority.CompareTo(b.priority);
            });
            foreach (ItemInfo charm in restricted)
            {
                int cell = FindBestCharmCell(ctx, charm, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = charm.slot.Clone();
                    occupied[cell] = true;
                    remaining.Remove(charm);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 2a2) 行约束物品：普通配置项保持原行；凯尔萨德尼钥匙可在选中羁绊的周期行中择优。
            var rowLocked = remaining.FindAll(x => x.isRowLocked);
            foreach (ItemInfo rl in rowLocked)
            {
                int cell = FindBestCharmCell(ctx, rl, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFreeForLockedItem(occupied, rl, ctx);
                }
                if (cell >= 0)
                {
                    result[cell] = rl.slot.Clone();
                    occupied[cell] = true;
                    remaining.Remove(rl);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 2b) 行星望远镜
            int telescopeCell = -1;
            ItemInfo telescope = remaining.Find(x => x.isPlanetModule);
            if (telescope != null)
            {
                int cell = FindBestCharmCell(ctx, telescope, result, occupied, slotsNow);
                if (cell >= 0)
                {
                    result[cell] = telescope.slot.Clone();
                    occupied[cell] = true;
                    telescopeCell = cell;
                    remaining.Remove(telescope);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 2b2) 和谐之晶：先放（周围8格等级和→伤害放大），其8邻域格加权吸引高等级护符
            var harmonyNeighbors = new HashSet<int>();
            var harmonyCrystals = remaining.FindAll(x => x.isHarmonyCrystal);
            foreach (ItemInfo hc in harmonyCrystals)
            {
                int cell = FindBestCharmCell(ctx, hc, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = hc.slot.Clone();
                    occupied[cell] = true;
                    remaining.Remove(hc);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                    // 收集8邻域格（未占用的）
                    int hx = cell % ctx.width;
                    int hy = cell / ctx.width;
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = hx + Neighbor8[i].x;
                        int ny = hy + Neighbor8[i].y;
                        if (InBounds(nx, ny, ctx.width, ctx.height))
                        {
                            int idx = ny * ctx.width + nx;
                            if (idx >= 0 && idx < ctx.storage && !occupied[idx])
                            {
                                harmonyNeighbors.Add(idx);
                            }
                        }
                    }
                }
            }

            // 2b3) 奉献徽章（Charm_CompanionChaos）：先放，记录其横排；同行空格加权引导同伴同排（金色手铃/迷你弩炮/灵魂粉末/采矿臂章）
            int dedicationRow = -1;
            var dedicationBadges = remaining.FindAll(x => x.isDedicationBadge);
            foreach (ItemInfo db in dedicationBadges)
            {
                int cell = FindBestCharmCell(ctx, db, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = db.slot.Clone();
                    occupied[cell] = true;
                    dedicationRow = cell / ctx.width;
                    remaining.Remove(db);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 2b4) 发光的沙漏（Charm_RightSpellCooldownHelper）：先放（自动避开最右列），记录其右边格，
            //      引导魔法书（CD 越长越优先）就位到沙漏右边
            var hourglassRightCells = new HashSet<int>();
            var hourglasses = remaining.FindAll(x => x.isHourglass);
            foreach (ItemInfo hg in hourglasses)
            {
                int cell = FindBestCharmCell(ctx, hg, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = hg.slot.Clone();
                    occupied[cell] = true;
                    remaining.Remove(hg);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                    int hx = cell % ctx.width;
                    int hy = cell / ctx.width;
                    int ridx = hy * ctx.width + (hx + 1);
                    if (hx + 1 < ctx.width && ridx >= 0 && ridx < ctx.storage && !occupied[ridx])
                    {
                        hourglassRightCells.Add(ridx);
                    }
                }
            }

            // 2b5) 雷伊星碎片（Item_DoubleMagic_Name）：先放（自动避开最左列），记录其左侧格，
            //      引导耗蓝最高的魔法书就位到碎片左侧
            var rayShardLeftCells = new HashSet<int>();
            var rayShards = remaining.FindAll(x => x.isRayShard);
            foreach (ItemInfo shard in rayShards)
            {
                int cell = FindBestCharmCell(ctx, shard, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = shard.slot.Clone();
                    occupied[cell] = true;
                    remaining.Remove(shard);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                    int sx = cell % ctx.width;
                    int sy = cell / ctx.width;
                    int lidx = sy * ctx.width + (sx - 1);
                    if (sx - 1 >= 0 && lidx >= 0 && lidx < ctx.storage && !occupied[lidx])
                    {
                        rayShardLeftCells.Add(lidx);
                    }
                }
            }

            // 2c) 行星藏品聚到望远镜周围（望远镜已放置时）
            if (telescopeCell >= 0)
            {
                int tx = telescopeCell % ctx.width;
                int ty = telescopeCell / ctx.width;
                var planets = remaining.FindAll(x => x.isPlanetCategory && !x.excludeFromPlanetCluster);
                foreach (ItemInfo planet in planets)
                {
                    // 找望远镜相邻且空闲的格子，等级高者优先；跳过负等级/禁用格（行星需要启用才吃到聚簇奖励）
                    int bestAdj = -1;
                    float bestAdjScore = float.MinValue;
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = tx + Neighbor8[i].x;
                        int ny = ty + Neighbor8[i].y;
                        if (!InBounds(nx, ny, ctx.width, ctx.height))
                        {
                            continue;
                        }
                        int idx = ny * ctx.width + nx;
                        if (idx >= 0 && idx < ctx.storage &&
                            !occupied[idx] && ctx.cellLevel[idx] >= 0 && !ctx.disabled[idx])
                        {
                            float sc = ctx.cellLevel[idx] * 100f;
                            if (sc > bestAdjScore)
                            {
                                bestAdjScore = sc;
                                bestAdj = idx;
                            }
                        }
                    }
                    int target = bestAdj >= 0 ? bestAdj : FindBestCharmCell(ctx, planet, result, occupied, slotsNow, harmonyNeighbors, dedicationRow);
                    if (target < 0)
                    {
                        target = FirstFree(occupied);
                    }
                    if (target >= 0)
                    {
                        result[target] = planet.slot.Clone();
                        occupied[target] = true;
                        remaining.Remove(planet);
                        slotsNow = SlotsFromArray(result, storage);
                        EvaluateLayout(ctx, slotsNow);
                    }
                }
            }

            // 2d) 指北针：先按通用规则落位；函数末尾会把整理前已配对的针恢复到原目标正下方。
            var compasses = remaining.FindAll(x => x.isCompass);
            foreach (ItemInfo compass in compasses)
            {
                int target = ctx.compassTargetByInstance.ContainsKey(compass.slot.instanceID)
                    ? -1
                    : FindCompassTargetCell(ctx, result, occupied);
                if (target < 0)
                {
                    target = FindBestCharmCell(ctx, compass, result, occupied, slotsNow, harmonyNeighbors, dedicationRow);
                }
                if (target < 0)
                {
                    target = FirstFree(occupied);
                }
                if (target >= 0)
                {
                    result[target] = compass.slot.Clone();
                    occupied[target] = true;
                    remaining.Remove(compass);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 2e) 其余护符按用户优先级（1→4），同优先级内按稀有度
            remaining.Sort((a, b) =>
            {
                int p = CompareManualPriority(a, b);
                if (p != 0)
                {
                    return p;
                }
                p = a.priority.CompareTo(b.priority);
                if (p != 0)
                {
                    return p;
                }
                return b.rarity.CompareTo(a.rarity);
            });
            foreach (ItemInfo charm in remaining)
            {
                int cell = FindBestCharmCell(ctx, charm, result, occupied, slotsNow, harmonyNeighbors, dedicationRow, hourglassRightCells, rayShardLeftCells);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = charm.slot.Clone();
                    occupied[cell] = true;
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 3) 其余物品填空
            foreach (ItemInfo other in ctx.others)
            {
                int cell = FirstFree(occupied);
                if (cell >= 0)
                {
                    result[cell] = other.slot.Clone();
                    occupied[cell] = true;
                }
            }

            // 4) 负面藏品：塞进最差（负等级最高）的格子
            foreach (ItemInfo burden in ctx.burdens)
            {
                int cell = FindWorstCell(ctx, result, occupied);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = burden.slot.Clone();
                    occupied[cell] = true;
                }
            }

            List<Slot> smart = SlotsFromArray(result, storage);
            if (!RestoreCompassBindings(ctx, smart))
            {
                Plugin.Log.LogWarning("智能初始布局无法恢复指北针原目标绑定，已回退整理前布局。");
                return CloneSlots(ctx.original);
            }
            if (ctx.whitePaperTargets.Count > 0 && plugin.WhitePaperComboBonus.Value > 0f)
            {
                EvaluateLayout(ctx, smart);
                var whitePaperRng = new System.Random(ctx.storage * 397 ^ ctx.items.Count * 7919);
                int attempts = Math.Max(4, ctx.whitePapers.Count * 4);
                for (int i = 0; i < attempts; i++)
                {
                    if (TryWhitePaperMove(ctx, smart, whitePaperRng))
                    {
                        RestoreCompassBindings(ctx, smart);
                        EvaluateLayout(ctx, smart);
                    }
                }
            }
            return smart;
        }

        /// <summary>为原先未配对的指北针找“上方是伤害类/指北针”的空格；上方伤害藏品优先级越高越优先。</summary>
        private int FindCompassTargetCell(SearchContext ctx, Slot[] result, bool[] occupied)
        {
            int best = -1;
            float bestScore = float.MinValue;
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                if (occupied[cell])
                {
                    continue;
                }
                int x = cell % ctx.width;
                int y = cell / ctx.width;
                int ax = x;
                int ay = y - 1;
                if (ay < 0)
                {
                    continue;
                }
                int idx = ay * ctx.width + ax;
                Slot above = null;
                if (idx >= 0 && idx < ctx.storage)
                {
                    above = result[idx];
                }
                if (above != null && above.hasItem && above.charm != null)
                {
                    bool valid = ctx.itemByInstance.TryGetValue(above.instanceID, out ItemInfo pairedAboveInfo) &&
                                 pairedAboveInfo != null && (pairedAboveInfo.isCompass || pairedAboveInfo.isAttackable);
                    if (valid)
                    {
                        // 按上方伤害藏品的优先级加权：高优先级伤害神器优先配对
                        float w = 1f;
                        if (ctx.itemByInstance.TryGetValue(above.instanceID, out ItemInfo aboveInfo) && aboveInfo != null)
                        {
                            w = (float)PriorityWeight(aboveInfo.priority);
                        }
                        float sc = ctx.cellLevel[cell] * 100f * w - (ctx.disabled[cell] ? 500f : 0f);
                        if (sc > bestScore)
                        {
                            bestScore = sc;
                            best = cell;
                        }
                    }
                }
            }
            return best;
        }

        private static int SteleImportance(StoneTablet tablet)
        {
            if (tablet == null)
            {
                return 0;
            }
            try
            {
                string q = tablet.GetQuery(tablet.instanceID);
                if (string.IsNullOrEmpty(q))
                {
                    return 0;
                }
                int negatives = 0;
                foreach (string line in q.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = line.Split(' ');
                    if (parts.Length >= 2 && int.TryParse(parts[parts.Length - 1], out int v) && v < 0)
                    {
                        negatives++;
                    }
                }
                return negatives;
            }
            catch
            {
                return 0;
            }
        }

        private static float EvaluateStelePattern(
            SearchContext ctx,
            StelePattern pattern,
            Slot[] result,
            bool[] occupied,
            HashSet<int> steleEffectCells = null)
        {
            if (!ConditionsOk(pattern, SlotsFromArray(result, ctx.storage)))
            {
                return -1000f;
            }

            float score = 0f;
            if (steleEffectCells != null && pattern.cell >= 0 && steleEffectCells.Contains(pattern.cell))
            {
                score -= 100f;
            }
            foreach (EffectEntry e in pattern.effects)
            {
                bool covered = e.cell >= 0 && e.cell < ctx.storage && (occupied[e.cell] || result[e.cell] != null);
                switch (e.kind)
                {
                    case 0:
                        score += e.value * 10f;
                        if (e.value < 0 && !covered)
                        {
                            score -= 160f;
                        }
                        else if (e.value > 0 && covered)
                        {
                            score -= 60f;
                        }
                        break;
                    case 2:
                        score += 25f;
                        break;
                    case 1:
                        score += 1f;
                        break;
                }
            }
            return score;
        }

        /// <summary>为护符挑选最佳格子：有满足条件(或豁免)的格子时只在这些格里选等级最高的；否则退而求其次。</summary>
        private static int FindBestCharmCell(SearchContext ctx, ItemInfo charm, Slot[] result, bool[] occupied,
            List<Slot> slotsNow, HashSet<int> harmonyNeighbors = null, int dedicationRow = -1,
            HashSet<int> hourglassRightCells = null, HashSet<int> rayShardLeftCells = null)
        {
            // 分档候选：自然满足位置格 / 豁免格(IgnoreCriteria) / 任意格
            int bestNatural = -1;
            float bestNaturalScore = float.MinValue;
            int bestIgnore = -1;
            float bestIgnoreScore = float.MinValue;
            int bestAny = -1;
            float bestAnyScore = float.MinValue;

            bool restricted = KindPriority(charm.kind) >= 2;

            // 永恒蚀：冰霜武具>太阳剑 → 只允许右三列；少于 → 只允许左三列（区域内无空位时放宽兜底）
            bool eclipseRestricted = charm.isEternalEclipse && ctx.frostCount != ctx.flameSwordCount;
            bool eclipseRight = ctx.frostCount > ctx.flameSwordCount;
            // 对立之秤：冰川>余烬 → 最右列；少于 → 最左列；相等 → 最左或最右列（边缘无空位时放宽兜底）
            bool scaleRestricted = charm.isOpposingScale;
            bool scaleRight = ctx.glacierCount > ctx.emberCount;
            bool scaleLeft = ctx.glacierCount < ctx.emberCount;

            for (int cell = 0; cell < ctx.storage; cell++)
            {
                if (occupied[cell])
                {
                    continue;
                }
                int x = cell % ctx.width;
                int y = cell / ctx.width;
                if (!IsAllowedLockedRow(charm, y))
                {
                    continue;
                }
                if (charm.isHourglass && x == ctx.width - 1)
                {
                    continue; // 沙漏：最右列右边没有格子，永远配不上魔法书
                }
                if (charm.isRayShard && x == 0)
                {
                    continue; // 雷伊星碎片：最左列左边没有格子，永远配不上魔法书
                }
                if (eclipseRestricted)
                {
                    if (eclipseRight && x < ctx.width / 2)
                    {
                        continue; // 冰霜多：只能在右三列
                    }
                    if (!eclipseRight && x >= ctx.width / 2)
                    {
                        continue; // 太阳剑多：只能在左三列
                    }
                }
                if (scaleRestricted)
                {
                    bool atEdge = x == 0 || x == ctx.width - 1;
                    if (scaleRight && !(x == ctx.width - 1))
                    {
                        continue; // 冰川多：只能最右列
                    }
                    if (scaleLeft && !(x == 0))
                    {
                        continue; // 余烬多：只能最左列
                    }
                    if (!scaleRight && !scaleLeft && !atEdge)
                    {
                        continue; // 相等：只能最左/最右列，禁止中间
                    }
                }
                bool isIgnore = ctx.ignore[cell];
                bool natural = IsSatisfyingCell(charm.kind, x, y, cell, ctx.storage, ctx.width);
                int level = ctx.cellLevel[cell];
                // 和谐之晶邻域：高等级护符优先聚到它周围8格
                float sc = level * 100f * ctx.mysticFactor[cell] - (ctx.disabled[cell] ? 500f : 0f);
                if (ctx.hasBelt && y == 0)
                {
                    sc += 6000f; // 腰带在场：神器优先塞满第一行
                }
                if (harmonyNeighbors != null && harmonyNeighbors.Contains(cell))
                {
                    sc += 8000f;
                }
                if (dedicationRow >= 0 && y == dedicationRow)
                {
                    sc += 6000f; // 奉献徽章同行：同伴优先同横排
                }
                if (charm.isMagicBook && hourglassRightCells != null && hourglassRightCells.Contains(cell))
                {
                    sc += 8000f + charm.magicCd * 4000f; // 沙漏右边格：魔法书优先，CD 越长越优先
                }
                if (charm.isMagicBook && rayShardLeftCells != null && rayShardLeftCells.Contains(cell))
                {
                    sc += 8000f + charm.magicMpCost * 4000f; // 雷伊星碎片左侧格：魔法书优先，耗蓝越高越优先
                }
                if (ctx.mysticFactor[cell] > 1)
                {
                    sc += 20000f;
                }
                if (restricted)
                {
                    if (charm.preferIgnoreCells)
                    {
                        // 冰锁类：豁免格优先（+5000，可解除限制上高等级格）
                        if (isIgnore)
                        {
                            sc += 5000f;
                        }
                        else if (natural)
                        {
                            sc += 500f; // 自然限制位置作为次选
                        }
                    }
                    else if (natural && !isIgnore)
                    {
                        // 普通受限物品：自然满足位置优先（小偏好），豁免格作为后备
                        sc += 500f;
                    }
                }

                if (sc > bestAnyScore)
                {
                    bestAnyScore = sc;
                    bestAny = cell;
                }
                if (natural && !isIgnore && sc > bestNaturalScore)
                {
                    bestNaturalScore = sc;
                    bestNatural = cell;
                }
                if (isIgnore && sc > bestIgnoreScore)
                {
                    bestIgnoreScore = sc;
                    bestIgnore = cell;
                }
            }

            if (restricted)
            {
                if (charm.preferIgnoreCells)
                {
                    // 冰锁类：豁免格 > 自然位置 > 任意
                    if (bestIgnore >= 0) return bestIgnore;
                    if (bestNatural >= 0) return bestNatural;
                }
                else
                {
                    // 普通受限：自然位置 > 豁免格 > 任意
                    if (bestNatural >= 0) return bestNatural;
                    if (bestIgnore >= 0) return bestIgnore;
                }
            }
            // 永恒蚀/对立之秤：规定位置无任何空位时放宽（按等级分选全背包最优），避免摆不下
            if ((eclipseRestricted || scaleRestricted) && bestAny < 0)
            {
                for (int cell = 0; cell < ctx.storage; cell++)
                {
                    if (occupied[cell])
                    {
                        continue;
                    }
                    float sc = ctx.cellLevel[cell] * 100f * ctx.mysticFactor[cell] - (ctx.disabled[cell] ? 500f : 0f);
                    if (sc > bestAnyScore)
                    {
                        bestAnyScore = sc;
                        bestAny = cell;
                    }
                }
            }
            return bestAny;
        }

        private static int FindWorstCell(SearchContext ctx, Slot[] result, bool[] occupied)
        {
            int worst = -1;
            int worstLevel = int.MaxValue;
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                if (occupied[cell])
                {
                    continue;
                }
                int level = ctx.cellLevel[cell];
                if (level < worstLevel)
                {
                    worstLevel = level;
                    worst = cell;
                }
            }
            return worst;
        }

        private static int FirstFree(bool[] occupied)
        {
            for (int i = 0; i < occupied.Length; i++)
            {
                if (!occupied[i])
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>找到第一个满足物品行约束的空闲格。</summary>
        private static int FirstFreeForLockedItem(bool[] occupied, ItemInfo item, SearchContext ctx)
        {
            for (int i = 0; i < ctx.storage; i++)
            {
                if (!occupied[i] && IsAllowedLockedRow(item, i / ctx.width))
                {
                    return i;
                }
            }
            return -1;
        }

        private static List<Slot> SlotsFromArray(Slot[] result, int storage)
        {
            var list = new List<Slot>(storage);
            for (int i = 0; i < storage; i++)
            {
                list.Add(result[i] != null ? result[i] : Slot.Empty());
            }
            return list;
        }

        private static void FillItemPositionMap(List<Slot> slots, Dictionary<int, int> positions)
        {
            positions.Clear();
            if (slots == null)
            {
                return;
            }
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                if (slot != null && slot.hasItem)
                {
                    positions[slot.instanceID] = i;
                }
            }
        }

        private static bool CanPlaceCompassChain(SearchContext ctx, CompassChain chain, int rootCell,
            HashSet<int> reserved)
        {
            if (rootCell < 0 || rootCell >= ctx.storage)
            {
                return false;
            }

            int x = rootCell % ctx.width;
            for (int i = 0; i < chain.instanceIDs.Count; i++)
            {
                int cell = rootCell + i * ctx.width;
                if (cell < 0 || cell >= ctx.storage || cell % ctx.width != x ||
                    (reserved != null && reserved.Contains(cell)))
                {
                    return false;
                }

                // 与已有的行锁定规则兼容：若链中物品被配置为锁行，整条链只能选满足该行的位置。
                if (ctx.itemByInstance.TryGetValue(chain.instanceIDs[i], out ItemInfo info) &&
                    info != null && !IsAllowedLockedRow(info, cell / ctx.width))
                {
                    return false;
                }
            }
            return true;
        }

        private static void SetCompassChainReservation(SearchContext ctx, CompassChain chain, int rootCell,
            HashSet<int> reserved, bool value)
        {
            for (int i = 0; i < chain.instanceIDs.Count; i++)
            {
                int cell = rootCell + i * ctx.width;
                if (value)
                {
                    reserved.Add(cell);
                }
                else
                {
                    reserved.Remove(cell);
                }
            }
        }

        private static int FindClosestCompassChainRoot(SearchContext ctx, CompassChain chain,
            int currentRoot, int fallbackRoot, HashSet<int> reserved)
        {
            int reference = currentRoot >= 0 ? currentRoot : fallbackRoot;
            int referenceX = reference % ctx.width;
            int referenceY = reference / ctx.width;
            int best = -1;
            int bestDistance = int.MaxValue;
            int bestFallbackPenalty = int.MaxValue;

            for (int cell = 0; cell < ctx.storage; cell++)
            {
                if (!CanPlaceCompassChain(ctx, chain, cell, reserved))
                {
                    continue;
                }

                int distance = Math.Abs(cell % ctx.width - referenceX) +
                               Math.Abs(cell / ctx.width - referenceY);
                int fallbackPenalty = cell == fallbackRoot ? 0 : 1;
                if (distance < bestDistance ||
                    (distance == bestDistance && fallbackPenalty < bestFallbackPenalty) ||
                    (distance == bestDistance && fallbackPenalty == bestFallbackPenalty && cell < best))
                {
                    best = cell;
                    bestDistance = distance;
                    bestFallbackPenalty = fallbackPenalty;
                }
            }
            return best;
        }

        private static void SwapSlotsAndTrack(List<Slot> slots, Dictionary<int, int> positions, int a, int b)
        {
            if (a == b)
            {
                return;
            }
            SwapSlots(slots, a, b);
            if (slots[a] != null && slots[a].hasItem)
            {
                positions[slots[a].instanceID] = a;
            }
            if (slots[b] != null && slots[b].hasItem)
            {
                positions[slots[b].instanceID] = b;
            }
        }

        /// <summary>
        /// 把整理前已配对的指北针恢复到原目标正下方。以链首目标的候选位置为锚点，后续指北针
        /// 随它一起移动；若链首落到背包底部等无空间位置，则选择最近的可容纳竖链位置。
        /// </summary>
        private static bool RestoreCompassBindings(SearchContext ctx, List<Slot> slots)
        {
            if (ctx.compassChains.Count == 0)
            {
                return true;
            }
            if (CompassBindingsSatisfied(ctx, slots))
            {
                return true;
            }

            var positions = ctx.compassPositionScratch;
            FillItemPositionMap(slots, positions);
            var reserved = ctx.compassReservedScratch;
            reserved.Clear();
            int[] assignedRoots = ctx.compassRootScratch;

            // 先以整理前位置建立一组必定互不重叠的保底分配。
            for (int i = 0; i < ctx.compassChains.Count; i++)
            {
                CompassChain chain = ctx.compassChains[i];
                if (!CanPlaceCompassChain(ctx, chain, chain.originalRootCell, reserved))
                {
                    return false;
                }
                assignedRoots[i] = chain.originalRootCell;
                SetCompassChainReservation(ctx, chain, assignedRoots[i], reserved, true);
            }

            // 尽量采用链首在当前候选布局中的新位置，使“目标移动、指北针跟随”真正参与优化。
            for (int i = 0; i < ctx.compassChains.Count; i++)
            {
                CompassChain chain = ctx.compassChains[i];
                SetCompassChainReservation(ctx, chain, assignedRoots[i], reserved, false);

                int currentRoot = positions.TryGetValue(chain.instanceIDs[0], out int rootCell)
                    ? rootCell
                    : chain.originalRootCell;
                int chosen = FindClosestCompassChainRoot(ctx, chain, currentRoot,
                    chain.originalRootCell, reserved);
                if (chosen < 0)
                {
                    return false;
                }
                assignedRoots[i] = chosen;
                SetCompassChainReservation(ctx, chain, chosen, reserved, true);
            }

            // 通过交换完成排列，物品集合不增不减；位置表随每次交换更新。
            for (int i = 0; i < ctx.compassChains.Count; i++)
            {
                CompassChain chain = ctx.compassChains[i];
                for (int k = 0; k < chain.instanceIDs.Count; k++)
                {
                    int instanceID = chain.instanceIDs[k];
                    if (!positions.TryGetValue(instanceID, out int from))
                    {
                        return false;
                    }
                    int target = assignedRoots[i] + k * ctx.width;
                    SwapSlotsAndTrack(slots, positions, from, target);
                }
            }

            return CompassBindingsSatisfied(ctx, slots);
        }

        /// <summary>防御：校验 CaptureState 快照与背包当前状态一致（物品实例集合相同），
        /// 防止背包初始化未完成时按不完整快照清空重写导致物品丢失。
        /// 注意：只统计"正常背包格"（y 在高度内且索引在 storage 内），排除药水带(y=100)等特殊位置。</summary>
        private static bool VerifyInventorySnapshot(GridInventory inv, List<Slot> captured)
        {
            try
            {
                int storage = inv.CurrentInventoryStorage;
                int height = inv.GetHeight(storage);

                var fresh = new HashSet<int>();
                foreach (var kv in inv.inventoryMatrix)
                {
                    if (kv.Value == null)
                    {
                        continue;
                    }
                    if (kv.Key.y < 0 || kv.Key.y >= height)
                    {
                        continue; // 药水带(y=100)等特殊位置，不参与背包整理
                    }
                    int idx = inv.PosToIdx(kv.Key);
                    if (idx < 0 || idx >= storage)
                    {
                        continue;
                    }
                    fresh.Add(kv.Value.InstanceID);
                }

                var capturedIds = new HashSet<int>();
                foreach (Slot s in captured)
                {
                    if (s != null && s.hasItem)
                    {
                        capturedIds.Add(s.instanceID);
                    }
                }

                if (fresh.Count != capturedIds.Count)
                {
                    return false;
                }
                foreach (int id in fresh)
                {
                    if (!capturedIds.Contains(id))
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- 入口

        public void Sort()
        {
            if (busy)
            {
                Plugin.Log.LogWarning("整理正在进行中，请稍候再试。");
                return;
            }

            if (!NetworkClient.active)
            {
                Notify("未进入游戏会话，无法整理背包。");
                return;
            }

            var localPlayer = NetworkClient.localPlayer;
            if (localPlayer == null)
            {
                Notify("未找到本地玩家，无法整理背包。");
                return;
            }

            var avatar = localPlayer.GetComponent<PlayerAvatar>();
            if (avatar == null)
            {
                Notify("未找到玩家角色，无法整理背包。");
                return;
            }

            var inv = avatar.Inventory;
            if (inv == null)
            {
                Notify("背包不可用，无法整理。");
                return;
            }

            // 防御：会话刚建立时背包初始化未完成，此刻整理可能按不完整快照清空重写导致物品丢失。
            // 记录会话稳定时刻：本地玩家出现后经过稳定延迟才允许整理。
            if (sessionStartTime < 0)
            {
                sessionStartTime = Time.unscaledTime;
            }
            if (Time.unscaledTime - sessionStartTime < plugin.SessionStableDelay.Value)
            {
                Notify($"背包初始化中，请稍候 {Mathf.CeilToInt(plugin.SessionStableDelay.Value)} 秒后再整理。");
                return;
            }

            if (inv.CurrentInventoryStorage <= 1 || inv.charms.Count == 0)
            {
                Notify("背包为空或没有护符，无需整理。");
                return;
            }

            busy = true;
            bool backgroundStarted = false;
            try
            {
                if (plugin.SelfTest.Value && NetworkServer.active)
                {
                    SortSelfTest(inv);
                    return;
                }

                switch (plugin.Mode.Value)
                {
                    case SortMode.Vanilla:
                        SortVanilla(inv);
                        break;

                    case SortMode.Enhanced:
                        // 主机与联机客户端都在后台做纯数据搜索，再由主线程分帧执行游戏操作。
                        backgroundStarted = StartSortEnhanced(inv);
                        break;

                    default:
                        Plugin.Log.LogWarning($"未知整理模式 {plugin.Mode.Value}，使用内置整理。");
                        SortVanilla(inv);
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"整理背包异常: {ex}");
                Notify("整理背包失败，详见日志。");
            }
            finally
            {
                if (!backgroundStarted)
                {
                    busy = false;
                }
            }
        }

        private static int CompareManualPriority(ItemInfo a, ItemInfo b)
        {
            int ar = a != null ? a.manualPriorityRank : 0;
            int br = b != null ? b.manualPriorityRank : 0;
            if (ar == br)
            {
                return 0;
            }
            if (ar == 0)
            {
                return 1;
            }
            if (br == 0)
            {
                return -1;
            }
            return ar.CompareTo(br);
        }

        /// <summary>由 Plugin.Update 在 Unity 主线程轮询后台搜索完成状态。</summary>
        public void Poll()
        {
            if (pendingEnhanced == null)
            {
                return;
            }

            PendingEnhancedSort state = pendingEnhanced;
            try
            {
                if (!NetworkClient.active || state.inv == null)
                {
                    CancelEnhancedSort(state, "游戏会话已结束，本次整理已取消。", false);
                    return;
                }

                if (pendingSearch != null)
                {
                    if (!pendingSearch.IsCompleted)
                    {
                        return;
                    }

                    Task<SearchOutcome> completed = pendingSearch;
                    pendingSearch = null;
                    BeginApplyEnhanced(state, completed.GetAwaiter().GetResult());
                    return;
                }

                if (state.applying)
                {
                    AdvanceApplyEnhanced(state);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"后台整理搜索异常: {ex}");
                CancelEnhancedSort(state, "整理背包失败，详见日志。", true);
            }
        }

        // ---------------------------------------------------------------- Vanilla

        private void SortVanilla(GridInventory inv)
        {
            float before = SafeScore(inv);
            bool knownResult;

            if (NetworkServer.active)
            {
                knownResult = inv.AutoArrangeInventoryForBestCharmLevels(
                    plugin.VanillaIterations.Value, plugin.AllowTabletRotation.Value);
            }
            else
            {
                inv.RequestAutoArrangeInventoryForBestCharmLevels(
                    plugin.VanillaIterations.Value, plugin.AllowTabletRotation.Value);
                knownResult = true;
            }

            if (!knownResult)
            {
                Notify("背包无需整理（无护符或已是最优）。");
                return;
            }

            float after = SafeScore(inv);
            if (float.IsNaN(before) || float.IsNaN(after))
            {
                Notify("整理完毕");
            }
            else
            {
                Plugin.Log.LogInfo($"内置整理完成：加成评分 {before:F0} -> {after:F0}");
                Notify("整理完毕");
            }
        }

        // ---------------------------------------------------------------- Enhanced

        /// <summary>离线计算最优布局（智能初始 + 多轮独立多起点退火，取全局最优），不修改游戏状态。</summary>
        private List<Slot> ComputeBestLayout(List<Slot> original, SearchContext ctx,
            out double beforeScore, out double bestScore)
        {
            beforeScore = EvaluateLayout(ctx, original);

            // 智能初始布局
            List<Slot> start;
            if (plugin.EnableSmartStart.Value)
            {
                start = BuildSmartStart(ctx);
                double smartScore = EvaluateLayout(ctx, start);
                Plugin.Log.LogInfo($"智能初始布局：评分 {beforeScore:F0} -> {smartScore:F0}");
            }
            else
            {
                start = original;
            }

            // 多轮独立搜索（不同随机种子），取全局最优——等效于自动重复按 F8 多次
            int rounds = Math.Max(1, plugin.SearchRounds.Value);
            long deadlineTicks = CreateSearchDeadline(plugin.SearchTimeBudgetMs.Value);
            List<Slot> bestLayout = original;
            double globalBest = beforeScore;
            for (int round = 0; round < rounds; round++)
            {
                if (SearchDeadlineReached(deadlineTicks))
                {
                    ctx.searchBudgetReached = true;
                    break;
                }
                var rng = new System.Random(Environment.TickCount + round * 7919);
                var result = AnnealMultiStart(ctx, start, original, rng,
                    plugin.EnhancedIterations.Value, plugin.EnhancedRestarts.Value,
                    plugin.EnhancedTemperature.Value, deadlineTicks);
                if (result.Score > globalBest)
                {
                    globalBest = result.Score;
                    bestLayout = result.Best;
                }
            }

            if (ctx.searchBudgetReached || SearchDeadlineReached(deadlineTicks))
            {
                ctx.searchBudgetReached = true;
                Plugin.Log.LogInfo($"搜索达到 {plugin.SearchTimeBudgetMs.Value}ms 时间预算，已保留当前最优布局。");
            }

            if (!CompassBindingsSatisfied(ctx, bestLayout))
            {
                Plugin.Log.LogWarning("搜索结果破坏了整理前的指北针目标绑定，已回退整理前布局。");
                bestLayout = CloneSlots(original);
                globalBest = beforeScore;
            }
            bestScore = globalBest;

            return globalBest >= beforeScore - 0.5 ? bestLayout : original;
        }

        private bool StartSortEnhanced(GridInventory inv)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            List<Slot> original = CaptureState(inv);
            if (!VerifyInventorySnapshot(inv, original))
            {
                Notify("背包状态未就绪，本次未整理（请稍后再试）。");
                return false;
            }
            SearchContext ctx = BuildContext(inv, original);
            if (plugin.VerboseDiagnostics.Value)
            {
                LogItemIdentification(ctx);
            }

            float beforeGame = SafeScore(inv);
            pendingEnhanced = new PendingEnhancedSort
            {
                inv = inv,
                original = original,
                ctx = ctx,
                beforeGameScore = beforeGame,
                stopwatch = sw,
                swapsPerFrame = Math.Max(1, plugin.ApplySwapsPerFrame.Value),
                rotationClicksPerFrame = Math.Max(1, plugin.ApplyRotationClicksPerFrame.Value),
                frameBudgetMs = Math.Max(0.25, plugin.ApplyFrameBudgetMs.Value),
                acknowledgementTimeoutMs = Math.Max(250, plugin.ApplyAckTimeoutMs.Value)
            };
            pendingSearch = Task.Run(() =>
            {
                List<Slot> layout = ComputeBestLayout(original, ctx,
                    out double beforeScore, out double bestScore);
                return new SearchOutcome
                {
                    layout = layout,
                    beforeScore = beforeScore,
                    bestScore = bestScore
                };
            });
            // 按下 F8 后立即给出简短反馈；完成时再统一显示原版风格的“整理完毕”。
            // 评分、回滚等内部诊断信息仍只写入日志。
            Notify("整理中…");
            return true;
        }

        private void BeginApplyEnhanced(PendingEnhancedSort state, SearchOutcome outcome)
        {
            if (state == null || outcome == null || state.inv == null)
            {
                throw new InvalidOperationException("后台整理状态丢失");
            }

            GridInventory inv = state.inv;
            List<Slot> original = state.original;

            // 搜索期间游戏继续运行；背包只要发生过位置、旋转或物品集合变化，就丢弃旧结果。
            List<Slot> currentLayout = CaptureState(inv);
            if (!VerifyInventorySnapshot(inv, original) ||
                !LayoutsEquivalent(currentLayout, original))
            {
                CancelEnhancedSort(state,
                    $"后台搜索完成（{state.stopwatch.ElapsedMilliseconds}ms），但背包期间已变化，结果已丢弃。",
                    false);
                return;
            }

            state.outcome = outcome;
            state.target = CloneSlots(outcome.layout);
            BeginMovePlan(state, original, state.target, false);
        }

        private void BeginMovePlan(PendingEnhancedSort state, List<Slot> current,
            List<Slot> target, bool rollingBack)
        {
            state.swaps.Clear();
            state.rotations.Clear();
            BuildClientOps(current, target, state.swaps, state.rotations);
            state.expected = CloneSlots(current);
            state.target = CloneSlots(target);
            state.swapIndex = 0;
            state.rotationIndex = 0;
            state.rotationRemaining = 0;
            state.rollingBack = rollingBack;
            state.applying = true;
            state.awaitingObservedState = false;
            state.acknowledgement.Reset();

            if (state.swaps.Count == 0 && state.rotations.Count == 0)
            {
                CompleteApplyEnhanced(state);
            }
        }

        private void AdvanceApplyEnhanced(PendingEnhancedSort state)
        {
            if (state.awaitingObservedState)
            {
                List<Slot> observed = CaptureState(state.inv);
                if (!LayoutsEquivalent(observed, state.expected))
                {
                    if (!state.acknowledgement.IsRunning)
                    {
                        state.acknowledgement.Restart();
                    }
                    if (state.acknowledgement.ElapsedMilliseconds < state.acknowledgementTimeoutMs)
                    {
                        return;
                    }

                    string phase = state.rollingBack ? "回滚" : "应用";
                    CancelEnhancedSort(state,
                        $"整理{phase}等待服务器确认超时，已停止继续操作。当前背包未被强制改写。", true);
                    return;
                }

                state.awaitingObservedState = false;
                state.acknowledgement.Reset();
            }

            var frame = System.Diagnostics.Stopwatch.StartNew();
            int swapsThisFrame = 0;
            int rotationsThisFrame = 0;
            bool didWork = false;

            while (state.swapIndex < state.swaps.Count &&
                   swapsThisFrame < state.swapsPerFrame &&
                   (swapsThisFrame == 0 || frame.Elapsed.TotalMilliseconds < state.frameBudgetMs))
            {
                var op = state.swaps[state.swapIndex++];
                ItemPosition a = state.inv.IdxToPos(op.a);
                ItemPosition b = state.inv.IdxToPos(op.b);
                state.inv.Swap(a.x, a.y, b.x, b.y);
                SwapSlots(state.expected, op.a, op.b);
                swapsThisFrame++;
                didWork = true;
            }

            // 所有交换完成后才开始旋转。rotationRemaining 跨帧保留，避免漏掉一次点击。
            while (state.swapIndex >= state.swaps.Count &&
                   state.rotationIndex < state.rotations.Count &&
                   rotationsThisFrame < state.rotationClicksPerFrame &&
                   ((swapsThisFrame == 0 && rotationsThisFrame == 0) ||
                    frame.Elapsed.TotalMilliseconds < state.frameBudgetMs))
            {
                var op = state.rotations[state.rotationIndex];
                if (state.rotationRemaining <= 0)
                {
                    state.rotationRemaining = op.count;
                }

                ItemPosition p = state.inv.IdxToPos(op.pos);
                state.inv.DoClickAction(p);
                Slot slot = state.expected[op.pos];
                slot.rotation = (slot.rotation + 1) % 4;
                state.expected[op.pos] = slot;
                state.rotationRemaining--;
                rotationsThisFrame++;
                didWork = true;

                if (state.rotationRemaining == 0)
                {
                    state.rotationIndex++;
                }
            }

            if (didWork)
            {
                state.awaitingObservedState = true;
                return;
            }

            if (state.swapIndex >= state.swaps.Count &&
                state.rotationIndex >= state.rotations.Count &&
                state.rotationRemaining == 0)
            {
                CompleteApplyEnhanced(state);
            }
        }

        private void CompleteApplyEnhanced(PendingEnhancedSort state)
        {
            List<Slot> actual = CaptureState(state.inv);
            if (!LayoutsEquivalent(actual, state.target))
            {
                if (!state.rollingBack && VerifyInventorySnapshot(state.inv, state.original))
                {
                    Plugin.Log.LogWarning("应用后的物品/旋转布局与搜索目标不一致，正在分帧恢复整理前布局。");
                    BeginMovePlan(state, actual, state.original, true);
                }
                else
                {
                    Plugin.Log.LogError("整理回滚后布局仍与整理前快照不一致，请保留日志并检查背包。");
                    CancelEnhancedSort(state, "整理未能安全完成，请检查背包。", true);
                }
                return;
            }

            float finalGameScore = SafeScore(state.inv);

            double finalOffline = EvaluateLayout(state.ctx, actual);
            if (plugin.VerboseDiagnostics.Value)
            {
                LogLayoutGrid(state.ctx, actual, state.rollingBack ? "回滚" : "整理");
                LogLayoutAnalysis(state.ctx, actual, state.rollingBack ? "回滚" : "整理");
            }

            state.stopwatch.Stop();
            Plugin.Log.LogInfo(
                $"增强整理完成（后台+分帧总耗时 {state.stopwatch.ElapsedMilliseconds}ms）：" +
                $"离线评分 {state.outcome.beforeScore:F0} -> {finalOffline:F0}（搜索最优 {state.outcome.bestScore:F0}）；" +
                $"游戏评分 {state.beforeGameScore:F0} -> {finalGameScore:F0}；" +
                $"搜索 {state.ctx.annealEvaluations} 候选/启动 {state.ctx.annealStartsCompleted}/{state.ctx.annealStarts}" +
                $"；{(state.rollingBack ? "已安全回滚" : "落地校验一致")}；布局 {state.ctx.items.Count} 件");
            state.applying = false;
            pendingEnhanced = null;
            pendingSearch = null;
            busy = false;
            Notify("整理完毕");
        }

        private void CancelEnhancedSort(PendingEnhancedSort state, string reason, bool notifyFailure)
        {
            if (state != null && state.stopwatch != null && state.stopwatch.IsRunning)
            {
                state.stopwatch.Stop();
            }
            Plugin.Log.LogWarning(reason);
            pendingSearch = null;
            pendingEnhanced = null;
            busy = false;
            Notify(notifyFailure ? reason : "整理期间背包发生变化，本次结果已取消");
        }

        /// <summary>执行交换/旋转操作序列（主机与联机客户端通用）。</summary>
        private static void ApplyMoves(GridInventory inv, List<(int a, int b)> swaps, List<(int pos, int count)> rots)
        {
            foreach (var (a, b) in swaps)
            {
                ItemPosition pa = inv.IdxToPos(a);
                ItemPosition pb = inv.IdxToPos(b);
                inv.Swap(pa.x, pa.y, pb.x, pb.y);
            }
            foreach (var (pos, count) in rots)
            {
                ItemPosition p = inv.IdxToPos(pos);
                for (int r = 0; r < count; r++)
                {
                    inv.DoClickAction(p);
                }
            }
        }

        /// <summary>用交换/旋转"挪移"把当前布局调整为目标布局（不清空背包、不改写字典）。</summary>
        private static void ApplyByMoves(GridInventory inv, List<Slot> original, List<Slot> finalLayout)
        {
            var swaps = new List<(int a, int b)>();
            var rots = new List<(int pos, int count)>();
            BuildClientOps(original, finalLayout, swaps, rots);
            ApplyMoves(inv, swaps, rots);
        }

        /// <summary>把"当前布局→目标布局"转换为 交换(位置) + 旋转(位置×次数) 操作序列（逻辑推演，含位置与旋转追踪）。</summary>
        private static void BuildClientOps(List<Slot> current, List<Slot> target,
            List<(int, int)> swaps, List<(int, int)> rots)
        {
            int n = Math.Min(current.Count, target.Count);
            var logical = CloneSlots(current);
            var posOf = new Dictionary<int, int>();
            for (int i = 0; i < logical.Count; i++)
            {
                if (logical[i].hasItem)
                {
                    posOf[logical[i].instanceID] = i;
                }
            }

            // 交换：逐位置归位（物品集相同；目标空位时把多余物品换到空位）
            for (int i = 0; i < n; i++)
            {
                bool curHas = logical[i].hasItem;
                bool tgtHas = target[i].hasItem;
                int curId = curHas ? logical[i].instanceID : -1;
                int tgtId = tgtHas ? target[i].instanceID : -1;
                if (curHas == tgtHas && curId == tgtId)
                {
                    continue;
                }

                if (tgtHas)
                {
                    // 位置 i 需要放置目标物品（在 j）
                    if (posOf.TryGetValue(tgtId, out int j) && j != i)
                    {
                        swaps.Add((i, j));
                        SwapLogical(logical, posOf, i, j);
                    }
                }
                else
                {
                    // 目标为空：把 i 上物品换到某个空位
                    int empty = -1;
                    for (int k = 0; k < logical.Count; k++)
                    {
                        if (k != i && !logical[k].hasItem)
                        {
                            empty = k;
                            break;
                        }
                    }
                    if (empty >= 0)
                    {
                        swaps.Add((i, empty));
                        SwapLogical(logical, posOf, i, empty);
                    }
                }
            }

            // 旋转：交换完成后，目标位置上的石板若旋转不一致则记录（在最终位置执行点击旋转）
            for (int i = 0; i < n; i++)
            {
                Slot t = target[i];
                Slot l = logical[i];
                if (t.hasItem && t.tablet != null && l.hasItem && l.tablet != null &&
                    t.instanceID == l.instanceID && t.rotation != l.rotation)
                {
                    int count = (t.rotation - l.rotation + 4) % 4;
                    if (count > 0)
                    {
                        rots.Add((i, count));
                    }
                }
            }
        }

        private static void SwapLogical(List<Slot> logical, Dictionary<int, int> posOf, int a, int b)
        {
            Slot tmp = logical[a];
            logical[a] = logical[b];
            logical[b] = tmp;
            if (logical[a].hasItem)
            {
                posOf[logical[a].instanceID] = a;
            }
            if (logical[b].hasItem)
            {
                posOf[logical[b].instanceID] = b;
            }
        }

        private struct AnnealResult
        {
            public List<Slot> Best;
            public double Score;
        }

        /// <summary>全离线模拟退火：交换 / 移动 / 旋转 / 定向移动（条件、行星、罗盘、负担）。</summary>
        private AnnealResult Anneal(SearchContext ctx, List<Slot> start, System.Random rng,
            int iterations, int restarts, float temp0, long deadlineTicks = 0)
        {
            ctx.annealStarts++;
            var best = CloneSlots(start);
            if (!RestoreCompassBindings(ctx, best))
            {
                best = CloneSlots(ctx.original);
            }
            double bestScore = EvaluateLayout(ctx, best);

            var candidate = CloneSlots(best);
            var mutated = CloneSlots(best);
            double candidateScore = bestScore;

            for (int r = 0; r < restarts; r++)
            {
                for (int i = 0; i < iterations; i++)
                {
                    if ((i & 31) == 0 && SearchDeadlineReached(deadlineTicks))
                    {
                        ctx.searchBudgetReached = true;
                        return new AnnealResult { Best = best, Score = bestScore };
                    }

                    CopySlots(candidate, mutated);
                    Mutate(ctx, mutated, rng);
                    if (!RestoreCompassBindings(ctx, mutated))
                    {
                        continue;
                    }

                    double s = EvaluateLayout(ctx, mutated);
                    ctx.annealEvaluations++;

                    if (s > bestScore)
                    {
                        CopySlots(mutated, best);
                        bestScore = s;
                    }

                    double t = Math.Max(1f, temp0 * (1f - (double)i / Math.Max(1, iterations)));
                    bool accept = s >= candidateScore ||
                                  rng.NextDouble() < Math.Exp((s - candidateScore) / t);
                    if (accept)
                    {
                        List<Slot> oldCandidate = candidate;
                        candidate = mutated;
                        mutated = oldCandidate;
                        candidateScore = s;
                    }
                }

                candidateScore = EvaluateLayout(ctx, best);
                CopySlots(best, candidate);
            }

            ctx.annealStartsCompleted++;
            return new AnnealResult { Best = best, Score = bestScore };
        }

        /// <summary>多起点退火：智能初始 / 原始布局 / 随机布局各跑一轮，取全局最优（离线评估极快，开销可忽略）。</summary>
        private AnnealResult AnnealMultiStart(SearchContext ctx, List<Slot> smartStart, List<Slot> original,
            System.Random rng, int iterations, int restarts, float temp0, long deadlineTicks = 0)
        {
            var globalBest = CloneSlots(original);
            double globalScore = EvaluateLayout(ctx, globalBest);

            var starts = new List<List<Slot>> { smartStart };
            if (plugin.EnableRandomStarts.Value)
            {
                starts.Add(original);
                var r1 = CloneSlots(original);
                ScrambleForSearch(ctx, r1, rng);
                starts.Add(r1);
                var r2 = CloneSlots(original);
                ScrambleForSearch(ctx, r2, rng);
                starts.Add(r2);
            }

            foreach (List<Slot> start in starts)
            {
                if (SearchDeadlineReached(deadlineTicks))
                {
                    ctx.searchBudgetReached = true;
                    break;
                }
                var res = Anneal(ctx, start, rng, iterations, restarts, temp0, deadlineTicks);
                if (res.Score > globalScore)
                {
                    globalScore = res.Score;
                    globalBest = res.Best;
                }
            }

            return new AnnealResult { Best = globalBest, Score = globalScore };
        }

        // ---------------------------------------------------------------- 邻域操作（含定向移动）

        private void Mutate(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            int[] itemIdx = ctx.itemIndexScratch;
            int[] emptyIdx = ctx.emptyIndexScratch;
            int itemCount = 0;
            int emptyCount = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].hasItem)
                {
                    itemIdx[itemCount++] = i;
                }
                else
                {
                    emptyIdx[emptyCount++] = i;
                }
            }

            if (itemCount == 0)
            {
                return;
            }

            // 手动提权使用额外的一次定向尝试，不挤占原有邻域操作概率；未提权时算法分布完全不变。
            if (ctx.manualPriorityCount > 0 && rng.Next(100) < 16)
            {
                if (TryManualPriorityMove(ctx, slots, rng)) return;
            }

            int roll = rng.Next(100);

            // 原有定向移动族
            if (roll < 12 && plugin.CriteriaMoveChance.Value > 0f)
            {
                if (TryCriteriaMove(ctx, slots, rng)) return;
            }
            if (roll < 22 && plugin.PlanetBonus.Value > 0f)
            {
                if (TryPlanetMove(ctx, slots, rng)) return;
            }
            if (roll < 32 && plugin.CompassBonus.Value > 0f)
            {
                if (TryCompassMove(ctx, slots, rng)) return;
            }
            if (roll < 37 && ctx.burdens.Count > 0)
            {
                if (TryBurdenDump(ctx, slots, rng)) return;
            }
            if (roll < 45 && ctx.whitePaperTargets.Count > 0 && plugin.WhitePaperComboBonus.Value > 0f)
            {
                if (TryWhitePaperMove(ctx, slots, rng)) return;
            }
            if (roll < 52 && plugin.HourglassBonus.Value > 0f)
            {
                if (TryHourglassMove(ctx, slots, rng)) return;
            }
            if (roll < 58 && plugin.RayShardBonus.Value > 0f)
            {
                if (TryRayShardMove(ctx, slots, rng)) return;
            }

            // 随机移动/交换/旋转
            if (roll < 75 && emptyCount > 0)
            {
                int a = itemIdx[rng.Next(itemCount)];
                int b = emptyIdx[rng.Next(emptyCount)];
                SwapSlots(slots, a, b);
                return;
            }

            if (roll < 92)
            {
                if (itemCount >= 2)
                {
                    int a = itemIdx[rng.Next(itemCount)];
                    int b = itemIdx[rng.Next(itemCount)];
                    if (a != b) SwapSlots(slots, a, b);
                }
                return;
            }

            for (int tries = 0; tries < 8; tries++)
            {
                int a = itemIdx[rng.Next(itemCount)];
                Slot slot = slots[a];
                if (slot.tablet != null &&
                    ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo tabletInfo) &&
                    tabletInfo != null && tabletInfo.tabletRotatable)
                {
                    slot.rotation = (slot.rotation + 1 + rng.Next(3)) % 4;
                    return;
                }
            }

            if (itemCount >= 2)
            {
                int a = itemIdx[rng.Next(itemCount)];
                int b = itemIdx[rng.Next(itemCount)];
                if (a != b) SwapSlots(slots, a, b);
            }
        }

        private bool TryManualPriorityMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            List<int> selected = ctx.moveScratchA;
            selected.Clear();
            int totalWeight = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                if (slot == null || !slot.hasItem || slot.charm == null ||
                    !ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo item) ||
                    item == null || item.manualPriorityRank <= 0)
                {
                    continue;
                }
                selected.Add(i);
                totalWeight += Math.Max(1, 64 / (item.manualPriorityRank * item.manualPriorityRank));
            }
            if (selected.Count == 0)
            {
                return false;
            }

            int ticket = rng.Next(Math.Max(1, totalWeight));
            int from = selected[0];
            ItemInfo fromInfo = null;
            foreach (int cell in selected)
            {
                ItemInfo candidate = ctx.itemByInstance[slots[cell].instanceID];
                ticket -= Math.Max(1, 64 / (candidate.manualPriorityRank * candidate.manualPriorityRank));
                if (ticket < 0)
                {
                    from = cell;
                    fromInfo = candidate;
                    break;
                }
            }
            if (fromInfo == null)
            {
                fromInfo = ctx.itemByInstance[slots[from].instanceID];
            }

            int currentLevel = ctx.disabled[from]
                ? int.MinValue / 4
                : (ctx.cellLevel[from] + fromInfo.enchant) * ctx.mysticFactor[from];
            int best = -1;
            int bestLevel = currentLevel;
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                if (cell == from || !IsAllowedLockedRow(fromInfo, cell / ctx.width) || ctx.disabled[cell])
                {
                    continue;
                }

                Slot target = slots[cell];
                if (target != null && target.hasItem && target.tablet != null)
                {
                    continue; // 不用定向移动打乱石板；普通退火仍可评估这种变化。
                }
                if (target != null && target.hasItem &&
                    ctx.itemByInstance.TryGetValue(target.instanceID, out ItemInfo targetInfo) && targetInfo != null)
                {
                    if (targetInfo.manualPriorityRank > 0 &&
                        targetInfo.manualPriorityRank <= fromInfo.manualPriorityRank)
                    {
                        continue; // 不能为了较低排名的神器挤走更高排名神器。
                    }
                    if (!IsAllowedLockedRow(targetInfo, from / ctx.width))
                    {
                        continue;
                    }
                }

                int level = (ctx.cellLevel[cell] + fromInfo.enchant) * ctx.mysticFactor[cell];
                if (level > bestLevel)
                {
                    bestLevel = level;
                    best = cell;
                }
            }
            if (best < 0)
            {
                return false;
            }
            SwapSlots(slots, from, best);
            return true;
        }

        private bool TryCriteriaMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            List<int> candidates = ctx.moveScratchA;
            candidates.Clear();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].hasItem &&
                    ctx.itemByInstance.TryGetValue(slots[i].instanceID, out ItemInfo item) &&
                    item != null && item.isCharm && KindPriority(item.kind) >= 2)
                {
                    candidates.Add(i);
                }
            }
            if (candidates.Count == 0)
            {
                return false;
            }

            int from = candidates[rng.Next(candidates.Count)];
            bool preferIgnore = ctx.itemByInstance.TryGetValue(slots[from].instanceID, out ItemInfo fromInfo) &&
                                fromInfo != null && fromInfo.preferIgnoreCells;
            CharmPositionKind kind = fromInfo != null ? fromInfo.kind : CharmPositionKind.None;

            List<int> targets = ctx.moveScratchB;
            targets.Clear();
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                if (cell == from)
                {
                    continue;
                }
                int x = cell % ctx.width;
                int y = cell / ctx.width;
                bool isIgnore = ctx.ignore[cell];
                bool natural = IsSatisfyingCell(kind, x, y, cell, ctx.storage, ctx.width);
                bool sat = isIgnore || natural;
                bool occupiedByRestricted = slots[cell].hasItem &&
                    ctx.itemByInstance.TryGetValue(slots[cell].instanceID, out ItemInfo targetInfo) &&
                    targetInfo != null && targetInfo.isCharm && KindPriority(targetInfo.kind) >= 2;
                if (sat && !occupiedByRestricted)
                {
                    // 冰锁类：只去豁免格（有豁免格目标时）；普通受限：只去自然满足格
                    if (preferIgnore && !isIgnore)
                    {
                        continue;
                    }
                    if (!preferIgnore && !natural)
                    {
                        continue;
                    }
                    targets.Add(cell);
                }
            }
            // 冰锁类没有可用豁免格时，回退到自然满足格
            if (targets.Count == 0 && preferIgnore)
            {
                for (int cell = 0; cell < ctx.storage; cell++)
                {
                    if (cell == from)
                    {
                        continue;
                    }
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    bool occupiedByRestricted = slots[cell].hasItem &&
                        ctx.itemByInstance.TryGetValue(slots[cell].instanceID, out ItemInfo targetInfo) &&
                        targetInfo != null && targetInfo.isCharm && KindPriority(targetInfo.kind) >= 2;
                    if (IsSatisfyingCell(kind, x, y, cell, ctx.storage, ctx.width) &&
                        !occupiedByRestricted)
                    {
                        targets.Add(cell);
                    }
                }
            }
            if (targets.Count == 0)
            {
                return false;
            }
            SwapSlots(slots, from, targets[rng.Next(targets.Count)]);
            return true;
        }

        /// <summary>行星聚簇：把 PLANET 藏品移到行星望远镜相邻空格（或交换）。</summary>
        private bool TryPlanetMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            // 找一个望远镜
            int moduleIdx = -1;
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot slot = slots[cell];
                if (slot != null && slot.hasItem &&
                    ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo item) &&
                    item != null && item.isPlanetModule)
                {
                    moduleIdx = cell;
                    break;
                }
            }
            if (moduleIdx < 0)
            {
                return false;
            }

            // 找一个不在望远镜身边的 PLANET 藏品
            int mx = moduleIdx % ctx.width;
            int my = moduleIdx / ctx.width;
            List<int> planets = ctx.moveScratchA;
            planets.Clear();
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot slot = slots[cell];
                if (cell != moduleIdx && slot != null && slot.hasItem &&
                    ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo item) && item != null &&
                    item.isPlanetCategory && !item.excludeFromPlanetCluster)
                {
                    int px = cell % ctx.width;
                    int py = cell / ctx.width;
                    bool adjacent = Math.Abs(px - mx) <= 1 && Math.Abs(py - my) <= 1;
                    if (!adjacent)
                    {
                        planets.Add(cell);
                    }
                }
            }
            if (planets.Count == 0)
            {
                return false;
            }

            // 找一个望远镜身边的空格（或可交换格）
            List<int> targets = ctx.moveScratchB;
            targets.Clear();
            for (int i = 0; i < 8; i++)
            {
                int nx = mx + Neighbor8[i].x;
                int ny = my + Neighbor8[i].y;
                if (InBounds(nx, ny, ctx.width, ctx.height))
                {
                    int idx = ny * ctx.width + nx;
                    if (idx >= 0 && idx < ctx.storage &&
                        !(slots[idx] != null && slots[idx].hasItem))
                    {
                        targets.Add(idx);
                    }
                }
            }
            if (targets.Count == 0)
            {
                return false;
            }

            SwapSlots(slots, planets[rng.Next(planets.Count)], targets[rng.Next(targets.Count)]);
            return true;
        }

        /// <summary>
        /// 罗盘定向移动：整理前已配对的竖链按整体探索新位置；未配对的指北针仍可自动寻找
        /// 伤害类藏品/指北针。
        /// </summary>
        private bool TryCompassMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            if (ctx.compassChains.Count > 0 && rng.Next(100) < 70)
            {
                CompassChain chain = ctx.compassChains[rng.Next(ctx.compassChains.Count)];
                var positions = ctx.compassPositionScratch;
                FillItemPositionMap(slots, positions);
                if (positions.TryGetValue(chain.instanceIDs[0], out int rootCell))
                {
                    HashSet<int> chainMembers = ctx.instanceSetScratch;
                    chainMembers.Clear();
                    for (int i = 0; i < chain.instanceIDs.Count; i++)
                    {
                        chainMembers.Add(chain.instanceIDs[i]);
                    }
                    List<int> roots = ctx.moveScratchA;
                    roots.Clear();
                    for (int cell = 0; cell < ctx.storage; cell++)
                    {
                        if (cell == rootCell || !CanPlaceCompassChain(ctx, chain, cell, null))
                        {
                            continue;
                        }
                        Slot target = slots[cell];
                        if (target != null && target.hasItem && chainMembers.Contains(target.instanceID))
                        {
                            continue;
                        }
                        roots.Add(cell);
                    }
                    if (roots.Count > 0)
                    {
                        // 这里只移动链首；Anneal 在评分前统一调用 RestoreCompassBindings，后续针会跟上。
                        SwapSlots(slots, rootCell, roots[rng.Next(roots.Count)]);
                        return true;
                    }
                }
            }

            List<int> compasses = ctx.moveScratchA;
            compasses.Clear();
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot slot = slots[cell];
                if (slot != null && slot.hasItem &&
                    ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo info) && info != null &&
                    info.isCompass && !ctx.compassChainInstances.Contains(slot.instanceID))
                {
                    compasses.Add(cell);
                }
            }
            if (compasses.Count == 0)
            {
                return false;
            }
            HashSet<int> unboundCompassCells = ctx.instanceSetScratch;
            unboundCompassCells.Clear();
            for (int i = 0; i < compasses.Count; i++)
            {
                unboundCompassCells.Add(compasses[i]);
            }

            // 目标：把某块指北针移到"上方有有效依赖"的格子（空格或可交换格），或把攻击类藏品移到指北针上方
            List<int> compassTargets = ctx.moveScratchB;
            List<int> damageTargets = ctx.moveScratchC;
            compassTargets.Clear();
            damageTargets.Clear();
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                int x = cell % ctx.width;
                int y = cell / ctx.width;
                int above = (y - 1) * ctx.width + x;
                if (y > 0 && above >= 0 && above < ctx.storage && slots[above] != null && slots[above].hasItem && slots[above].charm != null)
                {
                    bool valid = ctx.itemByInstance.TryGetValue(slots[above].instanceID, out ItemInfo aboveInfo) &&
                                 aboveInfo != null && (aboveInfo.isCompass || aboveInfo.isAttackable);
                    if (valid)
                    {
                        // 目标格：空格，或任意非指北针物品（允许交换腾位；评分会拒绝不划算的交换）
                        if (!(slots[cell] != null && slots[cell].hasItem && slots[cell].charm is Charm_UpCharmDamage))
                        {
                            compassTargets.Add(cell);
                        }
                    }
                }
                // 反向：把攻击类藏品放到指北针上方（目标格为空或可交换）
                int below = (y + 1) * ctx.width + x;
                if (y < ctx.height - 1 && below < ctx.storage && unboundCompassCells.Contains(below))
                {
                    if (!(slots[cell] != null && slots[cell].hasItem))
                    {
                        damageTargets.Add(cell);
                    }
                }
            }

            if (compassTargets.Count > 0 && rng.Next(2) == 0)
            {
                int from = compasses[rng.Next(compasses.Count)];
                SwapSlots(slots, from, compassTargets[rng.Next(compassTargets.Count)]);
                return true;
            }

            if (damageTargets.Count > 0)
            {
                List<int> damages = ctx.moveScratchD;
                damages.Clear();
                for (int cell = 0; cell < ctx.storage; cell++)
                {
                    Slot slot = slots[cell];
                    if (slot != null && slot.hasItem &&
                        ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo info) && info != null &&
                        info.isAttackable && !info.isCompass &&
                        !ctx.compassChainInstances.Contains(slot.instanceID))
                    {
                        damages.Add(cell);
                    }
                }
                if (damages.Count > 0)
                {
                    SwapSlots(slots, damages[rng.Next(damages.Count)], damageTargets[rng.Next(damageTargets.Count)]);
                    return true;
                }
            }

            return false;
        }

        private static bool CanUseWhitePaperTriple(SearchContext ctx, List<Slot> slots, int rootCell,
            int paperInstanceID, int leftInstanceID, int rightInstanceID)
        {
            int row = rootCell / ctx.width;
            for (int offset = 0; offset < 3; offset++)
            {
                int cell = rootCell + offset;
                if (cell < 0 || cell >= ctx.storage || cell / ctx.width != row)
                {
                    return false;
                }
                Slot occupant = slots[cell];
                if (occupant == null || !occupant.hasItem)
                {
                    continue;
                }
                if (ctx.compassChainInstances.Contains(occupant.instanceID))
                {
                    return false; // 不拆用户已经锁定的指北针竖链。
                }
                if (ctx.itemByInstance.TryGetValue(occupant.instanceID, out ItemInfo occupantInfo) && occupantInfo != null)
                {
                    bool selected = occupant.instanceID == paperInstanceID ||
                                    occupant.instanceID == leftInstanceID ||
                                    occupant.instanceID == rightInstanceID;
                    if (occupantInfo.isWhitePaper && occupant.instanceID != paperInstanceID)
                    {
                        return false;
                    }
                    if (occupantInfo.isRowLocked && !selected)
                    {
                        return false;
                    }
                }
            }

            if (ctx.itemByInstance.TryGetValue(leftInstanceID, out ItemInfo leftInfo) && leftInfo != null &&
                !IsAllowedLockedRow(leftInfo, row))
            {
                return false;
            }
            if (ctx.itemByInstance.TryGetValue(rightInstanceID, out ItemInfo rightInfo) && rightInfo != null &&
                !IsAllowedLockedRow(rightInfo, row))
            {
                return false;
            }
            if (ctx.itemByInstance.TryGetValue(paperInstanceID, out ItemInfo paperInfo) && paperInfo != null &&
                !IsAllowedLockedRow(paperInfo, row))
            {
                return false;
            }
            return true;
        }

        /// <summary>把一张白纸定向摆到“当前数量最大且未满”的连击两件神器之间。</summary>
        private bool TryWhitePaperMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            if (ctx.whitePaperTargets.Count == 0 || plugin.WhitePaperComboBonus.Value <= 0f)
            {
                return false;
            }

            RefreshWhitePaperAssignments(ctx, slots);
            List<int> paperCells = ctx.moveScratchA;
            paperCells.Clear();
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot slot = slots[cell];
                if (slot != null && slot.hasItem &&
                    ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo info) &&
                    info != null && info.isWhitePaper)
                {
                    paperCells.Add(cell);
                }
            }
            if (paperCells.Count == 0)
            {
                return false;
            }

            int paperStart = rng.Next(paperCells.Count);
            for (int paperOffset = 0; paperOffset < paperCells.Count; paperOffset++)
            {
                int paperCell = paperCells[(paperStart + paperOffset) % paperCells.Count];
                int paperInstanceID = slots[paperCell].instanceID;

                for (int targetIndex = 0; targetIndex < ctx.whitePaperTargets.Count; targetIndex++)
                {
                    WhitePaperComboTarget target = ctx.whitePaperTargets[targetIndex];
                    bool currentlyMatches = WhitePaperMatchesTarget(ctx, slots, paperCell, targetIndex);
                    int otherAssignments = ctx.whitePaperAssignmentScratch[targetIndex] - (currentlyMatches ? 1 : 0);
                    if (target.baseCount + otherAssignments >= target.cap)
                    {
                        continue;
                    }

                    List<int> artifacts = ctx.moveScratchB;
                    artifacts.Clear();
                    for (int cell = 0; cell < ctx.storage; cell++)
                    {
                        Slot slot = slots[cell];
                        if (slot == null || !slot.hasItem || slot.instanceID == paperInstanceID ||
                            ctx.compassChainInstances.Contains(slot.instanceID) ||
                            !ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo info) || info == null ||
                            info.isWhitePaper || !info.comboCategories.Contains(target.category))
                        {
                            continue;
                        }
                        artifacts.Add(cell);
                    }
                    if (artifacts.Count < 2)
                    {
                        continue;
                    }

                    // 这张已经处在当前最优的可补位连击中；保留它，但继续检查
                    // 其他白纸，让多出来的白纸能转向下一个未满连击。
                    if (currentlyMatches)
                    {
                        break;
                    }

                    int bestRoot = -1;
                    int bestLeftID = -1;
                    int bestRightID = -1;
                    float bestScore = float.MinValue;
                    int pairAttempts = Math.Min(16, Math.Max(1, artifacts.Count * 2));
                    for (int attempt = 0; attempt < pairAttempts; attempt++)
                    {
                        int leftPick = rng.Next(artifacts.Count);
                        int rightPick = rng.Next(artifacts.Count - 1);
                        if (rightPick >= leftPick) rightPick++;
                        int leftCell = artifacts[leftPick];
                        int rightCell = artifacts[rightPick];
                        int leftID = slots[leftCell].instanceID;
                        int rightID = slots[rightCell].instanceID;
                        if (rng.Next(2) == 0)
                        {
                            int tmp = leftID;
                            leftID = rightID;
                            rightID = tmp;
                        }

                        for (int root = 0; root < ctx.storage; root++)
                        {
                            if (root % ctx.width > ctx.width - 3 || root + 2 >= ctx.storage ||
                                !CanUseWhitePaperTriple(ctx, slots, root, paperInstanceID, leftID, rightID))
                            {
                                continue;
                            }
                            float score = ctx.cellLevel[root] + ctx.cellLevel[root + 2] + ctx.cellLevel[root + 1] * 0.25f;
                            if (slots[root].hasItem && slots[root].instanceID == leftID) score += 10f;
                            if (slots[root + 1].hasItem && slots[root + 1].instanceID == paperInstanceID) score += 10f;
                            if (slots[root + 2].hasItem && slots[root + 2].instanceID == rightID) score += 10f;
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestRoot = root;
                                bestLeftID = leftID;
                                bestRightID = rightID;
                            }
                        }
                    }

                    if (bestRoot >= 0)
                    {
                        var positions = ctx.compassPositionScratch;
                        FillItemPositionMap(slots, positions);
                        SwapSlotsAndTrack(slots, positions, positions[bestLeftID], bestRoot);
                        SwapSlotsAndTrack(slots, positions, positions[paperInstanceID], bestRoot + 1);
                        SwapSlotsAndTrack(slots, positions, positions[bestRightID], bestRoot + 2);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>沙漏配对：把发光的沙漏移到 CD 最长的魔法书左边（或把 CD 长的魔法书移到沙漏右边）。</summary>
        private bool TryHourglassMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            List<int> hourglassIdx = ctx.moveScratchA;
            List<int> magicIdx = ctx.moveScratchB;
            hourglassIdx.Clear();
            magicIdx.Clear();
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot slot = slots[cell];
                if (slot == null || !slot.hasItem ||
                    !ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo item) || item == null)
                {
                    continue;
                }
                if (item.isHourglass)
                {
                    hourglassIdx.Add(cell);
                }
                else if (item.isMagicBook)
                {
                    magicIdx.Add(cell);
                }
            }
            if (hourglassIdx.Count == 0 || magicIdx.Count == 0)
            {
                return false;
            }

            // CD 最长的魔法书优先配对（CD 越长，沙漏放它左边收益越大）
            magicIdx.Sort((a, b) =>
            {
                float ca = ctx.itemByInstance.TryGetValue(slots[a].instanceID, out ItemInfo ia) && ia != null ? ia.magicCd : 0f;
                float cb = ctx.itemByInstance.TryGetValue(slots[b].instanceID, out ItemInfo ib) && ib != null ? ib.magicCd : 0f;
                return cb.CompareTo(ca);
            });
            int magic = magicIdx[rng.Next(Math.Min(2, magicIdx.Count))];
            int mx = magic % ctx.width;
            int my = magic / ctx.width;

            // 方式1：把某块沙漏移到这本魔法书左边格（空格或可交换格）
            if (mx > 0)
            {
                int left = my * ctx.width + (mx - 1);
                if (left >= 0 && left < ctx.storage &&
                    !(slots[left] != null && slots[left].hasItem && slots[left].charm is Charm_RightSpellCooldownHelper))
                {
                    int hg = hourglassIdx[rng.Next(hourglassIdx.Count)];
                    if (left != hg)
                    {
                        SwapSlots(slots, hg, left);
                        return true;
                    }
                }
            }

            // 方式2：把 CD 最长的魔法书移到某块沙漏右边格（空格或可交换格）
            int h = hourglassIdx[rng.Next(hourglassIdx.Count)];
            int hx = h % ctx.width;
            int hy = h / ctx.width;
            if (hx + 1 < ctx.width)
            {
                int right = hy * ctx.width + (hx + 1);
                if (right >= 0 && right < ctx.storage &&
                    !(slots[right] != null && slots[right].hasItem && slots[right].charm is Charm_Magic))
                {
                    if (right != magic)
                    {
                        SwapSlots(slots, magic, right);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>雷伊星碎片配对：把碎片移到耗蓝最高的魔法书右侧（或把耗蓝高的魔法书移到碎片左侧）。</summary>
        private bool TryRayShardMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            List<int> shardIdx = ctx.moveScratchA;
            List<int> magicIdx = ctx.moveScratchB;
            shardIdx.Clear();
            magicIdx.Clear();
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot slot = slots[cell];
                if (slot == null || !slot.hasItem ||
                    !ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo item) || item == null)
                {
                    continue;
                }
                if (item.isRayShard)
                {
                    shardIdx.Add(cell);
                }
                else if (item.isMagicBook)
                {
                    magicIdx.Add(cell);
                }
            }
            if (shardIdx.Count == 0 || magicIdx.Count == 0)
            {
                return false;
            }

            // 耗蓝最高的魔法书优先配对（耗蓝越高，碎片放它右侧收益越大）
            magicIdx.Sort((a, b) =>
            {
                int ca = ctx.itemByInstance.TryGetValue(slots[a].instanceID, out ItemInfo ia) && ia != null ? ia.magicMpCost : 0;
                int cb = ctx.itemByInstance.TryGetValue(slots[b].instanceID, out ItemInfo ib) && ib != null ? ib.magicMpCost : 0;
                return cb.CompareTo(ca);
            });
            int magic = magicIdx[rng.Next(Math.Min(2, magicIdx.Count))];
            int mx = magic % ctx.width;
            int my = magic / ctx.width;

            // 方式1：把某块碎片移到这本魔法书右边格（空格或可交换格）
            if (mx + 1 < ctx.width)
            {
                int right = my * ctx.width + (mx + 1);
                if (right >= 0 && right < ctx.storage &&
                    !(slots[right] != null && slots[right].hasItem && slots[right].charm is Charm_Magic))
                {
                    int sh = shardIdx[rng.Next(shardIdx.Count)];
                    if (right != sh)
                    {
                        SwapSlots(slots, sh, right);
                        return true;
                    }
                }
            }

            // 方式2：把耗蓝最高的魔法书移到某块碎片左侧格（空格或可交换格）
            int sd = shardIdx[rng.Next(shardIdx.Count)];
            int sx = sd % ctx.width;
            int sy = sd / ctx.width;
            if (sx - 1 >= 0)
            {
                int left = sy * ctx.width + (sx - 1);
                if (left >= 0 && left < ctx.storage &&
                    !(slots[left] != null && slots[left].hasItem && slots[left].charm is Charm_Magic))
                {
                    if (left != magic)
                    {
                        SwapSlots(slots, magic, left);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>负担物品倾倒：把负面藏品移到当前最差的（负等级）格子。</summary>
        private bool TryBurdenDump(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            int worst = -1;
            int worstLevel = int.MaxValue;
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                int lvl = ctx.cellLevel[cell];
                if (lvl < worstLevel)
                {
                    worstLevel = lvl;
                    worst = cell;
                }
            }
            if (worst < 0)
            {
                return false;
            }

            List<int> burdenIdx = ctx.moveScratchA;
            burdenIdx.Clear();
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot slot = slots[cell];
                if (slot != null && slot.hasItem &&
                    ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo item) &&
                    item != null && item.isBurden)
                {
                    burdenIdx.Add(cell);
                }
            }
            if (burdenIdx.Count == 0)
            {
                return false;
            }

            int from = burdenIdx[rng.Next(burdenIdx.Count)];
            if (from != worst)
            {
                SwapSlots(slots, from, worst);
                return true;
            }
            return false;
        }

        private static void SwapSlots(List<Slot> slots, int a, int b)
        {
            Slot tmp = slots[a];
            slots[a] = slots[b];
            slots[b] = tmp;
        }

        /// <summary>
        /// 后台搜索专用乱序。所有石板属性均从主线程构建好的 ItemInfo 快照读取，
        /// 不调用 DungeonManager 或任何 Unity/游戏对象方法。
        /// </summary>
        private static void ScrambleForSearch(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            var occupied = new List<int>();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].hasItem)
                {
                    occupied.Add(i);
                }
            }

            for (int i = occupied.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                if (occupied[i] != occupied[j])
                {
                    SwapSlots(slots, occupied[i], occupied[j]);
                }
            }

            foreach (int idx in occupied)
            {
                Slot slot = slots[idx];
                if (slot.tablet != null && rng.Next(2) == 0 &&
                    ctx.itemByInstance.TryGetValue(slot.instanceID, out ItemInfo info) &&
                    info != null && info.tabletRotatable)
                {
                    slot.rotation = rng.Next(4);
                }
            }
        }

        private static long CreateSearchDeadline(int budgetMs)
        {
            if (budgetMs <= 0)
            {
                return 0;
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long budgetTicks = (long)(budgetMs * (double)System.Diagnostics.Stopwatch.Frequency / 1000d);
            return now + Math.Max(1L, budgetTicks);
        }

        private static bool SearchDeadlineReached(long deadlineTicks)
        {
            return deadlineTicks > 0 && System.Diagnostics.Stopwatch.GetTimestamp() >= deadlineTicks;
        }

        /// <summary>
        /// 将布局复制进已经分配好的缓冲区。搜索热循环只覆写字段，不再创建 Slot/List/数组，
        /// 从而避免数百万个短命对象触发 Mono GC。
        /// </summary>
        private static void CopySlots(List<Slot> src, List<Slot> dst)
        {
            if (src == null || dst == null || src.Count != dst.Count)
            {
                throw new ArgumentException("布局缓冲区大小不一致");
            }
            for (int i = 0; i < src.Count; i++)
            {
                Slot from = src[i];
                Slot to = dst[i];
                to.hasItem = from.hasItem;
                to.instanceID = from.instanceID;
                to.entityID = from.entityID;
                to.quantity = from.quantity;
                to.charm = from.charm;
                to.tablet = from.tablet;
                to.rotation = from.rotation;
            }
        }

        private static bool LayoutsEquivalent(List<Slot> a, List<Slot> b)
        {
            if (a == null || b == null || a.Count != b.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                Slot left = a[i];
                Slot right = b[i];
                if (left == null || right == null || left.hasItem != right.hasItem)
                {
                    return false;
                }
                if (!left.hasItem)
                {
                    continue;
                }
                if (left.instanceID != right.instanceID)
                {
                    return false;
                }
                if (left.tablet != null || right.tablet != null)
                {
                    if (left.tablet == null || right.tablet == null ||
                        ((left.rotation - right.rotation) & 3) != 0)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static List<Slot> CloneSlots(List<Slot> src)
        {
            var dst = new List<Slot>(src.Count);
            for (int i = 0; i < src.Count; i++)
            {
                dst.Add(src[i].Clone());
            }
            return dst;
        }

        // ---------------------------------------------------------------- 状态捕获/应用

        private static List<Slot> CaptureState(GridInventory inv)
        {
            var slots = new List<Slot>(inv.CurrentInventoryStorage);
            for (int i = 0; i < inv.CurrentInventoryStorage; i++)
            {
                ItemPosition pos = inv.IdxToPos(i);
                if (inv.inventoryMatrix.TryGetValue(pos, out var item) && item != null)
                {
                    slots.Add(new Slot
                    {
                        hasItem = true,
                        instanceID = item.InstanceID,
                        entityID = item.EntityID,
                        quantity = item.Quantity,
                        charm = item.Charm,
                        tablet = item.StoneTablet,
                        rotation = item.StoneTablet != null ? item.StoneTablet.rotation : 0
                    });
                }
                else
                {
                    slots.Add(Slot.Empty());
                }
            }
            return slots;
        }

        private static float SafeScore(GridInventory inv)
        {
            if (!NetworkServer.active)
            {
                return float.NaN;
            }
            return inv.EvaluateCurrentAutoArrangeScore();
        }

        // ---------------------------------------------------------------- 自检

        private void SortSelfTest(GridInventory inv)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var original = CaptureState(inv);
            if (!VerifyInventorySnapshot(inv, original))
            {
                Notify("背包状态未就绪，本次未整理（请稍后再试）。");
                return;
            }
            SearchContext ctx = BuildContext(inv, original);
            double before = EvaluateLayout(ctx, original);
            float beforeGame = SafeScore(inv);

            LogItemIdentification(ctx);

            var rng = new System.Random(Environment.TickCount);
            var scrambled = CloneSlots(original);
            Scramble(scrambled, rng);
            RestoreCompassBindings(ctx, scrambled);
            double scrambledScore = EvaluateLayout(ctx, scrambled);
            Plugin.Log.LogInfo($"自检：已随机打乱（离线 {before:F0} -> {scrambledScore:F0}）。");

            double smartScore = scrambledScore;
            var start = scrambled;
            if (plugin.EnableSmartStart.Value)
            {
                start = BuildSmartStart(ctx);
                smartScore = EvaluateLayout(ctx, start);
                Plugin.Log.LogInfo($"自检：智能初始布局（离线 {scrambledScore:F0} -> {smartScore:F0}）。");
            }

            var result = AnnealMultiStart(ctx, start, original, rng,
                plugin.EnhancedIterations.Value, plugin.EnhancedRestarts.Value,
                plugin.EnhancedTemperature.Value);

            List<Slot> finalLayout = result.Score >= before - 0.5 ? result.Best : original;
            // 自检同样用"交换/旋转挪移"应用，避免清空重写的中间态风险
            ApplyByMoves(inv, original, finalLayout);
            float finalGame = SafeScore(inv);
            double finalOffline = EvaluateLayout(ctx, finalLayout);

            LogLayoutGrid(ctx, finalLayout, "自检");
            LogLayoutAnalysis(ctx, finalLayout, "自检");

            sw.Stop();
            Plugin.Log.LogInfo(
                $"自检完成（{sw.ElapsedMilliseconds}ms）：离线 {before:F0} → 乱序 {scrambledScore:F0} → " +
                $"智能初始 {smartScore:F0} → 整理后 {finalOffline:F0}（搜索最优 {result.Score:F0}）；" +
                $"游戏评分 {beforeGame:F0} -> {finalGame:F0}；布局 {ctx.items.Count} 件" +
                $"（石板{ctx.steles.Count} 护符{ctx.charms.Count} 负担{ctx.burdens.Count}）");
            Notify($"自检：离线 {before:F0} → 整理后 {finalOffline:F0}（{sw.ElapsedMilliseconds}ms）");
        }

        private static void Scramble(List<Slot> slots, System.Random rng)
        {
            var occupied = new List<int>();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].hasItem)
                {
                    occupied.Add(i);
                }
            }

            for (int i = occupied.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                if (occupied[i] != occupied[j])
                {
                    SwapSlots(slots, occupied[i], occupied[j]);
                }
            }

            foreach (int idx in occupied)
            {
                Slot slot = slots[idx];
                if (slot.tablet != null && rng.Next(2) == 0 &&
                    DungeonManager.IsTabletRotatable(slot.tablet.instanceID, slot.tablet.isRotatable))
                {
                    slot.rotation = rng.Next(4);
                }
            }
        }

        // ---------------------------------------------------------------- 通知

        private void Notify(string msg)
        {
            Plugin.Log.LogInfo(msg);
            if (!plugin.ShowNotifications.Value)
            {
                return;
            }

            try
            {
                if (UIManager.Instance != null)
                {
                    UIManager.Instance.GetElement<UI_SystemMessage>()?.Open(msg, 2.5f);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"游戏内提示显示失败: {ex.Message}");
            }
        }
    }
}
