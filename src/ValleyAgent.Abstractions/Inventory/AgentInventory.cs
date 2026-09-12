using System;
using System.Linq;
using StardewValley;
using SObject = StardewValley.Object;

namespace ValleyAgent.Inventory
{
    /// <summary>
    /// 钱包变动事件参数（E3-1 经济数据层）。
    /// 镜像 ItemChangeRecord 的契约风格；Amount 为带符号增量（收入 +n / 支出 -n），
    /// NewBalance 是事件发生后的余额（回执），供留痕与 LLM 认知同步使用。
    /// </summary>
    public sealed record OnWalletChangedEventArgs(
        string NpcName,
        string GameDate,
        int Amount,
        int NewBalance,
        string? Reason);

    public class AgentInventory
    {
        public const int MaxSlots = 12;
        private readonly Item?[] _slots = new Item[MaxSlots];
        private int _money;
        private readonly object _lock = new();

        /// <summary>
        /// 背包变动事件（E1-1 预留契约，Phase 3 经济系统触发）。
        /// 当前无订阅者——保证 Phase 3 不需改动 inventory 表面。
        /// </summary>
#pragma warning disable CS0067 // 事件当前无订阅者，Phase 3 经济系统接入后将有引用
        public event Action<ItemChangeRecord>? OnItemChanged;
#pragma warning restore CS0067

        /// <summary>
        /// 钱包变动事件（E3-1）。AddMoney / TrySpend / TrySpendWithChange 触发，
        /// 在锁内按变更顺序发布，订阅者异常被隔离不影响钱包原子操作。
        /// TranscriptSink.AttachWallet 订阅后写 JSONL 留痕。
        /// </summary>
        public event Action<OnWalletChangedEventArgs>? OnWalletChanged;

        /// <summary>
        /// 钱包事件上下文用 NPC 名（由 AgentService 分配 Agent 时写入）。
        /// 物品/钱包事件记录需要归属，但 inventory 本身不知道所属 NPC。
        /// </summary>
        public string NpcName { get; set; } = "";

        /// <summary>
        /// 当前钱包余额。直接赋值是静默路径（存档恢复 / 档案初始资金回填），不触发 OnWalletChanged；
        /// 正常收支一律走 AddMoney / TrySpend，保证留痕完整。线程安全。
        /// </summary>
        public int Money
        {
            get { lock (_lock) { return _money; } }
            set { lock (_lock) { _money = value; } }
        }

        /// <summary>Number of occupied slots.</summary>
        public int Count { get { lock (_lock) { return _slots.Count(s => s != null); } } }

        /// <summary>Number of free slots.</summary>
        public int FreeSlots { get { lock (_lock) { return _slots.Count(s => s == null); } } }

        /// <summary>Whether the inventory is completely full.</summary>
        public bool IsFull { get { lock (_lock) { return _slots.All(s => s != null); } } }

        /// <summary>
        /// Attempts to add an item to the inventory.
        /// Will stack with existing items of the same type if possible.
        /// Thread-safe. 原子（2026-08-17 审计 P1#3）：容量不足时整批零副作用拒绝——
        /// 旧实现先合并堆叠再找空槽，失败时部分堆叠已入栈且传入 item 的 Stack 被吞，
        /// 执行器回滚不覆盖本步，导致 NPC 背包净多物品而回执报失败（镜像/账本漂移）。
        /// </summary>
        public bool TryAdd(Item item)
        {
            if (item == null)
            {
                return false;
            }

            lock (_lock)
            {
                // 阶段一：预检可吸收总量（可堆叠槽余量 + 空槽容量），不触碰任何槽位。
                var capacity = 0;
                for (var i = 0; i < MaxSlots; i++)
                {
                    if (_slots[i] is SObject existing && existing.canStackWith(item))
                    {
                        capacity += Math.Max(0, existing.maximumStackSize() - existing.Stack);
                    }
                    else if (_slots[i] == null)
                    {
                        capacity += item.maximumStackSize();
                    }
                }

                if (capacity < item.Stack)
                {
                    return false;
                }

                // 阶段二：预检通过才提交（先堆叠后空槽，与旧逻辑一致；容量已保证不会中途失败）。
                for (var i = 0; i < MaxSlots; i++)
                {
                    if (_slots[i] is SObject existing && existing.canStackWith(item))
                    {
                        var remaining = existing.addToStack(item);
                        if (remaining <= 0)
                        {
                            return true;
                        }

                        item.Stack = remaining;
                    }
                }

                for (var i = 0; i < MaxSlots; i++)
                {
                    if (_slots[i] == null)
                    {
                        _slots[i] = item;
                        return true;
                    }
                }

                // 预检已保证容量足够，理论不可达；兜底返回失败而非静默部分成功。
                return false;
            }
        }

