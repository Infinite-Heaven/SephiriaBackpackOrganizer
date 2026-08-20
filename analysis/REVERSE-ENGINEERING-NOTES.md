# 逆向分析笔记（Assembly-CSharp, v0.10.x / Unity 6000.3.21f1 Mono）

本笔记记录从游戏程序集反编译得到的、与本插件相关的机制，供后续维护参考。

## 物品数据模型

- `NewItemOwnInstance`（背包中的物品实例）：`InstanceID / Quantity / XIdx / YIdx / EntityID / Charm / StoneTablet`
- `ItemEntity`（ScriptableObject 物品定义）：`id / type(EItemType) / rarity(EItemRarity) / categories / cost` 等
- `EItemType`：`Misc, ThrowingWeapon, Potion, Food, Scroll, Charm, StoneTablet, Identifiable`
- `EItemRarity`：`Common, Uncommon, Rare, Legend, Eternal`
- `ItemDatabase`（静态）：`FindItemById / GetAllItemID / MaxStackCount` 等；物品资源在 `Resources/Item`、分类在 `Resources/ItemCategory`

## 背包容器 GridInventory（Mirror NetworkBehaviour）

- 主数据结构：`SyncDictionary<ItemPosition, NewItemOwnInstance> inventoryMatrix`（public readonly）
- 索引：`SyncDictionary<ItemPosition, Charm_Basic> charms`、`SyncDictionary<ItemPosition, StoneTablet> stoneTablets`（均 public readonly）
- 格子：`Width=6, Height=7(最大), CurrentInventoryStorage`；`IdxToPos(int)` / `PosToIdx(x,y)` 行列主序
- 等级矩阵：`SyncDictionary<ItemPosition,int> levelMatrix` —— 护符 `DisplayedLevel = levelMatrix[自身位置]`
- 权限：`public class GridInventory.Permission : IDisposable`（ctor 调私有 GetPermission，Dispose 调私有 ReleasePermission，
  作用：移除/重放全部石板效果、刷新护符生效状态与套装效果）。**插件用 `using (new GridInventory.Permission(inv))` 包裹布局写入**。

## 神器等级加成机制（核心）

- 每块 `StoneTablet` 有 `conditionQuery`（生效条件）与 `query`（效果模板），按自身 `(xIdx,yIdx,rotation)` 解析，
  效果类型：`IncreaseConstLevel / Disable / IgnoreCriteria / MultiplyConstLevel`
- `ApplyEffect()`（[Server]）：把效果写入 `levelMatrix` 等；`RemoveEffect()` 撤销 —— 都在 Permission 周期内执行
- 护符 `Charm_Basic.DisplayedLevel = levelMatrix[格位]`，`IsEffectEnabled` 由 `CharmActivateCriteria`（位置/邻接条件）决定
- 游戏内置优化：`GridInventory.AutoArrangeInventoryForBestCharmLevels(maxIterations=4, allowTabletRotation=true)`
  （[Server]，爬山：每次迭代尝试全部旋转 + 全部两两交换，取最优，评分 `EvaluateCurrentAutoArrangeScore()`）
- 客户端入口：`RequestAutoArrangeInventoryForBestCharmLevels(...)` → Mirror Command（`requiresAuthority: true`）
- **全游戏没有任何 UI 调用该自动排列** —— 属于未暴露的隐藏功能

## 评分函数 EvaluateCurrentAutoArrangeScore()

```
对每个护符 c：
  displayed = levelMatrix[c.pos]
  clamped = clamp(displayed, 0, c.maxLevel)
  IsEffectEnabled ? (enabledCount++, enabledLevelSum += clamped) : (disabledCount++)
  displayed < 0 → negativeSum += -displayed；displayed > maxLevel → overflowSum += ...
score = enabledLevelSum*10000 + enabledCount*1000 + Σdisplayed*10 + overflowSum - disabledCount*750 - negativeSum*250
```

## 布局写入（插件复刻游戏私有 ApplyAutoArrangeState）

