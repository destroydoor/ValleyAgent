import { stages } from '@/data/commits';
import { useStore } from '@/store';
import { GitBranch, GitCommit, Layers } from 'lucide-react';

export default function StagePanel() {
  const { activeStage, setActiveStage } = useStore();

  return (
    <div
      className="sticky top-4 h-fit rounded-lg border-2 p-4"
      style={{
        backgroundColor: '#2a2018',
        borderColor: '#8b6914',
        color: '#f0e6d3',
      }}
    >
      <div className="mb-4 flex items-center gap-2">
        <Layers size={18} style={{ color: '#daa520' }} />
        <h2 className="text-lg font-bold" style={{ color: '#daa520' }}>
          开发阶段
        </h2>
      </div>

      <div className="space-y-2">
        {stages.map((stage) => {
          const isActive = activeStage?.id === stage.id;
          return (
            <div
              key={stage.id}
              className="cursor-pointer rounded p-3 transition-all hover:brightness-110"
              style={{
                backgroundColor: isActive ? stage.color + '30' : '#1a1510',
                borderLeft: `4px solid ${stage.color}`,
                borderTop: `1px solid ${isActive ? stage.color : '#3a2a1a'}`,
                borderRight: `1px solid ${isActive ? stage.color : '#3a2a1a'}`,
                borderBottom: `1px solid ${isActive ? stage.color : '#3a2a1a'}`,
              }}
              onClick={() => setActiveStage(isActive ? null : stage)}
            >
              <div className="mb-1 flex items-center justify-between">
                <span className="text-sm font-bold" style={{ color: stage.color }}>
                  {stage.name}
                </span>
                <span className="text-xs opacity-60">{stage.commitCount} 提交</span>
              </div>
              <p className="mb-2 text-xs leading-relaxed opacity-80">
                {stage.description}
              </p>
              <div className="flex flex-wrap gap-1">
                {stage.keyCommits.map((hash) => (
                  <span
                    key={hash}
                    className="rounded px-1.5 py-0.5 text-xs font-mono"
                    style={{ backgroundColor: '#3a2a1a', color: '#c4a86b' }}
                  >
                    {hash}
                  </span>
                ))}
              </div>
            </div>
          );
        })}
      </div>

      <div
        className="mt-4 rounded border p-3 text-xs"
        style={{ backgroundColor: '#1a1510', borderColor: '#3a2a1a' }}
      >
        <div className="mb-2 flex items-center gap-2" style={{ color: '#daa520' }}>
          <GitBranch size={14} />
          <span className="font-bold">分支统计</span>
        </div>
        <div className="space-y-1 opacity-80">
          <div className="flex justify-between">
            <span>dev</span>
            <span style={{ color: '#4a90d2' }}>活跃</span>
          </div>
          <div className="flex justify-between">
            <span>master</span>
            <span style={{ color: '#95a5a6' }}>基线</span>
          </div>
          <div className="flex justify-between">
            <span>feat/*</span>
            <span style={{ color: '#27ae60' }}>2 条</span>
          </div>
          <div className="flex justify-between">
            <span>fix/*</span>
            <span style={{ color: '#e74c3c' }}>2 条</span>
          </div>
          <div className="flex justify-between">
            <span>refactor/*</span>
            <span style={{ color: '#9b59b6' }}>2 条</span>
          </div>
        </div>
      </div>
    </div>
  );
}
