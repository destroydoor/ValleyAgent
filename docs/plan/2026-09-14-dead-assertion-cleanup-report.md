# 死断言清理执行报告（check:test-dead 281 条候选分流）

> **Created:** 2026-09-15
> **执行依据:** `docs/plan/2026-09-14-dead-assertion-cleanup-plan.md`（§2 决策树 / §3 硬约束 / §4 产出物）
> **执行者:** subagent；改动全部留在工作区，未 commit / 未 push
> **改动范围:** `src/ValleyAgent.TestMod/**` 25 个文件 + `scripts/check-dead-assertions.mjs`（仅 allowlist 段）；生产代码零改动

---

## 0. 数字对账（以 2026-09-14 现跑 checker 输出为准，281 条无出入）

| 处置 | 条数 | 说明 |
|---|---:|---|
| **D（删除）** | **21** | 构造性恒真/守卫回声断言，本次从现存测试源码删除（带证据注释） |
| **D（类已删，无需改码）** | **128** | 候选所属 18 个测试类此前已从代码库移除，仅存于 gitignored 历史日志 |
| **D（label 已删，无需改码）** | **9** | 现存类中 9 个 label 此前已被删除（历史日志残留） |
| **S（改 AssertEx 带具体反例）** | **115** | 活链路真实断言，`Assert` → `AssertEx`，反例填具体机理（禁空话） |
| **K（allowlist 保留原样）** | **8** | 心跳/诊断型 `Assert(true)`，登记进 checker ALLOWLIST（含理由注释） |
| **E（验证缺口 backlog）** | **0** | 计划中的唯一 E 示例（IT08 rollback）经现源码复核不成立，见《人工确认清单》 |
| **合计** | **281** | 21+128+9+115+8+0 = 281 ✓ |

**checker 现状（清理后重跑）**：`node scripts/check-dead-assertions.mjs` → 高风险候选 273 条（= 281 − 8 条 allowlist 抑制）。剩余 273 条全部来自 `logs/test_results/` 历史日志（gitignored）：其中 115 条 S 改写后下次游戏内新鲜 IT 运行即携带 Counterexample 字段、自动降级为"有效但稳定"；157 条删除类/删除 label 永不再产出。按计划 §6，归零复核由用户游戏内 IT 重跑后执行。

**门禁结果（2026-09-15）**：
- `dotnet build src/ValleyAgent.TestMod` → **0 警告 0 错误**
- `dotnet test src/ValleyAgent.UnitTests` → **657 通过 / 0 失败 / 0 跳过**
- `node scripts/check-test-anti-cheat.mjs` → **PASS（0 违规）**
- `node scripts/check-dead-assertions.mjs` → allowlist 精确抑制 8 条，退出码仍为 1（历史残留符合预期）

---

## 一、《改动清单》（本次实际改码的 152 条：21 D + 115 S + 8 K + 8 allowlist 登记）

路径相对 `src/ValleyAgent.TestMod/Tests/`。`S` 处置的完整反例文案见各 file:line 源码（下表列机理关键词）。

### D 处置（21 条，删除 + 证据注释）