        /// <summary>
        /// Attempts to remove an item from the inventory. Thread-safe.
        /// </summary>
        public bool TryRemove(string itemId, int count, out Item? removed)
        {
            removed = null;
            if (string.IsNullOrEmpty(itemId) || count <= 0)
            {
                return false;
            }

            lock (_lock)
            {
                for (var i = 0; i < MaxSlots; i++)
                {
                    if (_slots[i] is SObject obj &&
                        (obj.QualifiedItemId == itemId || obj.ItemId == itemId))
                    {
                        var take = Math.Min(count, obj.Stack);
                        removed = obj.getOne();
                        removed.Stack = take;

                        obj.Stack -= take;
                        if (obj.Stack <= 0)
                        {
                            _slots[i] = null;
                        }

                        return true;
                    }
                }

                return false;
            }
        }

        public bool RemoveItem(string itemId, int count) => TryRemove(itemId, count, out _);

        /// <summary>Gets a snapshot of all items currently in the inventory. Thread-safe.</summary>
        public Item?[] GetAllItems()
        {
            lock (_lock) { return _slots.ToArray(); }
        }

        /// <summary>Checks if the inventory contains an item with the given ID. Thread-safe.</summary>
        public bool Contains(string itemId)
        {
            lock (_lock)
            {
                return _slots.Any(s => s is SObject obj &&
                    (obj.QualifiedItemId == itemId || obj.ItemId == itemId));
            }
        }

        /// <summary>Clears all items from the inventory. Thread-safe.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                for (var i = 0; i < MaxSlots; i++)
                {
                    _slots[i] = null;
                }
            }
        }

        // ───────────────────────── 钱包（E3-1）─────────────────────────

        /// <summary>
        /// 原子加钱。amount 必须 &gt; 0，否则抛 ArgumentOutOfRangeException（调用方传 0/负数属于 bug）。
        /// 触发 OnWalletChanged（Amount 为正增量），返回加钱后的余额。线程安全。
        /// </summary>
        public int AddMoney(int amount, string? reason = null)
        {
            if (amount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), "amount must be positive");
            }

            lock (_lock)
            {
                _money += amount;
                RaiseWalletChanged(new OnWalletChangedEventArgs(NpcName, "", amount, _money, reason));
                return _money;
            }
        }

        /// <summary>
        /// 原子尝试花费。余额不足返回 false（不变更、不触发事件）；
        /// amount ≤ 0 同样返回 false。成功时触发 OnWalletChanged（Amount 为负增量）。线程安全。
        /// </summary>
        public bool TrySpend(int amount, string? reason = null)
        {
            if (amount <= 0)
            {
                return false;
            }

            lock (_lock)
            {
                if (_money < amount)
                {
                    return false;
                }

                _money -= amount;
                RaiseWalletChanged(new OnWalletChangedEventArgs(NpcName, "", -amount, _money, reason));
                return true;
            }
        }

        /// <summary>
        /// 原子尝试花费并返回"找零"（花费后剩余余额）到 change。
        /// 余额不足或 amount ≤ 0 返回 false（change 置 0，零副作用）。成功时触发 OnWalletChanged。线程安全。
        /// </summary>
        public bool TrySpendWithChange(int amount, out int change, string? reason = null)
        {
            change = 0;
            if (amount <= 0)
            {
                return false;
            }

            lock (_lock)
            {
                if (_money < amount)
                {
                    return false;
                }

                _money -= amount;
                change = _money;
                RaiseWalletChanged(new OnWalletChangedEventArgs(NpcName, "", -amount, _money, reason));
                return true;
            }
        }

        /// <summary>
        /// 在锁内发布钱包变动事件；订阅者异常被吞掉（.editorconfig 已关 CA1031）。
        /// 设计哲学 #8：留痕/观察者失败绝不能影响游戏状态或钱包原子性。
        /// </summary>
        private void RaiseWalletChanged(OnWalletChangedEventArgs args)
        {
            try
            {
                OnWalletChanged?.Invoke(args);
            }
            catch (Exception)
            {
                // 订阅者（如 TranscriptSink 写盘）失败不阻断钱包操作
            }
        }
    }
}
