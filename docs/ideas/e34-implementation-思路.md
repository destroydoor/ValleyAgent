# E3-4a 实施思路：只读 NPC 背包展示界面（UI 外壳）

> 依据：`docs/plan/2026-08-02-execution-plan.md` 的 E3-4 条目（需求文档 §1.8 交易流程）
> 范围：**仅 UI 外壳 + 展示模式抽象 + 可单测的"不能拿走"守卫**。
> 还价钩子（E3-4b，接 HaggleStateMachine）、菜单打开入口、右键交易菜单（E3-3）均不在本轮。
> 本文件是 E3-4a 的独立实施思路，与 `e31-implementation-思路.md`（数据层）同风格。

---

## 1. 背景与目标

执行计划 E3-4 原文：

> **改动**：复用箱子/背包界面做只读展示（能看、能点选进入谈价、不能拖走）；预留"展示模式"抽象
> 供未来偷窃功能复用。
> **验收**：无法从界面直接拿走任何物品；点选物品正确进入还价流程。

E3-1 已经交付 NPC 钱包 + 12 格背包数据层（`AgentInventory.GetAllItems()` 返回 `Item?[]` 快照）。
E3-4a 是交易流程的**只读展示侧**：玩家能看到 NPC 背包装了什么、点选一个物品"谈价"，
但绝不能从这个界面把 NPC 的东西拿走（那是未来偷窃玩法，或 E3-4b 谈价成交后由 C# 规则侧转移）。

三个"必须"：

1. **复用原版界面** —— 执行计划明确"复用箱子/背包界面"，不自绘菜单、不造新交互轮子。
2. **拿走行为必须被规则侧锁死** —— 验收第一条就是"无法从界面直接拿走任何物品"，
   这条守卫要能脱离游戏单测，而不是只靠肉眼在游戏里验证。
3. **给偷窃留好抽象** —— "展示模式"接口在本轮定义好形状，未来 `StealableDisplayMode`
   用同一个菜单 + 同一套策略类，只是 `CanTakeItems` 翻转。

## 2. 为什么复用 ItemGrabMenu，以及本版本的坑

### 2.1 为什么不是自绘菜单

仓库里已有一个自绘的 `AgentInventoryMenu`（`UI/AgentInventoryMenu.cs`，纯展示无交互）——
但 E3-4 要求"复用箱子/背包界面"。`ItemGrabMenu` 是原版箱子的权威实现，自带：
格子布局与绘制、物品 hover 提示、玩家背包栏、右上关闭按钮、键盘退出、物品高亮（暗色表示不可拿）。
复用它能拿到全部原版体验，且未来 E3-3 交易菜单/E3-4b 还价都在同一交互心智模型里。

### 2.2 本版本 ItemGrabMenu 的公开 API（反射实测，SDV 1.6.x）

```
大构造器（行为参数都在这里）：
ItemGrabMenu(
  IList<Item> inventory,
  bool reverseGrab,
  bool showReceivingMenu,
  InventoryMenu.highlightThisItem highlightFunction,   // NPC 格子的高亮谓词（纯视觉）
  behaviorOnItemSelect behaviorOnItemSelectFunction,   // 玩家背包"收下"回调（本菜单传 null）
  string message,                                       // 菜单标题
  behaviorOnItemSelect behaviorOnItemGrab = null,      // 从 NPC 格拿走时的回调（本菜单传 null）
  bool snapToBottom = false,
  bool canBeExitedWithKey = true,
  bool playRightClickSound = true,
  bool allowRightClick = true,
  bool showOrganizeButton = true,                       // 整理按钮 → 必须关
  int source = 0,                                       // source_none=0（不建箱子专属按钮）
  Item sourceItem = null,
  int whichSpecialButton = -1,
  object context = null,
  ItemExitBehavior heldItemExitBehavior = ...,
  bool allowExitWithHeldItem = true)
```

**关键坑（本版本没有"只读开关"）**：反射确认本版本 ItemGrabMenu **没有**
`canGrabItem` / `canGrabFromPlayerInventory` 字段，也**没有** `grabItemFromChest` /
`grabItemFromInventory` 方法（旧版本有，可覆写或置 false；本版本拿取逻辑全部内联在
`receiveLeftClick` / `receiveRightClick` 里）。IL 实测 `receiveLeftClick` 内部就是
`behaviorOnItemSelect.Invoke(...)` + `Farmer.addItemToInventoryBool(...)`。

> 结论：**只读只能靠覆写 `receiveLeftClick` / `receiveRightClick` 拦截两个格子区域**，
> 改字段/覆写 grab 方法的路子在本版本不存在。这是本设计最核心的决策。

另外 `InventoryMenu.leftClick` IL 实测**不检查** `highlightMethod` —— 高亮纯粹是视觉
（不可拿物品画暗色），不能当守卫用；真正的守卫是点击拦截。这条也写死在设计里，防止后人
误以为"把 highlight 传 false 就只读了"。

## 3. 只读三层防线