1. 清空 `inventoryMatrix / charms / stoneTablets` 全键
2. 按新顺序重建：`inventoryMatrix[pos] = new NewItemOwnInstance(...)`，
   并同步 `charm.NetworkxIdx/yIdx`、`tablet.NetworkxIdx/yIdx/Networkrotation`
3. 全程包在 `GridInventory.Permission` 内，保证效果矩阵正确重放

## 本地玩家获取

`NetworkClient.localPlayer.GetComponent<PlayerAvatar>()`（游戏内 SteamRichPresence 同款写法），`PlayerAvatar.Inventory` 即主背包。

## 输入系统

- 游戏**只使用新版 Input System**（`UnityEngine.InputSystem`：`Keyboard.current / Mouse.current`）
- 旧版 `UnityEngine.Input.GetKeyDown` 在新输入系统下失效 → **插件热键用 `Keyboard.current[key].wasPressedThisFrame` 检测**，
  KeyCode→Key 需特殊映射（LeftControl→LeftCtrl 等），并保留旧输入回退

## 其它

- 官方模组钩子 `HorayModAPI`（开发中）：`OnAllDatabasesReady / GridInventoryStartPermission` 等事件；
  其中 `NotifyStartSession` 当前版本无任何调用点，故插件用轮询检测会话开始
- `ArrangementBonusEnabled()` 返回 false —— "布局加成"系统当前版本被禁用，与本插件无关

## v1.1 追加：负向石板 / 位置限制 / 解除限制机制
- **负向石板**：`StoneTablet.AdditionEffectData.levelParam` 带符号（int），负值即减等级；
  效果解析 `StoneTablet.ParseQuery(query, w, h, storage, originPos, rotation, out humanReadable)`
  为公开静态方法，`new StoneTablet.AdditionEffectData(AdditionMetadata)` 构造公开 —— 插件可离线精确预计算。
  越界格子（x<0 / y<0 / x>=w / y>=h）在游戏中写入 levelMatrix 后无任何物品可读，**天然无害**。
- **位置条件**：`Charm_Basic.criteria`（public）指向 `CharmActivateCriteria` 子类，其 `GetCriteria(charm)` 决定
  `RefreshCharm` 是否启用效果；`ReleasePermission()` 会为每个护符调用 `RefreshCharm()` ——
  因此**游戏权威评分 EvaluateCurrentAutoArrangeScore 已实时反映位置条件与负等级**。
  位置型子类：`TopInInventory`(y==0)、`BottomInInventory`(PosToIdx>=storage-6)、`SideEnd`(x==0||x==5)、
  `Inside`、`Outlined`、`BothSidesAreEmpty`、`BothSideCharm`、`NeighborsAreFull`、`Near8MagicBook`、`FullHP`(非位置型)。
  ⚠️ 注意 `SideEnd.IsActivePosition` 有游戏 bug（写 `pos.y == 5`），GetCriteria 才是权威；插件按 GetCriteria 语义预判。
- **解除限制**：石板 `IgnoreCriteria` 效果写入 `ignoreCriteriaMatrix`（SyncDictionary），
  `RefreshCharm` 中 `flag2 = ignoreCriteriaMatrix[格] > 0` 时跳过条件判定 —— 豁免格上的受限护符无视位置条件生效。
- **生效条件**：石板 `conditionQuery`（AnyItem/OnlyCharm/Placed）不满足时整块石板效果不生效，离线模型需复刻该判定。
- **智能排序（v1.1 算法）**：
  1. `BuildSmartStart`：石板贪心（`SteleImportance` 负效果多者优先；`EvaluateStelePlacement` 正覆盖+10/级、
     负暴露-160/格、豁免格+25）；受限护符优先（`KindPriority`）摆到满足格或豁免格；其余护符按等级增量选格。
  2. `GuideScore` = 游戏评分 + 条件满足±150 + 负格暴露-80×|delta|（权重可配）。
  3. 退火邻域增加定向移动 `TryCriteriaMove`（受限护符跳到随机满足格，15% 概率）。
  4. 安全兜底：最终按游戏评分与原始布局比较，绝不更差。

## v2.0 追加：行星望远镜 / 指北针 / 附魔 / 负面藏品 / 稀有度

