using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
#if !BEPINEX5
using BepInEx.Unity.Mono;
using BepInEx.Unity.Mono.Configuration;
#endif
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SephiriaBackpackOrganizer
{
    /// <summary>
    /// 赛菲莉娅 (Sephiria) 背包自动整理插件 —— 入口。
    /// 按热键触发，对背包中的石板(StoneTablet)与护符(Charm)进行"神器等级加成"布局优化，
    /// 使护符摆放在高等级格位上、石板效果覆盖最大化。
    /// </summary>
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
#if BEPINEX5
    // BepInEx 5：BepInEx.BaseUnityPlugin（Unity Mono，MonoBehaviour，自带 Awake/Update 消息）
    public class Plugin : BepInEx.BaseUnityPlugin
#else
    // BepInEx 6：BepInEx.Unity.Mono.BaseUnityPlugin（MonoBehaviour，自带 Awake/Update 消息）
    public class Plugin : BepInEx.Unity.Mono.BaseUnityPlugin
#endif
    {
        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal ConfigEntry<KeyboardShortcut> Hotkey;
        internal ConfigEntry<SortMode> Mode;
        internal ConfigEntry<bool> ShowNotifications;
        internal ConfigEntry<bool> AutoSortOnSessionStart;
        internal ConfigEntry<float> SessionStableDelay;
        internal ConfigEntry<string> RowLockedItems;
        internal ConfigEntry<bool> SelfTest;

        internal ConfigEntry<int> VanillaIterations;
        internal ConfigEntry<bool> AllowTabletRotation;

        internal ConfigEntry<int> EnhancedIterations;
        internal ConfigEntry<int> EnhancedRestarts;
        internal ConfigEntry<float> EnhancedTemperature;
        internal ConfigEntry<int> SearchRounds;
        internal ConfigEntry<int> SearchTimeBudgetMs;
        internal ConfigEntry<bool> VerboseDiagnostics;

        internal ConfigEntry<int> ApplySwapsPerFrame;
        internal ConfigEntry<int> ApplyRotationClicksPerFrame;
        internal ConfigEntry<float> ApplyFrameBudgetMs;
        internal ConfigEntry<int> ApplyAckTimeoutMs;

        internal ConfigEntry<bool> ManualPriorityEnabled;
        internal ConfigEntry<bool> ShowManualPriorityBadge;
        internal ConfigEntry<float> ManualPriorityStrength;

        internal ConfigEntry<bool> EnableSmartStart;
        internal ConfigEntry<bool> EnableRandomStarts;
        internal ConfigEntry<float> CriteriaWeight;
        internal ConfigEntry<float> NegativeWeight;
        internal ConfigEntry<float> CriteriaMoveChance;

        internal ConfigEntry<bool> PriorityEnable;
        internal ConfigEntry<int> PriorityCommon;
        internal ConfigEntry<int> PriorityUncommon;
        internal ConfigEntry<int> PriorityRare;
        internal ConfigEntry<int> PriorityLegend;
        internal ConfigEntry<int> PriorityEternal;
        internal ConfigEntry<string> PriorityFixedItems;
        internal ConfigEntry<string> ForcedPriorityItems;
        internal ConfigEntry<string> IgnoreCellPreferredItems;
        internal ConfigEntry<float> PriorityWeight1;
        internal ConfigEntry<float> PriorityWeight2;
        internal ConfigEntry<float> PriorityWeight3;
        internal ConfigEntry<float> PriorityWeight4;
        internal ConfigEntry<bool> CompassTargetForcedHigh;
        internal ConfigEntry<string> PriorityLowValueItems;
        internal ConfigEntry<float> LowValueLevelFactor;
        internal ConfigEntry<string> PriorityMinLevelItems;

        internal ConfigEntry<float> PlanetBonus;
        internal ConfigEntry<string> PlanetClusterExcludedItems;
        internal ConfigEntry<string> HarmonyCrystalItems;
        internal ConfigEntry<float> HarmonyLevelBonus;
        internal ConfigEntry<string> DedicationBadgeItems;
        internal ConfigEntry<float> DedicationCompanionBonus;
        internal ConfigEntry<string> HourglassItems;
        internal ConfigEntry<float> HourglassBonus;
        internal ConfigEntry<string> RayShardItems;
        internal ConfigEntry<float> RayShardBonus;
        internal ConfigEntry<string> EclipseItems;
        internal ConfigEntry<string> OpposingScaleItems;
        internal ConfigEntry<float> WhitePaperComboBonus;
        internal ConfigEntry<float> CompassBonus;
        internal ConfigEntry<float> CompassUnpairedFactor;
        internal ConfigEntry<float> BurdenPenalty;
        internal ConfigEntry<string> BeltItems;
        internal ConfigEntry<float> BeltRowBonus;

        internal ConfigEntry<string> BurdenItemKeys;

        internal ConfigEntry<bool> MysticEnable;
        internal ConfigEntry<string> MysticCategory;
        internal ConfigEntry<float> MysticMultiplier;

        private InventorySorter sorter;
        private Harmony harmony;
        private bool autoSortedThisSession;
        private bool lastSessionActive;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Hotkey = Config.Bind("General", "Hotkey",
                new KeyboardShortcut(KeyCode.F8),
                "触发背包整理的快捷键（游戏使用新输入系统，插件会同时兼容新旧输入）");

            Mode = Config.Bind("General", "SortMode", SortMode.Enhanced,
                new ConfigDescription(
                    "整理模式：\n" +
                    "Vanilla = 调用游戏内置的自动排列（任意模式可用，含联机作为客户端时）；\n" +
                    "Enhanced = 后台增强搜索，并通过分帧交换/旋转安全应用；主机与联机客户端均可用。"));

            ShowNotifications = Config.Bind("General", "ShowNotifications", true,
                "是否在游戏内显示整理结果提示（屏幕系统消息）");

            AutoSortOnSessionStart = Config.Bind("General", "AutoSortOnSessionStart", false,
                "进入会话后自动整理一次背包（默认关闭，可在配置里开启）");

            SessionStableDelay = Config.Bind("General", "SessionStableDelay", 3f,
                new ConfigDescription("进入会话后等待背包初始化完成的秒数（期间按 F8 会提示稍后再试，" +
                    "防止背包未就绪时整理导致物品丢失）", new AcceptableValueRange<float>(0f, 30f)));

            RowLockedItems = Config.Bind("General", "RowLockedItems",
                "",
                "额外需固定在整理前原行的物品 LocalizedString key（逗号分隔）。"
                + "凯尔萨德尼钥匙无需填写：插件会自动按当前最多的坚固/余烬/冰川/魔法科技羁绊选择周期行");

            VanillaIterations = Config.Bind("Vanilla", "MaxIterations", 30,
                new ConfigDescription("游戏内置自动排列的最大迭代次数（原版默认 4，越大效果越好但耗时略增）",
                    new AcceptableValueRange<int>(1, 500)));

            AllowTabletRotation = Config.Bind("Vanilla", "AllowTabletRotation", true,
                "是否允许自动旋转石板以匹配加成覆盖范围");

            EnhancedIterations = Config.Bind("Enhanced", "Iterations", 3000,
                new ConfigDescription("增强模式的模拟退火迭代次数（v2.0 为全离线评估，毫秒级，可放心调大）",
                    new AcceptableValueRange<int>(10, 50000)));

            EnhancedRestarts = Config.Bind("Enhanced", "Restarts", 3,
                new ConfigDescription("增强模式的随机重启次数（每次重启从当前最优继续搜索）",
                    new AcceptableValueRange<int>(1, 10)));

            EnhancedTemperature = Config.Bind("Enhanced", "Temperature", 800f,
                new ConfigDescription("增强模式的初始温度（越大越倾向接受次优解，越容易跳出局部最优，但可能更“乱”）",
                    new AcceptableValueRange<float>(1f, 50000f)));

            SearchRounds = Config.Bind("Enhanced", "SearchRounds", 4,
                new ConfigDescription("每次整理的独立搜索轮数（每轮不同随机种子，取全局最优）。" +
                    "相当于自动重复按 F8 多次；搜索时间达到预算后会提前结束",
                    new AcceptableValueRange<int>(1, 10)));

            SearchTimeBudgetMs = Config.Bind("Enhanced", "SearchTimeBudgetMs", 0,
                new ConfigDescription("增强搜索的主线程时间预算（毫秒）。达到预算后保留当前最优布局并提前结束；" +
                    "0=不限制并完整执行所有搜索（默认，质量优先）。需要进一步限制停顿时建议 100~250；" +
                    "数值过低会跳过随机起点与后续重启，明显降低整理质量",
                    new AcceptableValueRange<int>(0, 1000)));

            ApplySwapsPerFrame = Config.Bind("Apply", "SwapsPerFrame", 2,
                new ConfigDescription("每帧最多执行的背包交换次数。数值越低越平滑，默认 2",
                    new AcceptableValueRange<int>(1, 10)));

            ApplyRotationClicksPerFrame = Config.Bind("Apply", "RotationClicksPerFrame", 4,
                new ConfigDescription("每帧最多执行的石板旋转点击次数。默认 4",
                    new AcceptableValueRange<int>(1, 12)));

            ApplyFrameBudgetMs = Config.Bind("Apply", "FrameBudgetMs", 2f,
                new ConfigDescription("每帧应用整理操作的时间预算（毫秒），默认 2",
                    new AcceptableValueRange<float>(0.25f, 10f)));

            ApplyAckTimeoutMs = Config.Bind("Apply", "NetworkAckTimeoutMs", 2000,
                new ConfigDescription("联机时等待服务器确认每批操作的最长毫秒数，默认 2000",
                    new AcceptableValueRange<int>(250, 10000)));

            ManualPriorityEnabled = Config.Bind("ManualPriority", "Enabled", true,
                "允许用鼠标中键点击背包中的神器来切换手动提权");

            ShowManualPriorityBadge = Config.Bind("ManualPriority", "ShowBadge", true,
                "在已提权神器左下角显示透明小字 P1、P2…；P1 是最后提权、权重最高的神器");

            ManualPriorityStrength = Config.Bind("ManualPriority", "Strength", 6000f,
                new ConfigDescription("手动提权对神器每个等级的额外奖励/扣分。P1 为 6000 分，P2/P3…每级固定递减 200 分，负等级按相同数值扣分",
                    new AcceptableValueRange<float>(1f, 100000f)));

            VerboseDiagnostics = Config.Bind("Debug", "VerboseDiagnostics", false,
                "输出完整物品识别、布局网格和特殊机制分析。关闭可减少每次整理后的额外评分与日志开销");

            EnableSmartStart = Config.Bind("Smart", "EnableSmartStart", true,
                new ConfigDescription(
                    "启用智能初始布局：石板贪心摆位（正覆盖最大化、负等级推出背包外或压到非护符下）、" +
                    "受限护符优先放满足位置条件的格子或豁免格（解除限制的石板格）"));

            EnableRandomStarts = Config.Bind("Smart", "EnableRandomStarts", true,
                "多起点搜索：退火从 智能初始/原始/随机 多个起点跑，取全局最优（离线评估极快，开销可忽略）");

            CriteriaWeight = Config.Bind("Smart", "CriteriaWeight", 150f,
                new ConfigDescription("引导评分中位置条件满足/不满足的奖惩（0=关闭）",
                    new AcceptableValueRange<float>(0f, 5000f)));

            NegativeWeight = Config.Bind("Smart", "NegativeWeight", 80f,
                new ConfigDescription("引导评分中护符站上负等级格子的额外惩罚（0=关闭）",
                    new AcceptableValueRange<float>(0f, 2000f)));

            CriteriaMoveChance = Config.Bind("Smart", "CriteriaMoveChance", 0.15f,
                new ConfigDescription("退火中“受限护符定向跳转到满足条件格子”的移动概率（0=关闭）",
                    new AcceptableValueRange<float>(0f, 1f)));

            PriorityEnable = Config.Bind("Priority", "Enable", true,
                new ConfigDescription(
                    "藏品优先级系统：1级(最高)→4级(最低)。默认：传说=1、羁绊(永恒)=1、稀有=2、高级=3、普通=4。" +
                    "高优先级藏品优先满足等级与位置需求，必要时低级藏品被牺牲进负格"));

            PriorityCommon = Config.Bind("Priority", "Common", 4,
                new ConfigDescription("普通(Common)优先级（1最高~4最低）", new AcceptableValueRange<int>(1, 4)));
            PriorityUncommon = Config.Bind("Priority", "Uncommon", 3,
                new ConfigDescription("高级(Uncommon)优先级", new AcceptableValueRange<int>(1, 4)));
            PriorityRare = Config.Bind("Priority", "Rare", 2,
                new ConfigDescription("稀有(Rare)优先级", new AcceptableValueRange<int>(1, 4)));
            PriorityLegend = Config.Bind("Priority", "Legend", 1,
                new ConfigDescription("传说(Legend)优先级", new AcceptableValueRange<int>(1, 4)));
            PriorityEternal = Config.Bind("Priority", "Eternal", 1,
                new ConfigDescription("羁绊/永恒(Eternal)优先级", new AcceptableValueRange<int>(1, 4)));

            PriorityFixedItems = Config.Bind("Priority", "FixedHighPriorityItems",
                "Item_ColdLock_Name,Item_SweepRange_Name,Item_SweepRange_Enhanced_Name,Item_MiniBossFight_Name,Item_FrostRelicStack_Name",
                "强制最高优先级(1级)的特定藏品 LocalizedString key（逗号分隔）。" +
                "默认：冰冷的锁、丢弃的金戒指、绝对戒指、红茶叶袋、冰星（效果重要，即使稀有度不高）");

            ForcedPriorityItems = Config.Bind("Priority", "ForcedPriorityItems",
                "Item_ScytheOfBerut_Name:3,Item_IceHammer_Name:2,Item_FaultfinderNeedle_Name:4",
                "强制指定优先级的藏品（格式 key:优先级，逗号分隔多个；1最高~4最低，覆盖稀有度映射与强制1级配置）。" +
                "默认：贝鲁特之镰降为3级（其效果依赖暴击溢出，权重不高）；暴风雪之锤升为2级；故障探测针降为4级（普通）");

            IgnoreCellPreferredItems = Config.Bind("Priority", "IgnoreCellPreferredItems",
                "Item_ColdLock_Name",
                "优先利用豁免格（IgnoreCriteria 解锁石板解除位置限制）的藏品 LocalizedString key（逗号分隔）。" +
                "默认：冰冷的锁——只要背包里有解锁石板就尽可能站豁免格（可上高等级格），其次才考虑其自然限制位置。" +
                "其他有位置限制的物品则相反：先考虑自然满足位置，不行再用豁免格");

            PriorityWeight1 = Config.Bind("Priority", "Weight1", 1.5f,
                new ConfigDescription("1级藏品等级分权重", new AcceptableValueRange<float>(0.5f, 3f)));
            PriorityWeight2 = Config.Bind("Priority", "Weight2", 1.25f,
                new ConfigDescription("2级藏品等级分权重", new AcceptableValueRange<float>(0.5f, 3f)));
            PriorityWeight3 = Config.Bind("Priority", "Weight3", 1.1f,
                new ConfigDescription("3级藏品等级分权重", new AcceptableValueRange<float>(0.5f, 3f)));
            PriorityWeight4 = Config.Bind("Priority", "Weight4", 1.0f,
                new ConfigDescription("4级藏品等级分权重", new AcceptableValueRange<float>(0.5f, 3f)));

            CompassTargetForcedHigh = Config.Bind("Priority", "CompassTargetForcedHigh", true,
                "被北向的金色针（指北针 Charm_UpCharmDamage）锁定的目标神器强制最高优先级(1级)：" +
                "无论稀有度，优先拉满等级。整理前已经配对的针才生效");

            PriorityLowValueItems = Config.Bind("Priority", "LowLevelValueItems",
                "Item_ShadowEye_Name,Item_PlateArmor_Name",
                "等级价值低的藏品 LocalizedString key（逗号分隔，支持护符类名如 Charm_ShadowEye）：" +
                "这些藏品升级收益极低，等级分按 LowValueLevelFactor 打折，且优先级降为4级（最晚选格）。" +
                "默认：闪烁的眼睛、蜥蜴板甲（只保证启用，不追求高等级）");

            LowValueLevelFactor = Config.Bind("Priority", "LowLevelValueFactor", 0.1f,
                new ConfigDescription("低等级价值藏品的等级分保留比例（0=等级完全无价值，仍保证启用）",
                    new AcceptableValueRange<float>(0f, 1f)));

            PriorityMinLevelItems = Config.Bind("Priority", "MinLevelItems",
                "Item_SuperPlanet_Name=2",
                "必须优先达到指定最低等级的藏品（格式 key=等级，逗号分隔多个）。" +
                "命中后强制最高优先级(1级)，启用时有效等级低于目标每级额外扣分，保证优先拉满该等级。" +
                "默认：谱子「银河」=2（Level2 效果关键）");

            PlanetBonus = Config.Bind("Synergy", "PlanetBonus", 40000f,
                new ConfigDescription("行星望远镜(Charm_PlanetModule)启用时，周围八格每颗启用行星藏品的加成奖励（0=关闭）。" +
                    "该值代表“行星聚拢到望远镜旁”的权重：应明显高于行星自身单级等级分(10000)才会让搜索优先聚拢",
                    new AcceptableValueRange<float>(0f, 200000f)));

            PlanetClusterExcludedItems = Config.Bind("Synergy", "PlanetClusterExcludedItems",
                "Item_SuperPlanet_Name,Item_FlamePlanet_Name",
                "虽是行星分类但不参与望远镜聚簇的藏品 LocalizedString key（逗号分隔）。" +
                "默认：乐谱银河（谱子「银河」）、红色行星观察日志——它们是行星但不需放在望远镜周围");

            HarmonyCrystalItems = Config.Bind("Synergy", "HarmonyCrystalItems",
                "Item_NearLevelDamage_Name",
                "和谐之晶类藏品 LocalizedString key（逗号分隔）：周围8格内神器每1级伤害放大+1%——" +
                "整理会尽量把高等级护符聚到它周围8格（须启用才生效）");

            HarmonyLevelBonus = Config.Bind("Synergy", "HarmonyLevelBonus", 2000f,
                new ConfigDescription("和谐之晶周围8格每级护符有效等级的评分奖励（0=关闭）",
                    new AcceptableValueRange<float>(0f, 50000f)));

            DedicationBadgeItems = Config.Bind("Synergy", "DedicationBadgeItems",
                "Item_CompanionChaos_Name",
                "奉献徽章类藏品 LocalizedString key（逗号分隔；默认类识别已覆盖奉献徽章）。" +
                "效果：加成同一横排的同伴藏品");

            DedicationCompanionBonus = Config.Bind("Synergy", "DedicationCompanionBonus", 3000f,
                new ConfigDescription("奉献徽章同一横排内每个同伴藏品的评分奖励（0=关闭）。" +
                    "同伴按 ICompanionCharm 接口自动识别（金色手铃/迷你弩炮/灵魂粉末×3/采矿臂章等）",
                    new AcceptableValueRange<float>(0f, 50000f)));

            HourglassItems = Config.Bind("Synergy", "HourglassItems",
                "",
                "发光的沙漏类藏品 LocalizedString key（逗号分隔；默认类识别已覆盖 Charm_RightSpellCooldownHelper）。" +
                "效果：使右边一格的魔法书（Charm_Magic）CD 恢复速度 +30/60/100%（按沙漏等级）");

            HourglassBonus = Config.Bind("Synergy", "HourglassBonus", 6000f,
                new ConfigDescription("沙漏右边有魔法书时，按魔法书 CD 秒数的评分奖励（0=关闭）。" +
                    "CD 越长奖励越高 → 搜索会把沙漏放到 CD 最长的魔法书左边",
                    new AcceptableValueRange<float>(0f, 50000f)));

            RayShardItems = Config.Bind("Synergy", "RayShardItems",
                "Item_DoubleMagic_Name",
                "雷伊星碎片类藏品 LocalizedString key（逗号分隔，也支持类名 token）。" +
                "效果：放在耗蓝量最高的魔法书右侧（与沙漏方向相反）");

            RayShardBonus = Config.Bind("Synergy", "RayShardBonus", 4000f,
                new ConfigDescription("雷伊星碎片左侧有魔法书时，按魔法书耗蓝量的评分奖励（0=关闭）。" +
                    "耗蓝越高奖励越高 → 搜索会把碎片放到耗蓝最高的魔法书右侧",
                    new AcceptableValueRange<float>(0f, 50000f)));

            EclipseItems = Config.Bind("Synergy", "EclipseItems",
                "",
                "永恒蚀类藏品 LocalizedString key（逗号分隔；默认类识别已覆盖 Charm_FireIceWeapon）。" +
                "摆位规则：背包中冰霜武具(FROST)藏品多于太阳剑(FLAMESWORD)时放右三列，少于时放左三列，相等/皆无则不限");

            OpposingScaleItems = Config.Bind("Synergy", "OpposingScaleItems",
                "",
                "对立之秤类藏品 LocalizedString key（逗号分隔；默认类识别已覆盖 Charm_FireIce）。" +
                "摆位规则：冰川(GLACIER)藏品多于余烬(EMBER)时放最右列，少于时放最左列，相等/皆无时只能最左或最右列");

            WhitePaperComboBonus = Config.Bind("Synergy", "WhitePaperComboBonus", 5000f,
                new ConfigDescription("白纸夹在两件同连击神器中间时的补位评分权重（0=关闭）。" +
                    "优先当前数量最大但尚未达到最高效果档位的连击；例如坚固 9/10 时优先用白纸补到 10",
                    new AcceptableValueRange<float>(0f, 100000f)));

            CompassBonus = Config.Bind("Synergy", "CompassBonus", 12000f,
                new ConfigDescription("指北针(Charm_UpCharmDamage)维持整理前原目标时的评分奖励（0=不额外加分，原目标绑定仍始终生效）。" +
                    "整理前已配对的针会锁定同一个物品实例并随它移动；只有原先未配对的针才自动寻找伤害类藏品/指北针",
                    new AcceptableValueRange<float>(0f, 50000f)));

            CompassUnpairedFactor = Config.Bind("Synergy", "CompassUnpairedFactor", 0.1f,
                new ConfigDescription("指北针未配对（上方无伤害类/指北针）时其等级分的保留比例。" +
                    "游戏里指北针效果只在配对时生效，未配对等级分应视为虚分（0.1=保留一成）",
                    new AcceptableValueRange<float>(0f, 1f)));

            BeltItems = Config.Bind("Synergy", "BeltItems",
                "Item_Belt_Name",
                "多用途腰带类藏品 LocalizedString key（逗号分隔；类识别已覆盖 Charm_WoodenBox，木箱也会命中）。" +
                "效果：背包最上行(y=0)每有一件神器，效果叠加一次——启用后整理会尽量把神器堆满第一行");

            BeltRowBonus = Config.Bind("Synergy", "BeltRowBonus", 2500f,
                new ConfigDescription("腰带启用时，第一行每件神器的评分奖励（0=关闭）",
                    new AcceptableValueRange<float>(0f, 50000f)));

            BurdenPenalty = Config.Bind("Burden", "NegativeCellPenalty", 20000f,
                new ConfigDescription("负面藏品未待在负等级格子时的扣分（强制塞负格；0=关闭）",
                    new AcceptableValueRange<float>(0f, 100000f)));

            BurdenItemKeys = Config.Bind("Burden", "ItemKeys", "Item_MindBurden_Name",
                "负面藏品识别：LocalizedString key（逗号分隔多个）。识别到的物品会被塞进背包最差的（负等级）格子。默认心之重担(Item_MindBurden_Name)");

            MysticEnable = Config.Bind("Mystic", "Enable", true,
                "神秘标签联动：神秘藏品≥2个时 1 个神秘地块等级×2，≥5个时共 4 个地块×2（ComboEffect_Mystic）。" +
                "启用后插件会优先把高价值护符放到×2地块上");

            MysticCategory = Config.Bind("Mystic", "Category", "Mystic",
                "神秘标签名（游戏内分类 key，本地化显示为“神秘”）");

            MysticMultiplier = Config.Bind("Mystic", "Multiplier", 2f,
                new ConfigDescription("神秘地块等级倍率", new AcceptableValueRange<float>(1f, 10f)));

            SelfTest = Config.Bind("Debug", "SelfTest", false,
                "自检模式（仅主机可用）：按热键时先把背包随机打乱再整理，对比前后评分，用于验证优化器是否有效");

            sorter = new InventorySorter(this);
            harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} 已加载。" +
                        $"按 [{Hotkey.Value}] 整理背包（当前模式: {Mode.Value}）");
        }

        /// <summary>每帧轮询逻辑（热键检测/会话诊断/自动整理）。两个 BepInEx 版本均由 MonoBehaviour Update 消息调用。</summary>
        private void Tick()
        {
            if (sorter == null)
            {
                return;
            }

            // 后台搜索完成后，只在 Unity 主线程执行背包交换与结果校验。
            sorter.Poll();
            if (sorter.Busy)
            {
                return;
            }

            // 会话状态诊断（仅在状态变化时记录一次）
            bool sessionActive = NetworkClient.active && NetworkClient.localPlayer != null;
            if (sessionActive != lastSessionActive)
            {
                lastSessionActive = sessionActive;
                if (!sessionActive)
                {
                    sorter.ResetSessionClock(); // 退出会话：重置背包初始化计时
                    ManualPriorityManager.Clear();
                }
                Log.LogInfo($"会话状态变化: NetworkClient.active={NetworkClient.active}, " +
                            $"localPlayer={(NetworkClient.localPlayer != null ? "有" : "无")}");
            }

            if (IsHotkeyDown(Hotkey.Value))
            {
                sorter.Sort();
                return;
            }

            // 可选：进入会话（本地玩家背包就绪）后自动整理一次
            if (AutoSortOnSessionStart.Value)
            {
                if (NetworkClient.active && NetworkClient.localPlayer != null)
                {
                    if (!autoSortedThisSession)
                    {
                        var avatar = NetworkClient.localPlayer.GetComponent<PlayerAvatar>();
                        if (avatar != null && avatar.Inventory != null && avatar.Inventory.charms.Count > 0)
                        {
                            autoSortedThisSession = true;
                            Log.LogInfo("检测到会话开始且背包有护符，自动整理…");
                            sorter.Sort();
                        }
                    }
                }
                else
                {
                    autoSortedThisSession = false;
                }
            }
        }

        private void Update()
        {
            Tick();
        }

        /// <summary>
        /// 热键检测：游戏使用新输入系统(InputSystem)，旧版 UnityEngine.Input 会失效，
        /// 因此优先用 Keyboard.current，失败时回退旧输入。
        /// </summary>
        private static bool IsHotkeyDown(KeyboardShortcut ks)
        {
            KeyCode mainKey = ks.MainKey;
            if (mainKey == KeyCode.None)
            {
                return false;
            }

            bool pressed;
            var kb = Keyboard.current;
            if (kb != null)
            {
                Key? key = KeyCodeToInputSystemKey(mainKey);
                if (key.HasValue)
                {
                    pressed = kb[key.Value].wasPressedThisFrame;
                    if (pressed && ks.Modifiers != null)
                    {
                        foreach (var mod in ks.Modifiers)
                        {
                            Key? mk = KeyCodeToInputSystemKey(mod);
                            if (mk.HasValue && !kb[mk.Value].isPressed)
                            {
                                return false;
                            }
                        }
                    }
                    return pressed;
                }
            }

            // 回退：旧输入系统
            if (Input.GetKeyDown(mainKey))
            {
                return true;
            }

            return false;
        }

        private static Key? KeyCodeToInputSystemKey(KeyCode kc)
        {
            switch (kc)
            {
                case KeyCode.LeftControl: return Key.LeftCtrl;
                case KeyCode.RightControl: return Key.RightCtrl;
                case KeyCode.LeftAlt: return Key.LeftAlt;
                case KeyCode.RightAlt: return Key.RightAlt;
                case KeyCode.LeftCommand: return Key.LeftCommand;
                case KeyCode.RightCommand: return Key.RightCommand;
                case KeyCode.Return: return Key.Enter;
                case KeyCode.None: return null;
            }

            try
            {
                return (Key)Enum.Parse(typeof(Key), kc.ToString(), ignoreCase: true);
            }
            catch
            {
                return null;
            }
        }

        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
            harmony = null;
            ManualPriorityManager.Clear();
            Instance = null;
        }

        internal bool IsSorting => sorter != null && sorter.Busy;
    }

    internal static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.sephiria.backpack-organizer";
        public const string PLUGIN_NAME = "Sephiria Backpack Organizer";
        public const string PLUGIN_VERSION = "2.5.1";
    }
}
