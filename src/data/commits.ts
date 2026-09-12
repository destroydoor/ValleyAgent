import type { GitCommit, Stage } from '@/types';

function parseCommitType(subject: string): GitCommit['type'] {
  const lower = subject.toLowerCase();
  if (lower.startsWith('feat') || lower.startsWith('feat(')) return 'feat';
  if (lower.startsWith('fix') || lower.startsWith('fix(')) return 'fix';
  if (lower.startsWith('refactor') || lower.startsWith('refactor(')) return 'refactor';
  if (lower.startsWith('test') || lower.startsWith('test(')) return 'test';
  if (lower.startsWith('docs') || lower.startsWith('docs(')) return 'docs';
  if (lower.startsWith('chore') || lower.startsWith('chore(')) return 'chore';
  if (lower.startsWith('merge') || lower.includes('merge:')) return 'merge';
  return 'other';
}

function isMergeCommit(subject: string): boolean {
  return subject.toLowerCase().startsWith('merge');
}

import rawCommits from '../../git_history.json';

export const commits: GitCommit[] = (rawCommits as Array<{
  hash: string;
  short: string;
  date: string;
  subject: string;
  body: string;
}>).map((c) => ({
  ...c,
  type: parseCommitType(c.subject),
  isMerge: isMergeCommit(c.subject),
}));

export const stages: Stage[] = [
  {
    id: 'phase0',
    name: 'Phase 0: 基线',
    description: 'ValleyAgent v2.0.0 基线版本，项目起点',
    startIndex: 0,
    endIndex: 2,
    commitCount: 3,
    keyCommits: ['0f9c8ec'],
    color: '#95a5a6',
  },
  {
    id: 'phase1',
    name: 'Phase 1: 基础设施 + 对话系统',
    description: '对话系统集成、寻路修复、Python Server 解耦、协议升级',
    startIndex: 3,
    endIndex: 29,
    commitCount: 27,
    keyCommits: ['1876919', '5bc5e79', 'd048fdb'],
    color: '#27ae60',
  },
  {
    id: 'phase2',
    name: 'Phase 2: 叙事测试 + V4 重写',
    description: 'N1~N5 叙事测试、MockLLMProvider、MiniMax 集成、V4 全面重写',
    startIndex: 30,
    endIndex: 44,
    commitCount: 15,
    keyCommits: ['df0eebb', 'a52bd6f', '589180d'],
    color: '#4a90d2',
  },
  {
    id: 'phase3',
    name: 'Phase 3: LangChain + MemPalace',
    description: 'LangChain/LlamaIndex 重写、ChromaDB→MemPalace、架构测试',
    startIndex: 45,
    endIndex: 51,
    commitCount: 7,
    keyCommits: ['cc9f392', 'f9c26fe', '731e36e'],
    color: '#9b59b6',
  },
  {
    id: 'phase4',
    name: 'Phase 4: Vanilla Release',
    description: 'NPC 回归日程位置、两阶段 vanilla release、跨地图跟随修复',
    startIndex: 52,
    endIndex: 55,
    commitCount: 4,
    keyCommits: ['176697f', 'ef9c909'],
    color: '#f39c12',
  },
  {
    id: 'phase5',
    name: 'Phase 5: 游戏测试修复',
    description: 'WebSocket/好感度/送礼/战斗/测试全面修复',
    startIndex: 56,
    endIndex: 61,
    commitCount: 6,
    keyCommits: ['3f40b4e', 'd774f32', 'b653c89'],
    color: '#e74c3c',
  },
  {
    id: 'phase6',
    name: 'Phase 6: V5 提示词 + 记忆系统',
    description: 'v5 第一人称提示词、SignificantMemory、remember/forget 工具',
    startIndex: 62,
    endIndex: 62,
    commitCount: 1,
    keyCommits: ['ed8a1a6', '404fd3c'],
    color: '#1abc9c',
  },
  {
    id: 'phase7',
    name: 'Phase 7: NPC 自主行为优化',
    description: '移除 THINKING 状态、GOAP 设计原则、好感度阈值统一、话题生成',
    startIndex: 63,
    endIndex: 67,
    commitCount: 5,
    keyCommits: ['2a1ea44', 'c11e827', '0efdcff'],
    color: '#3498db',
  },
  {
    id: 'phase8',
    name: 'Phase 8: 命令模式重构',
    description: '18+ 种命令分解到独立命令类、CA Error 全面修复',
    startIndex: 68,
    endIndex: 70,
    commitCount: 3,
    keyCommits: ['b779f89', '3b83501'],
    color: '#e67e22',
  },
  {
    id: 'phase9',
    name: 'Phase 9: 最终修复',
    description: 'P0/P1 Bug 修复、API 层重复守卫移除、LM Studio 兼容性',
    startIndex: 71,
    endIndex: 90,
    commitCount: 20,
    keyCommits: ['4662d15', '4d09950', '26e7020'],
    color: '#c0392b',
  },
];

export const typeColors: Record<GitCommit['type'], string> = {
  feat: '#27ae60',
  fix: '#e74c3c',
  refactor: '#9b59b6',
  test: '#f39c12',
  docs: '#3498db',
  merge: '#daa520',
  chore: '#95a5a6',
  other: '#7f8c8d',
};

export const typeLabels: Record<GitCommit['type'], string> = {
  feat: '功能',
  fix: '修复',
  refactor: '重构',
  test: '测试',
  docs: '文档',
  merge: '合并',
  chore: '杂项',
  other: '其他',
};
