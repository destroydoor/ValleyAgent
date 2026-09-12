using System;
using System.Threading;
using StardewValley;

namespace ValleyAgent.Health
{
    public class AgentHealth
    {
        public const int DefaultMaxHealth = 100;

        private int _health;
        private volatile bool _isDead;
        private int _deathDay;  // 0 = 未死亡
        private long _deathTimeTicks; // 0 = 未死亡
        private readonly object _deathLock = new(); // P1-14: 确保 OnDeath 只触发一次

        public event Action<AgentHealth>? OnDeath;

        /// <summary>血量变化时触发（不含致死事件，致死由 OnDeath 触发）。
        /// 参数：(sender, oldHealth, newHealth)</summary>
        public event Action<AgentHealth, int, int>? OnHealthChanged;

        /// <summary>Current health points. Thread-safe read.</summary>
        public int Health => Volatile.Read(ref _health);

        /// <summary>Maximum health points.</summary>
        public int MaxHealth { get; }

        /// <summary>Whether the NPC is currently dead.</summary>
        public bool IsDead => _isDead;

        /// <summary>The in-game day number when the NPC died. Null if alive.</summary>
        public int? DeathDay
        {
            get { var v = Volatile.Read(ref _deathDay); return v == 0 ? null : v; }
        }

        /// <summary>Real-world timestamp of death. Null if alive.</summary>
        public DateTime? DeathTime
        {
            get { var v = Volatile.Read(ref _deathTimeTicks); return v == 0 ? null : new DateTime(v); }
        }

        public AgentHealth(int maxHealth = DefaultMaxHealth)
        {
            MaxHealth = maxHealth;
            _health = maxHealth;
            _isDead = false;
            _deathDay = 0;
            _deathTimeTicks = 0;
        }

        /// <summary>
        /// Deals damage to the NPC. Returns true if the NPC died from this damage.
        /// Thread-safe.
        /// P1-14: 使用锁确保 OnDeath 回调只触发一次，避免多线程竞争。
        /// </summary>
        public bool TakeDamage(int amount)
        {
            if (amount <= 0)
            {
                return false;
            }

            var oldHealth = Volatile.Read(ref _health);
            var newHealth = Interlocked.Add(ref _health, -amount);
            if (newHealth > 0)
            {
                // 未致死：触发 OnHealthChanged（致死路径由 OnDeath 处理，避免双发）
                OnHealthChanged?.Invoke(this, oldHealth, newHealth);
                return false;
            }

            // P1-14: 加锁确保 OnDeath 只触发一次
            lock (_deathLock)
            {
                if (_isDead)
                {
                    return true; // 已有其他线程触发了死亡
                }
                _isDead = true;
            }

            var seasonIndex = Game1.currentSeason?.ToLowerInvariant() switch
            {
                "spring" => 0,
                "summer" => 1,
                "fall" => 2,
                "winter" => 3,
                _ => 0
            };
            Volatile.Write(ref _deathDay, ((Game1.year - 1) * 112) + (seasonIndex * 28) + Game1.dayOfMonth);
            Volatile.Write(ref _deathTimeTicks, DateTime.UtcNow.Ticks);
            OnDeath?.Invoke(this);
            return true;
        }

        /// <summary>
        /// Heals the NPC by the given amount. Thread-safe.
        /// </summary>
        public void Heal(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            int current, target;
            do
            {
                current = Volatile.Read(ref _health);
                target = Math.Min(MaxHealth, current + amount);
                if (target <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _health, target, current) != current);

            // CAS 成功：current 为旧值，target 为新值
            OnHealthChanged?.Invoke(this, current, target);
        }

        /// <summary>
        /// Fully heals the NPC and clears death state. Call at start of new day.
        /// Uses a single atomic flag to prevent readers from seeing partial state.
        /// </summary>
        public void Respawn()
        {
            var oldHealth = Volatile.Read(ref _health);
            Volatile.Write(ref _health, MaxHealth);
            _isDead = false;
            Volatile.Write(ref _deathDay, 0);
            Volatile.Write(ref _deathTimeTicks, 0);

            // 复活是显著血量变化（通常从 0 到 MaxHealth），触发 OnHealthChanged
            if (oldHealth != MaxHealth)
            {
                OnHealthChanged?.Invoke(this, oldHealth, MaxHealth);
            }
        }

        /// <summary>Gets health as a percentage (0.0 to 1.0).</summary>
        public float HealthPercent => MaxHealth > 0 ? (float)Volatile.Read(ref _health) / MaxHealth : 0f;

        /// <summary>Sets health directly (used for save/load).
        /// P1-14: 增加 suppressEvent 参数，存档恢复时传 true 避免误触发 OnDeath 回调。
        /// </summary>
        public void SetHealth(int value, bool suppressEvent = false)
        {
            var oldHealth = Volatile.Read(ref _health);
            var newHealth = Math.Clamp(value, 0, MaxHealth);
            Volatile.Write(ref _health, newHealth);

            if (suppressEvent)
            {
                return;
            }

            if (newHealth <= 0)
            {
                // 致死：触发 OnDeath，不触发 OnHealthChanged（避免双发）
                // P1-14: 加锁确保 OnDeath 只触发一次
                lock (_deathLock)
                {
                    if (_isDead)
                    {
                        return;
                    }
                    _isDead = true;
                }
                OnDeath?.Invoke(this);
                return;
            }

            if (newHealth != oldHealth)
            {
                OnHealthChanged?.Invoke(this, oldHealth, newHealth);
            }
        }
    }
}