```
第 1 层（真正的守卫）：覆写 receiveLeftClick / receiveRightClick
  ├─ NPC 物品格子命中（ItemsToGrabMenu.isWithinBounds）
  │    → 按策略：FireSelection=true 时调用 _displayMode.OnItemSelected(item)
  │    → 策略 AllowTransfer=false（只读）→ 吞掉点击，绝不进入 base（base 会尝试拿走）
  │    → 策略 AllowTransfer=true（未来偷窃）→ 放行交给 base 执行真正的拿走
  ├─ 玩家背包格子命中（inventory.isWithinBounds）
  │    → 任何展示模式下都吞掉：不允许把玩家物品塞进 NPC 背包
  └─ 其余区域（关闭按钮等）→ 交给 base

第 2 层（视觉）：NPC 格子 highlightFunction = _ => false
  原版把不可拿取的物品画成暗色，玩家一眼看出"这不是我的"。
  注意：这只是视觉，不是守卫（见 §2.2）。

第 3 层（按钮消灭）：构造后防御性置空 organizeButton / fillStacksButton /
  colorPickerToggleButton / specialButton
  防止任何"整理排序 / 补满堆叠 / 调色"路径原地改写 NPC 背包 —— 即使某版本无视
  showOrganizeButton=false / source=source_none 也造了按钮，置空后点击无对象可点。
```

辅助事实（IL 实测，已排除风险）：

- `receiveGamePadButton` 只做"整理玩家自己物品"（`organizeItemsInList(Farmer.get_Items)`）
  与玩家物品相关路径，**不碰 NPC 格子**；且 organizeButton 已被置空。
- `receiveKeyPress` 的 Delete 只作用于 `heldItem`；两个格子都被拦截后 `heldItem` 恒为 null，
  删除路径自然失效。
- `behaviorOnItemGrab` / `behaviorOnItemSelectFunction` 传 null，双保险：即使未来某处
  漏了拦截走到 base，也没有回调把物品塞给玩家。

## 4. 展示模式抽象（偷窃复用预留）

```csharp
/// 展示模式：决定 NPC 背包界面对玩家交互的响应。
public interface IInventoryDisplayMode
{
    /// <summary>是否允许玩家直接拿走物品。ReadOnly=false；未来 Stealable=true。</summary>
    bool CanTakeItems { get; }

    /// <summary>玩家点选 NPC 物品时回调（E3-4b 将接到 HaggleStateMachine 进入还价）。</summary>
    void OnItemSelected(Item item);
}

/// 只读模式（本轮唯一实现）。
public sealed class ReadOnlyDisplayMode : IInventoryDisplayMode
{
    private readonly Action<Item>? _onItemSelected;
    public ReadOnlyDisplayMode(Action<Item>? onItemSelected = null) => _onItemSelected = onItemSelected;
    public bool CanTakeItems => false;
    public void OnItemSelected(Item item) => _onItemSelected?.Invoke(item);
}
```

E3-4b 的接线方式（本轮不接，只定形状）：

```csharp
new ReadOnlyInventoryMenu(
    agent.Inventory.GetAllItems(),
    $"{agent.Name} 的背包",
    new ReadOnlyDisplayMode(item => haggleFlow.Start(item)));   // ← E3-4b 把回调接到 HaggleStateMachine
```

未来偷窃：`StealableDisplayMode : IInventoryDisplayMode { CanTakeItems => true; ... }`，
同一菜单 + 同一策略类，`CanTakeItems` 翻转即可复用整个界面与守卫管线。

## 5. 可单测的纯逻辑：InventoryDisplayInteraction

**为什么要抽纯逻辑**：`ItemGrabMenu` 构造时 `IClickableMenu` 基类会用
`Game1.mouseCursors` 纹理创建右上关闭按钮 —— 脱离游戏运行时无法实例化菜单。
而验收第一条"无法拿走任何物品"是规则级承诺，必须可单测。
于是把"一次点击该不该放行/该不该触发选中"的裁决抽成**纯静态类**，菜单运行时调用它：

```csharp
public static class InventoryDisplayInteraction
{
    public enum Target { NpcItems, PlayerInventory }

    public readonly record struct Decision(bool AllowTransfer, bool FireSelection);

    public static Decision Decide(IInventoryDisplayMode mode, Target target)
    {
        if (target == Target.NpcItems)
        {
            return mode.CanTakeItems
                ? new Decision(AllowTransfer: true,  FireSelection: false)  // 偷窃：直接拿走
                : new Decision(AllowTransfer: false, FireSelection: true);  // 只读：不转移，进谈价
        }

        // 玩家自己的背包：任何模式下都禁止塞进 NPC 背包
        return new Decision(AllowTransfer: false, FireSelection: false);
    }
}
```

语义表：

| 模式 | 目标 | AllowTransfer | FireSelection | 行为 |
|---|---|---|---|---|
| ReadOnly | NPC 物品 | ✗ | ✓ | 点选 → OnItemSelected（还价钩子）；拿不走 |
| ReadOnly | 玩家背包 | ✗ | ✗ | 点击被吞，物品不动 |
| Stealable（未来） | NPC 物品 | ✓ | ✗ | 放行给 base 执行真正的拿走 |
| Stealable（未来） | 玩家背包 | ✗ | ✗ | 不给不塞 |