| # | 断言 label | 证据（file:line） | 一句话证据 |
|---|---|---|---|
| 1 | E3_Unreachable / ResourceClump barriers created | Edge/E3_Unreachable.cs:88 | 紧跟无条件 `resourceClumps.Add` 循环断言 Count>0，构造性恒真（§2 Q2） |
| 2 | E4_FullInventoryFight / Monster was killed | Edge/E4_FullInventoryFight.cs:137 | 断言条件与外层 if 守卫条件逐字相同；`_monsterDied=true` 赋值保留供后续断言使用 |
| 3 | E4_FullInventoryFight / Loot dropped on ground | Edge/E4_FullInventoryFight.cs:151 | 断言条件与外层 if 条件相同（debris 增长才进来再断言增长），恒真；语义由结尾 "Monster loot appeared on ground" 承载 |
| 4 | E4_FullInventoryFight / NPC participated in fight | Edge/E4_FullInventoryFight.cs:175 | Setup 中 `_npc==null` 已 Skip 提前返回，Update 里断言 `_npc!=null` 恒真 |
| 5 | E6_RainFestival / Weather context is set (isRaining=true) | Edge/E6_RainFestival.cs:88 | Setup 无条件写 `Game1.isRaining=true`，tick 30 读回，构造性恒真 |
| 6 | E6_RainFestival / Festival date is set (Spring 13) | Edge/E6_RainFestival.cs:88 | 同上（season/day 同一 Setup 无条件写入） |
| 7 | E6_RainFestival / Decision was made with state | Edge/E6_RainFestival.cs:113 | 走到该行必然 `_decisionMade=true`，而该标志只在 state 非空时置位，恒真 |
| 8 | E8_FullDayCycle / DayEnd cleanup processed without error | Edge/E8_FullDayCycle.cs:194 | 断言对象是 `private readonly bool _noErrors = true` 字面量字段，恒真（字段一并删除） |
| 9 | F2_RandomMonsters / All_spawned_monsters_cleared | Fuzzy/F2_RandomMonsters.cs:269 | Teardown 先手动移除全部怪物、再统计 `finalMonsters` 断言 ==0，恒真 |
| 10 | F7_ScanConsistencyStress / All_locations_tested | Fuzzy/F7_ScanConsistencyStress.cs:237 | 外层 if 守卫即 `_locationIndex >= TestLocations.Length`，断言同条件恒真 |
| 11 | F8_StateDurationBypass / At_least_10_rounds_executed | Fuzzy/F8_StateDurationBypass.cs:122 | 外层 if 守卫即 `_round >= 10`，断言同条件恒真 |
| 12 | Func_MultiplayerSync / AgentStateMessage_TwoAgents | Functional/Func_MultiplayerSync.cs:169 | 对象初始化器写入 2 个 Agent 后立即断言 Count==2（初始化器回声） |
| 13 | Func_MultiplayerSync / AgentStateMessage_HaleyState | Functional/Func_MultiplayerSync.cs:169 | 同上（State/Health 初始化器回声） |
| 14 | Func_MultiplayerSync / DialogueRequestMessage_Fields | Functional/Func_MultiplayerSync.cs:183 | 初始化器回声 |
| 15 | Func_MultiplayerSync / DialogueResponseMessage_Fields | Functional/Func_MultiplayerSync.cs:199 | 初始化器回声 |
| 16 | Func_MultiplayerSync / GiftRequestMessage_Fields | Functional/Func_MultiplayerSync.cs:213 | 初始化器回声 |
| 17 | Func_MultiplayerSync / NpcActionMessage_Fields | Functional/Func_MultiplayerSync.cs:225 | 初始化器回声 |
| 18 | Func_MultiplayerSync / Snapshot_AllFieldsSet | Functional/Func_MultiplayerSync.cs:241 | 初始化器回声；随断言一并删除无主的 snapshot 初始化块，TestAgentStateSnapshotMapping 语义并入注释说明 |
| 19 | Func_MultiplayerSync / Snapshot_DeadAgent | Functional/Func_MultiplayerSync.cs:241 | 同上 |
| 20 | Func_MultiplayerSync / Renderer_InitialStateEmpty | Functional/Func_MultiplayerSync.cs:256 | `AgentRemoteRenderer` 状态为实例字段（`src/ValleyAgent.Abstractions/Multiplayer/AgentRemoteRenderer.cs:20` `_remoteStates = new()`），刚 new 的实例必为空 |
| 21 | IT02 / broadcaster_instance_available | Integration/IT02_StateChanged_ActualStateMirror.cs:94 | Update 入口守卫 `_broadcaster == null → return true` 保证走到断言处必非 null，恒真 |

### S 处置（115 条，Assert → AssertEx + 具体反例）

每条的反例都落到具体变量/机理，无"出错时会失败"类空话。按文件归组（file:line 为改后位置）：

