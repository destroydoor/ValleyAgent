import Header from '@/components/Header';
import Timeline from '@/components/Timeline';
import StagePanel from '@/components/StagePanel';
import Legend from '@/components/Legend';
import CommitModal from '@/components/CommitModal';

export default function Home() {
  return (
    <div className="flex h-screen flex-col" style={{ backgroundColor: '#1a1510' }}>
      <Header />

      <div className="flex flex-1 overflow-hidden">
        {/* Left: Timeline */}
        <div className="flex flex-1 flex-col">
          <div className="px-4 pt-3">
            <Legend />
          </div>
          <div className="flex-1 overflow-hidden px-4 pb-4">
            <Timeline />
          </div>
        </div>

        {/* Right: Stage Panel */}
        <div
          className="w-80 overflow-y-auto border-l-2 p-4"
          style={{ borderColor: '#3a2a1a' }}
        >
          <StagePanel />
        </div>
      </div>

      <CommitModal />
    </div>
  );
}