菜单的 `HandleReadOnlyClick` 就是调用这个策略并按 `AllowTransfer` 决定是否把点击交给 base
（`return !decision.AllowTransfer`）。**被测代码 == 上线代码**，守卫不是测试里的玩具。

## 6. 类清单

| 文件 | 内容 |
|---|---|
| `src/ValleyAgent/UI/IInventoryDisplayMode.cs` | 展示模式接口（CanTakeItems + OnItemSelected） |
| `src/ValleyAgent/UI/ReadOnlyDisplayMode.cs` | 只读模式实现（回调转发，可空安全） |
| `src/ValleyAgent/UI/InventoryDisplayInteraction.cs` | 纯策略（§5），可单测的"不能拿走"守卫 |
| `src/ValleyAgent/UI/ReadOnlyInventoryMenu.cs` | `sealed class : ItemGrabMenu`，三层防线（§3） |
| `src/ValleyAgent.UnitTests/ReadOnlyInventoryMenuTests.cs` | 单测（§7） |

## 7. 测试计划（TDD）

`ReadOnlyInventoryMenuTests.cs` 覆盖：

| 测试 | 验证点 |
|---|---|
| `ReadOnlyDisplayMode_CanTakeItems_IsFalse` | 只读模式永远不允许拿走（验收第一条的规则侧） |
| `ReadOnlyDisplayMode_OnItemSelected_ForwardsToCallback` | 点选回调转发（验收第二条的钩子形状） |
| `ReadOnlyDisplayMode_NoCallback_DoesNotThrow` | E3-4a 未接线还价时菜单不崩（可空回调） |
| `Decide_ReadOnly_NpcItemClick_NoTransfer_FiresSelection` | 核心守卫：只读 + NPC 物品 → 禁止转移 + 触发选中 |
| `Decide_ReadOnly_PlayerInventoryClick_NoTransfer_NoSelection` | 只读 + 玩家背包 → 全吞 |
| `Decide_Stealable_NpcItemClick_AllowsTransfer_NoSelection` | 未来偷窃抽象：CanTakeItems=true → 放行转移、不触发选中 |
| `Decide_Stealable_PlayerInventoryClick_NoTransfer` | 偷窃也不允许"塞给"NPC |
| `ReadOnlyInventoryMenu_Contract_ExtendsItemGrabMenu_WithExpectedCtor` | 反射断言：菜单继承 ItemGrabMenu、公开构造器形状 (IList\<Item\>, string, IInventoryDisplayMode) |
| `IInventoryDisplayMode_Contract_Shape` | 反射断言：接口含 `bool CanTakeItems` + `void OnItemSelected(Item)`，防签名漂移（镜像钱包测试风格） |

**无头可测性依据**：`StardewValley.Item` 基类构造 IL 实测只做 `Object..ctor` + 字段赋值，
不碰 `Game1`；测试用 `FakeItem : Item`（覆写 7 个抽象成员：`drawInMenu` / `DisplayName` /
`TypeDefinitionId` / `getDescription` / `isPlaceable` / `maximumStackSize` / `GetOneNew`）
即可脱离游戏实例化。菜单类本身不实例化（需要 XNA 纹理），只做类型级反射断言。

## 8. 与 E3-4b 的接线点（本轮不接）

- `OnItemSelected(Item)` 回调 → E3-4b 接 `HaggleStateMachine`：点选物品 → 进入该物品的还价流程。
- 菜单打开入口（玩家在什么操作下 new 这个菜单）→ E3-4b 与 E3-3 的右键"送礼/交易/取消"菜单
  一起决定；本轮不新增任何触发入口。
- 验收"点选物品正确进入还价流程"在本轮只验证到"回调被触发且携带正确 Item 引用"，
  还价流程本身是 E3-4b。

## 9. 本轮不做

- 还价钩子 / HaggleStateMachine / pending offer（E3-4b；成交原子校验在 E3-3）。
- 菜单打开入口、调试命令、TestMod 场景（E3-4b 一并加）。
- 游戏内完整验收（阶段 6 手动/TestMod）：本轮单测 + 编译门禁先行。
- 不碰 `Economy/` 目录（E3-2/E3-3 范围）；不碰 TS/ValleyAI 仓库；不 push。

## 10. 验证门槛

1. `dotnet build src\ValleyAgent\ValleyAgent.csproj -c Debug -p:GamePath="<REPO_ROOT>\Stardew Valley" --no-incremental --verbosity minimal`
   → 0 warning 0 error。
2. `dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj -c Debug -p:GamePath="<REPO_ROOT>\Stardew Valley" --no-build --verbosity minimal`
   → 新测试全绿 + 既有测试不回归（零跳过零失败）。
3. 提交后 `git checkout -- "Stardew Valley/Mods"` 还原构建部署残留，只 stage 源码。