- **行星望远镜** = `Charm_PlanetModule`（zh-CN: `Item_PlanetModule_Name`=巨型望远镜）：
  `OnCharmEffectRefreshed` 时若自身 `IsEffectEnabled`，遍历周围八格，对 `Entity.categories.Contains("PLANET")`
  的物品（`Charm_SummonGreenBat` 系列行星）执行 `SetEnhancement(true)` —— 真实战斗增强，不反映在 levelMatrix/游戏评分。
  行星类：`Charm_SummonGreenBat : Charm_Basic, IAttackableCharm`（行星是伤害类！）、`Charm_FlamePlanet`（非攻击）、`Charm_SummonRedPlanet`。
- **指北针** = `Charm_UpCharmDamage`（zh-CN: `Item_UpCharmDamage_FlavorText`=罗盘）：`yOffset=-1`，
  `RefreshCharm` 时在上方格 `(x, y-1)` 注册 `CharmDependency`；`OnRequestCharmDamageBonus(rootCharm)` 在
  `IsDependencyValid`（上方是 `Charm_UpCharmDamage` 或 `IAttackableCharm`）时给上方藏品伤害加成——
  **不检查 IsEffectEnabled**：指北针即使在负等级格上（自身 disabled）依然加成上方藏品，可链式叠加。
  伤害加成聚合 `RequestCharmDamageBonusOnRoot` 同样不检查启用。
- **附魔等级**：`DungeonManager.Instance.GetGlobalItemStatValue(instanceID, "Enchant")`（public，返回 string）。
  GetPermission 时 `levelMatrix[格] -= 附魔`、ReleasePermission 时 `+= 附魔` —— 附魔随物品走，计入格位等级。
- **负面藏品**：心之重担 = `Item_MindBurden_Name`（zh-CN 本地化；`HardModeShard_MindBurden` 困难模式挑战物，
  "没有任何能力且无法丢弃"）。运行时按 `ItemEntity.aName.key`（`LocalizedString.key` 公开字段）匹配识别。
- **石板位置条件**：旗帜=`Item_StoneTablet_Flag_Name`、遮阳=`Item_StoneTablet_Shade_Name`；
  条件经 `conditionQuery` 边界 token（`LEFT/UP/X_MINUS/HORIZONTAL` 等）表达，`StoneTablet.ParseQuery`
  在给定 (origin, rotation) 下展开，条件不满足则整块石板效果不生效（ApplyEffect 的
  `list.Count != 0 && (!flag || (!flag3 && flag2))` 判定）。
- **稀有度**：`EItemRarity` Common=0..Eternal=4；`ItemEntity.rarity` public。
- **v2.0 评分模型**（全离线，性能关键）：`baseLevel = levelMatrix - 当前石板贡献 - 当前物品附魔`；
  候选布局等级 = baseLevel + 石板贡献 + 附魔；护符启用 = 非禁用 && 等级≥0 && 条件满足（位置型预判 +
  布局依赖型按候选布局邻接判定）&& 非武器类。评分 = 等级×10000 + 启用1000 - 禁用750 - 负格250×|lvl| +
  稀有度奖励 + 行星聚簇(40000/颗, 需行星启用) + 罗盘配对(12000, 无需罗盘启用) - 负担未待最低格惩罚。
  搜索多起点（智能初始/原始/随机×2）× 模拟退火 3000×3 全部离线，一次权限周期仅用于最终应用。

## v2.1 追加：神秘标签（×2 地块）

- **神秘分类** = `ItemCategory_Mystic`（本地化"神秘"）；护符标签存于 `ItemEntity.categories`，
  实测字符串为大写 `MYSTIC`（匹配需大小写不敏感）。
