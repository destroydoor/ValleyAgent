import { useStore } from '@/store';
import { typeColors, typeLabels } from '@/data/commits';
import { X, GitCommit, Calendar, User, FileText } from 'lucide-react';

export default function CommitModal() {
  const { selectedCommit, setSelectedCommit } = useStore();

  if (!selectedCommit) return null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center p-4"
      style={{ backgroundColor: 'rgba(26, 21, 16, 0.85)' }}
      onClick={() => setSelectedCommit(null)}
    >
      <div
        className="relative w-full max-w-2xl rounded-lg border-2 p-6 shadow-2xl"
        style={{
          backgroundColor: '#f5deb3',
          borderColor: '#8b6914',
          color: '#3a2a1a',
        }}
        onClick={(e) => e.stopPropagation()}
      >
        <button
          onClick={() => setSelectedCommit(null)}
          className="absolute right-4 top-4 rounded p-1 transition-colors hover:bg-black/10"
        >
          <X size={20} />
        </button>

        <div className="mb-4 flex items-center gap-3">
          <div
            className="flex h-10 w-10 items-center justify-center rounded-full"
            style={{ backgroundColor: typeColors[selectedCommit.type] }}
          >
            <GitCommit size={18} color="white" />
          </div>
          <div>
            <span
              className="rounded px-2 py-0.5 text-xs font-bold text-white"
              style={{ backgroundColor: typeColors[selectedCommit.type] }}
            >
              {typeLabels[selectedCommit.type]}
            </span>
            {selectedCommit.isMerge && (
              <span
                className="ml-2 rounded px-2 py-0.5 text-xs font-bold text-white"
                style={{ backgroundColor: '#daa520' }}
              >
                合并
              </span>
            )}
          </div>
        </div>

        <h2 className="mb-4 text-xl font-bold" style={{ color: '#5a3a1a' }}>
          {selectedCommit.subject}
        </h2>

        <div className="mb-4 space-y-2 text-sm">
          <div className="flex items-center gap-2">
            <GitCommit size={14} style={{ color: '#8b6914' }} />
            <span className="font-mono text-xs">{selectedCommit.hash}</span>
          </div>
          <div className="flex items-center gap-2">
            <Calendar size={14} style={{ color: '#8b6914' }} />
            <span>{selectedCommit.date}</span>
          </div>
          <div className="flex items-center gap-2">
            <User size={14} style={{ color: '#8b6914' }} />
            <span>ValleyTalk Dev</span>
          </div>
        </div>

        {selectedCommit.body && (
          <div
            className="rounded border p-3"
            style={{ backgroundColor: '#faf0d8', borderColor: '#c4a86b' }}
          >
            <div className="mb-1 flex items-center gap-2">
              <FileText size={14} style={{ color: '#8b6914' }} />
              <span className="text-xs font-bold" style={{ color: '#8b6914' }}>
                详细说明
              </span>
            </div>
            <pre className="whitespace-pre-wrap font-mono text-xs leading-relaxed" style={{ color: '#5a3a1a' }}>
              {selectedCommit.body}
            </pre>
          </div>
        )}
      </div>
    </div>
  );
}