| 文件 | label（行号） | 反例机理关键词 |
|---|---|---|
| Edge/E1_LongPathfind.cs | NPC_stays_in_range (93)、NPC_has_controller_at_some_point (104) | FOLLOW 寻路失控拉大 dist>50 / FOLLOW 从未生成 PathFindController |
| Edge/E2_DoorLoop.cs | NPC.currentLocation not null after warp (134)、Location check (160)、NPC position reset correctly (cycle N) (168，动态 label 覆盖 5 条历史候选)、Completed all cycles (177) | warp 竞态致 currentLocation=null（FOLLOW 跨图缺陷族门口场景）/ WarpTargetGuard 改写落点 dist≥5 / warp 抛异常累计 _errors>0 |
| Edge/E3_Unreachable.cs | NPC currentLocation not null (129)、NPC has not crashed (143) | 寻路失败清理 despawn / 死亡清理链移出 characters |
| Edge/E4_FullInventoryFight.cs | Player inventory stayed at 12 after fight (162)、Monster loot appeared on ground (168) | 战利品被拾取进背包 / FIGHT 未开打（slime 存活且 debris 无增长） |
| Edge/E5_DeathRespawn.cs | NPC is on map (167) | TryRevive 后未重新加入 characters |
| Edge/E8_FullDayCycle.cs | NPC is on map after day cycle (196) | 跨天 DayEnd 移除后未重放置 |
| Edge/E10_IllegalTransitionPermitted.cs | Illegal_transitions_are_blocked_by_state_machine (179) | ForceTransition 绕过 AllowedTransitions（记录在案的历史漏洞面） |
| Edge/E11_FightRadiusUnenforced.cs | Near_monster_detected_as_baseline (156) | ScanEnvironment 扫描链失效返回 NULL |
| Edge/E13_FarmScanLocationCheck.cs | FarmHandler_ScanEnvironment_has_location_check (291)、NPC_did_not_harvest_crops_on_non_farm_location (303)、Farm_scan_and_harvest_baseline_works (312) | 位置守卫被回退 / 同守卫联动 / 农场基线崩塌（扫描 NULL 且未收获） |
| Edge/E14_MineScanLocationCheck.cs | ForceMiningLocation_flag_affects_ScanEnvironment (183)、Mountain_scan_baseline_works (191) | 标志未传达到 ScanEnvironment / 采矿基线崩塌 |
| Fuzzy/F1_RandomWalk.cs | NPC_moved_at_least_once (232)、PathFindController_existed_at_some_point (235)、Stuck_count_less_than_3 (240)、Final_tile_walkable (243) | MoveTo 全失败无位移 / controller 从未创建 / 连续 3 次 stuck / 落在不可通行瓦片 |
| Fuzzy/F2_RandomMonsters.cs | Monster_count_greater_than_zero_at_some_point (261)、Handler_no_crash (266) | 生成循环从未观察到 Monster / fuzz 期间异常被 catch 累计 _hadError |
| Fuzzy/F3_RandomDrops.cs | Inventory_count_never_exceeds_12 (191)、Overflow_items_appeared_on_ground (196)、No_NullReferenceException (200) | TryAdd 容量上限失效 / 溢出物品凭空消失（F3 事故丢失语义）/ FillNpcInventory NRE 被 catch |
| Fuzzy/F5_DualAgent.cs | Neither_NPC_crashes (217) | 双 Agent 并发驱动异常被 catch（crash 标志置位） |
| Fuzzy/F6_SceneSwitch.cs | Scene_change_detected_at_least_3_of_4_transitions (124)、All_changes_correctly_identified (129) | warp 后指纹未变化（SafeWarp 未生效/事件弹回）/ 识别漏检>1 |
| Fuzzy/F7_ScanConsistencyStress.cs | No_NullReferenceException_during_scanning (222) | 跨图压力扫描 NRE 被 RecordScanCrash 累计 |
| Fuzzy/F8_StateDurationBypass.cs | ForceTransition_duration_bypass_blocked (109) | 1 tick 内非法转换成功到达目标态 |
| Focused/F_FollowCrossMap.cs | player_on_busstop (247)、final_same_map (276) | SafeWarp 落点守卫失败或事件拉走玩家 / FOLLOW 跨图跟随断裂滞留中途地图 |
| Focused/F_MineRealCombat.cs | no_event_lingering_after_mine_warp (138) | 180 tick 抑制窗口结束后 Marlon 剧情被重新拉起 |
| Functional/Func_ApiSanitySmoke.cs | At_least_10_API_calls_tested (164) | API 表面收缩或前置 Skip 致 CheckApi 执行数<10 |
| Functional/Func_MultiplayerSync.cs | SinglePlayer_* 5 条 (93-114)、MessageTypes_AgentState (229)、MessageTypes_DialogueRequest (233)、Renderer_* 7 条 (282-339)、FullSync_* 7 条 (361-429) | 模式判定分支写反 / MessageTypes 常量改名即路由断裂（原 Assert(true) 死断言升级为真实相等断言）/ Handle*Message 缓存与队列断链各种机理 / 全量同步清旧写新与字段映射断链 |
| Functional/Func_IllegalTransitionAudit.cs | No_illegal_transition_succeeds (179)、All_legal_transitions_succeed (186)、Full_transition_matrix_covered (193) | ForceTransition 绕过 / 合法对被误拦 / from 态进不去致覆盖缺口 |
| Functional/Func_NpcInventoryFidelity.cs | No_empty_or_null_slot_names (138) | 序列化把空槽写成空串/null（执行镜像与读取视图不一致） |
| Integration/IT01_SetState_HaleyEvent.cs | state_not_IDLE (106) | set_state 动作 no-op |
| Integration/IT02_StateChanged_ActualStateMirror.cs | single_player_guard_active (99) | 测试被放进联机会话运行，单机前提破坏 |
| Integration/IT03_F3_TravelFailureRecovery.cs | on_travel_failed_callback_bound (172)、npc_still_accessible (227) | ServiceInitializer 漏接回调 / 旅行失败清理销毁 NPC 实例 |
| Integration/IT04_F5_EvictionNotification.cs | eviction_triggered (144)、evicted_npc_name_captured (155) | 满池 ForceAllocate 未触发 OnAgentDeallocated / 事件参数缺 NpcName |
| Integration/IT05_TravelCircuitBreaker.cs | travel_blocked_by_circuit_breaker (126)、npc_still_accessible_after_block (143) | 熔断开启仍发起旅行 / 阻止路径错清理 NPC |
| Integration/IT07_G1_ConsecutiveFailureFallback.cs | fallback_love_nonempty (119)、fallback_dislike_nonempty (122)、fallback_love_mentions_item (129)、fallback_dislike_mentions_item (136)、consecutive_failures_state_set (152) | GetLocalGiftFallback 返回 null（G1 事故路径）/ 回退文本模板槽位丢失 / _consecutiveFailures 计数未累计 |
| Integration/IT08_G7_InventoryFullNotification.cs | inventory_full_rejected (143)、rollback_npc_item_restored (149)、no_tulip_in_inventory (169) | 物理校验漏判背包满 / 回滚缺失致 NPC 扣物不回补 / 校验与提交顺序错位先入包 |
| Integration/IT09_ChopTree_ActionExecution.cs | location_available (155)、tree_removed_from_terrain (161) | 执行链把 NPC 移出地图 / GoalExecutor 砍树未生效 |
| Integration/IT10_E6_FollowToIdleNoFriendshipGain.cs | friendship_unchanged (91)、friendship_unchanged_after_second_cycle (100) | 状态转换误发好感度回执（E6 回归面）/ 次轮命中 |
| Integration/IT11_Trade_Settlement.cs | trade_batch_succeeded (146)、trade_steps_count (154)、player_money_increased (159)†、npc_money_decreased (164)†、player_item_decreased (169)、npc_item_increased (174)、receipt_npc_money (179)、idempotent_replay_cached (199)、idempotent_no_double_execution (204)、idempotent_npc_money_stable (209)、insufficient_rejected (231)、insufficient_npc_money_untouched (237)、insufficient_player_item_untouched (242)、insufficient_player_money_untouched (247)、state_still_readable (261) | AdjustExecutor 物理校验误拒/步数缺/资金与物品 op 漏执行或反向/回执余额脱钩/幂等缓存断链二次入账/预校验漏判 INSUFFICIENT/零副作用破坏/Brain 拆除致状态不可读（†两条不在 281 基数内，系顺带补反例） |
| Integration/IT12_SetGoal_ChopTree.cs | goal_created (161)、state_executing_goal (166)、goal_cleared_after_complete (184)、state_left_executing (191)、state_still_readable (200) | set_goal 未建目标 / 未进 EXECUTING_GOAL / FinalizeSuccess 漏清目标 / 收尾 ForceTransition 未执行 / Brain 被回收 |
| Integration/IT13_DirectorTools.cs | money_tool_success (114)、money_changed (117)、mood_tool_success (124)、mood_written (127)、position_tool_success (140)、position_moved (144)、beat_tool_success (156)、beat_active (160)、events_tool_success (171)、events_written (174)、memory_tool_success (186)、memory_written (190)、unknown_tool_rejected (196) | DirectorTools 各工具路由/参数校验/写入断链与"报成功但未落状态"机理，未知工具未拒绝 |