- **ComboEffect_Mystic**：`first=2`（≥2 个神秘护符 → 1 个 ×2 地块）、`second=5`（≥5 个 → 共 4 个地块）。
  激活时在 `GridInventory.mysticPositions`（公开 SyncList，10 个随机格，`GenerateServerMysticPositions`
  按 UnitAvatar.RandomID 种子生成一次）前 N 个位置上 `ServerCreateFixedEngravingFromExternalItem` 创建
  **固定附魔石板 ID 12002**（效果 = 地块等级 ×2，MultiplyConstLevel 系，写 multiplyLevelMatrix → levelMatrix）。
  `OnDisableEffect` 移除。组合计数来自 `currentSetEffectCount["Mystic"]`（公开 SyncDictionary，
  `SearchSetEffectInInventory` 按护符 `GetItemCategory()` = entity.categories 统计）。
- **插件利用**：BuildContext 读 `mysticPositions` + `currentSetEffectCount` → 计算 `mysticFactor[]`
  （有效块 = 前 1 或 4 个 ×2）；baseLevel 反推对 ×2 地块**先除倍率**再减（避免双重计算）；
  EvaluateLayout 护符等级 ×factor（行星/罗盘启用判定同步）；智能初始 FindBestCharmCell 对 ×2 地块加权。
  实测（2 神秘护符）：神秘2个/×2地块1格，游戏评分 23042 → 51051（+27009），24-42ms。
- ⚠️ 注意：mysticPositions 仅在神秘组合激活时生成——玩家刚凑齐 2 个神秘藏品时，第一次整理可能
  尚未生成（组合效果在权限周期刷新后触发），第二次 F8 即生效。

## v2.2 追加：如意宝珠（毁坏 +3 等级）

- **如意宝珠** = `Charm_Chintamani`（zh-CN: `Item_Chintamani_Name`；**本身是 MYSTIC 标签**，可帮凑神秘计数）：
  `OnEnabledEffect` 订阅 `NetworkAvatar.OnDamagedServerside`——**必须 IsEffectEnabled（启用）才触发**；
  受到致命伤害时 `HealPercent(50)` + `StartReviveInvulnerable()` + `using Permission { AddDungeonTempLevel(自身格, +3); ForceRemoveItem(自身); }`。
  `AddDungeonTempLevel` = `levelMatrix[格] += 3`（本局临时，dungeonTempLevels 记录；Permission 内调用，ReleasePermission
  的 multiply 应用会把它一并放大 → **在神秘×2地块上毁坏 = 等效 +6**）。
- **插件处理**（`[Treasure]` 配置，默认 `Item_Chintamani_Name`）：
  1. 识别宝珠（按 aName.key，与负担识别并列但行为相反）；
  2. **低优先级"预备+3"道具**：智能初始布局中其他护符→其他物品先占好格子，宝珠最后用剩余位置
     （FindBestCharmCell 复用 ×2 加权 20000，×2 地块有空位优先；没有加成位置放负格亦可）；
  3. 评分：宝珠恰好启用且站在 ×2 地块 → +6000（毁坏收益翻倍奖励）；未启用无惩罚（保命不关键，玩家定位）。
- 实测（2石板+宝珠+负担）：`宝珠1` 识别成功，负担负格✓、25-27ms；
  极端局（30件全宝珠）：宝珠30、神秘30个/×2地块4格，游戏评分 17000→30000、30-33ms。

## v2.4.1 追加：指北针原目标实例绑定

- 捕获整理前每枚已配对指北针正上方物品的 `instanceID`，搜索评分只承认同一目标实例，不再按稀有度或
  优先级改配到其他伤害神器。
- 连续纵向叠放的指北针合并为“目标神器 → 指北针 → 指北针”竖链；搜索移动链首后统一恢复整条链，
  并以硬约束校验最终布局，确保每枚针仍在原目标正下方。
- 整理前未配对的指北针不建立绑定，继续沿用自动寻找伤害类神器/指北针的旧逻辑。

## v2.4.2 追加：白纸连击补位

- **白纸** = `Charm_WhitePaper`，`Order=Post`；每次连击刷新前检查左右各一格，统计两侧神器
  `GetItemCategory()` 的交集。某分类在左右共出现 `match=2` 次时，白纸把该分类加入自己的
  `assignedCategory`，因此会为该连击额外贡献 1 件。
