import { GitBranch, GitCommit, Calendar, Users } from 'lucide-react';

export default function Header() {
  return (
    <header
      className="flex items-center justify-between border-b-2 px-6 py-4"
      style={{
        backgroundColor: '#2a2018',
        borderColor: '#8b6914',
        color: '#f0e6d3',
      }}
    >
      <div className="flex items-center gap-3">
        <div
          className="flex h-10 w-10 items-center justify-center rounded-lg border-2"
          style={{ borderColor: '#daa520', backgroundColor: '#1a1510' }}
        >
          <GitBranch size={20} style={{ color: '#daa520' }} />
        </div>
        <div>
          <h1 className="text-xl font-bold" style={{ color: '#daa520' }}>
            ValleyTalk
          </h1>
          <p className="text-xs opacity-60">Git 提交历史可视化</p>
        </div>
      </div>

      <div className="flex items-center gap-6 text-sm">
        <div className="flex items-center gap-2">
          <GitCommit size={14} style={{ color: '#daa520' }} />
          <span>
            <span className="font-bold" style={{ color: '#daa520' }}>91</span>{' '}
            提交
          </span>
        </div>
        <div className="flex items-center gap-2">
          <GitBranch size={14} style={{ color: '#daa520' }} />
          <span>
            <span className="font-bold" style={{ color: '#daa520' }}>7</span>{' '}
            分支
          </span>
        </div>
        <div className="flex items-center gap-2">
          <Calendar size={14} style={{ color: '#daa520' }} />
          <span>2026-05-02 ~ 2026-05-31</span>
        </div>
        <div className="flex items-center gap-2">
          <Users size={14} style={{ color: '#daa520' }} />
          <span>1 作者</span>
        </div>
      </div>
    </header>
  );
}