### K 处置（8 条 + checker ALLOWLIST 登记；6 条保留原样，2 条降级为日志）

理由与登记位置：`scripts/check-dead-assertions.mjs:78-119`（ALLOWLIST 段，每条含 reason）。登记前 8 条在 checker 输出中列报，登记后精确抑制。

| # | 断言 label | 保留处 | K 理由 |
|---|---|---|---|
| 1 | E10 / No_crash_during_transition_tests | Edge/E10_IllegalTransitionPermitted.cs:189 | 心跳型 Assert(true)：真崩溃时结果文件根本不会写出，断言无失败语义；真行为断言是 Illegal_transitions_are_blocked… |
| 2 | E11 / Scan_correctly_identifies_monster_count | Edge/E11_FightRadiusUnenforced.cs:163 | 诊断信息型：记录两次扫描原文供人工对照 |
| 3 | E12 / Scan_divergence_documented | Edge/E12_ForageScanBlindIndoor.cs:229 | 诊断信息型：记录室内/室外差异 |
| 4 | E14 / Scan_divergence_documented | Edge/E14_MineScanLocationCheck.cs:195 | 诊断信息型：记录 Farm/Mountain 差异 |
| 5 | F7 / Scan_mismatches_documented_not_failures | Fuzzy/F7_ScanConsistencyStress.cs:231 | 诊断信息型：不匹配是 location guard 设计行为，只记录计数 |
| 6 | F8 / All_transitions_completed_without_crash | Fuzzy/F8_StateDurationBypass.cs:116 | 心跳型：标记 10 轮压力跑到收尾 |
| 7 | Func_IllegalTransitionAudit / Audit_complete_without_crash | Functional/Func_IllegalTransitionAudit.cs:199 | **K' 降级**：Assert(true) 已从代码删除，"审计收尾"信息改为同位置 Monitor.Log 承载（detail 统计文案保留）；历史键由 allowlist 抑制 |
| 8 | Func_NpcInventoryFidelity / Inventory_roundtrip_completed | Functional/Func_NpcInventoryFidelity.cs:190 | **K' 降级**：Assert(true) 已从代码删除，"往返跑到收尾"信息由注释与日志承载；历史键由 allowlist 抑制 |

