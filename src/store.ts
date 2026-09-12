import { create } from 'zustand';
import type { GitCommit, Stage } from '@/types';

interface AppState {
  selectedCommit: GitCommit | null;
  setSelectedCommit: (commit: GitCommit | null) => void;
  activeStage: Stage | null;
  setActiveStage: (stage: Stage | null) => void;
  hoveredCommit: GitCommit | null;
  setHoveredCommit: (commit: GitCommit | null) => void;
}

export const useStore = create<AppState>((set) => ({
  selectedCommit: null,
  setSelectedCommit: (commit) => set({ selectedCommit: commit }),
  activeStage: null,
  setActiveStage: (stage) => set({ activeStage: stage }),
  hoveredCommit: null,
  setHoveredCommit: (commit) => set({ hoveredCommit: commit }),
}));