- 连击“上限”按 `ItemCategoryEntity.comboEffectPrefab` 中 `ComboEffectBase.RequestComboData()` 返回的最高
  `comboCount`（兼容 `addStatByCombo` / 旧版 `setStatus`）计算。例如坚固最高档为 10，基础数量 9 时白纸
  放到两件坚固神器中间即可补成 10。
- 排序时先从 `currentSetEffectCount` 扣除白纸当前已复制的分类，得到不依赖白纸的基础数量；候选按
  “基础数量降序、距离上限升序”排列。评分与定向移动共同引导白纸夹入最高优先级连击，并惩罚连击已满后
  继续堆白纸的浪费布局。

## v2.4.3 追加：凯尔萨德尼钥匙自适应周期行

- 游戏类为 `Charm_3Elemental_ByRow`，在 `SearchCategory` 中按 `Item.YIdx % lineCategory.Length`
  选择分类；实际四行顺序为 `STURDY / EMBER / GLACIER / MAGITECH`，即坚固 / 余烬 / 冰川 / 魔法科技。
- 选择前从 `currentSetEffectCount` 扣掉白纸当前复制数与钥匙自身的原分类，再以其他神器
  `GetItemCategory()` 的实际数量兜底，因此依据的是“不靠钥匙自己”的已有最多羁绊。
- 搜索约束从“保持原行”改为“保持目标行余数”：例如坚固可以在第 1/5/9 行中选择等级与其他协同最优的格子。

## v2.5.0 追加：藏品优先级微调（金色针锁定 / 低价值 / 最低等级 / 腰带第一行）

本地化 key（zh-CN.json 实测）：
- 北向的金色针 = `Item_UpCharmDamage_Name`（类 `Charm_UpCharmDamage`，即指北针/罗盘；蓝针为
  `Item_UpCharmDamage_Uncommon_Name`，同类不同稀有度）。
- 闪烁的眼睛 = `Item_ShadowEye_Name`（类 `Charm_ShadowEye`）。
- 蜥蜴板甲 = `Item_PlateArmor_Name`。
- 故障探测针 = `Item_FaultfinderNeedle_Name`（类名未含 Faultfinder，按 key 识别）。
- 多用途腰带 = `Item_Belt_Name`（类 `Charm_WoodenBox`，与木箱 `Item_WoodenBox_Name` 同类）。
- 谱子「银河」= `Item_SuperPlanet_Name`。

机制：
1. **金色针锁定目标强制 P1**：`CaptureCompassBindings` 后把 `compassTargetByInstance` 全部目标
   的 `priority` 置 1（整理前已绑定的针只会指向同一实例）。配置 `Priority.CompassTargetForcedHigh`。
2. **低等级价值物品**：`Priority.LowLevelValueItems`（默认 闪烁的眼睛/蜥蜴板甲/故障探测针）命中后
   `priority=4` 且 `levelScoreFactor=Priority.LowValueLevelFactor`（默认 0.1），等级分大幅打折；
   启用（+1000）与负格惩罚保留，等效“只要等级≥0 能生效即可”。
3. **最低等级目标**：`Priority.MinLevelItems` 格式 `key=等级`（默认 `Item_SuperPlanet_Name=2`），
   命中后强制 P1；启用时 `eff < 目标` 每缺 1 级扣 `10000×PriorityWeight`，保证银河优先到 2 级。
4. **多用途腰带（Charm_WoodenBox）**：游戏 `CheckQuickSlot` 按背包前 6 格（IdxToPos(0..5)，即 y=0
   最上行）统计物品数量，`ApplyEffect(level, count)` 叠加火/雷/冰 AP——腰带自身启用即可，无需占第一行。
   插件在 `EvaluateLayout` 中按启用腰带给“第一行护符数 × BeltRowBonus（默认 2500）”加分，
   `FindBestCharmCell` 对 y=0 格 +6000 引导塞满第一行；配置 `Synergy.BeltItems`/`BeltRowBonus`。
5. 物品匹配统一支持 aName.key 或护符类名（大小写不敏感），见 `MatchesItemKey`。
6. 日志：`LogItemIdentification` 打印全部护符的 `key|类名` 清单，便于向配置填 key。