---

## 二、《删除清单》

### 本次删除的断言（21 条）
见《改动清单》D 处置表。全部为单条断言删除，**未删除任何测试类**（计划 §5 规定类级删除需主会话过目，本次无类级删除；断言删除后各测试类仍保有 ≥1 条真行为断言，无"删完空壳"的类）。 Func_MultiplayerSync.TestAgentStateSnapshotMapping 方法体随断言清空（无主 snapshot 初始化块一并移除），方法保留为空实现占位（相位编排仍调用）。

### 此前已删除、本次仅清理历史候选的（137 条，无需改码）

**18 个测试类整体已从代码库移除（128 条候选）**：
- 证据：`grep -rl <类名> src/` 在生产与测试源码均无命中；`src/ValleyAgent.TestMod/V3TestRunner.cs:727` 注释 "NOTE: Real scenario tests (Real_OneFullDay, Scene_*) were removed because the Tests.Real namespace does not exist."
- 类清单与候选数：
  - Scene_PlayerGift×12、Scene_PlayerDialogue×11、Scene_MineExploration×9、Scene_FarmHarvest×8（40）
  - Real_OneFullDay×7、Real_AutonomousFullDay×5（12）
  - Integration_DecisionFlow×12、Integration_DialogueFlow×8、Integration_GiftFlow×7（27）
  - EmotionPipelineTests×15（该类断言对象 EmotionAnalyzer/DialogueMemoryAnalyzer 属"假 AI"，C# 侧已按 2026-08-15 步骤 3 删除，类移除与架构裁决一致）
  - PromptContractTests×9
  - Visual_ChatMenu×3、Visual_FightSwordSwing×3、Visual_HealthBar×3、Visual_MinePickaxe×3、Visual_VanillaUIReference×7、Visual_HudCards×2、Visual_ChatMenuTransition×2（23）

**现存类中 label 已删（9 条候选）**：均经 grep 确认当前源码无该 label 字符串（非动态插值）：
- IT08 / chatbox_available_for_notification、F6 / DecisionQueue_does_not_repeat_same_scene、IT02 / broadcast_sendmessage_manual_verify、IT06 / action_result_callback_attempted、IT07 / prefix_integration_manual_verify、IT11 / pending_offer_written、IT11 / settlement_succeeded、IT03 / position_recovered、IT04 / chatbox_eviction_message_manual_verify

> 注：`logs/` 历史日志未删（计划 §3.5，归用户决定）。上述 137 条候选在新鲜运行后自然消失。

---

## 三、《人工确认清单》（Q1 拿不准 → 全部按证据裁决，无暂停级悬案）

| 事项 | 两边证据 | 裁决 |
|---|---|---|
| **IT08 rollback 三条是否属 E（计划示例称"回滚路径在测试里从未触发"）** | 计划原文 vs 现源码：`IT08_G7_InventoryFullNotification.cs:55-114` Setup 显式填满玩家背包（MaxItems）、92-149 显式执行 2-op 批并断言 INVENTORY_FULL + 回滚——失败分支**就是测试本体** | **不复核为 E，按 S 处置**。计划示例描述的是旧行为；当前代码每轮运行都在走失败注入路径。据此 E 计数为 0 |
| **Func_MultiplayerSync 消息字段断言（AgentStateMessage_* 等 8 条）归 D 还是 S** | 一边：类注释声称"测试消息序列化"；另一边：断言对象是本方法内对象初始化器刚写入的字段，未经过任何序列化往返（SMAPI 序列化是 SendMessage 内部行为，测试未触达） | **归 D**（§2 Q2 初始化器回声）。真实字段映射语义由同文件 TestAgentRemoteRendererFlow / TestFullSyncMessage 的生产类调用链承载（该部分全部转 S） |
| **E6 天气/日期回读、IT02 broadcaster 实例、E4 NPC participated 等"看似守卫实为恒真"** | 各自 Setup/守卫无条件保证条件成立（详见 D 表证据列） | **归 D** |
| **IT13 spawn_beat/beat_active 是否因旧叙事 Director 砍除而死管道（Q1）** | AGENTS §3.5（2026-09-14 裁决）：旧叙事 director.ts 已砍，但 **C# 9 个 DirectorTools + director_command 通道保留为就绪层**；IT13 测的正是保留的 C# 工具层（DirectorTools/BeatStore 均在容器注册，Setup 缺服务才 Skip） | **活链路，按 S 处置** |
| **第三轮指令：Tests/Visual/*.cs（VIS001-004）"各有约 1 处 RecordRunnerAssertion/Assert 待处置"** | 证据链：① VIS001-008 的 `TestName` 是 `VIS001_DialogueMenuVisual` 等（各文件 :23-28），**不是** `Visual_*`——checker 里的 `Visual_ChatMenu/FightSwordSwing/HealthBar/HudCards/MinePickaxe/VanillaUIReference/ChatMenuTransition` 23 条候选在源码中无对应 TestName，全部属孤儿历史（已列入第二节，不止 Visual_VanillaUIReference 一个）；② VIS 现存的 5 个 Assert label（fight_engaged/npc_has_health/hud_visible/mine_state_set/dialogue_generated）均不在 281 清单（grep 基线输出无命中）；③ `RecordRunnerAssertion` 全仓仅 2 处调用（V3TestRunner.cs:400、Runners/ExperienceTestRunner.cs:356，label 均为 `timeout_as_failure`），也不在 281 清单 | **不动 VIS 文件**。281 候选无一条落在 Tests/Visual/ 现存源码；对非候选断言改写无计划依据（§2 决策树只对 checker 候选逐条走），且会违反"宁可漏杀不可错杀"。已在工作区核验，未产生改动 |

## 四、《验证缺口 backlog》

**本批 E 处置：0 条。** 计划中的 E 示例（IT08 rollback）经复核证伪（见上表），无其他断言命中"测试从未走过失败分支"形态——命中候选全部能写出具体反例机理（转 S）或属构造性恒真（转 D）。

顺带记录两个非断言层面的既有缺口（不属本批范围，仅留痕，未改码）：
1. `logs/test_results/` 历史日志与代码现实脱节（137 条候选的测试已不存在），checker 的"历史基数"会长期偏大——如需干净基线，由用户决定是否清档。
2. IT11 的 `player_money_increased`/`npc_money_decreased` 两条历史上曾有失败记录（不在 281 内），本次顺带补了反例约束，使其在"有效但稳定"分级里更可审计。

---

## 五、复跑与验收指引（主会话）

1. 工作区改动：25 个测试文件 + `scripts/check-dead-assertions.mjs`（ALLOWLIST 段，无条目时行为与旧版一致，向后兼容）。
2. 游戏内（或 Docker）重跑 IT/Edge/Fuzzy/Functional 测试后执行 `node scripts/check-dead-assertions.mjs`：预期高风险候选应**大幅下降**——115 条 S 改写项因新鲜运行携带 Counterexample 转入"有效但稳定"，删除项不再产出；剩余若出现新候选即为新鲜运行暴露的真问题。
3. 记忆更新（计划 §6）：`test-fail-gate.md` 由主会话把"269/281 待分流"更新为"已分流，见本报告"。
