import { useRef, useEffect, useState, useCallback } from 'react';
import { commits, stages, typeColors, typeLabels } from '@/data/commits';
import { useStore } from '@/store';
import { GitCommit, GitMerge } from 'lucide-react';

const NODE_RADIUS = 10;
const NODE_GAP = 60;
const LEFT_MARGIN = 80;
const STAGE_BAR_WIDTH = 6;

export default function Timeline() {
  const containerRef = useRef<HTMLDivElement>(null);
  const { selectedCommit, setSelectedCommit, setHoveredCommit, setActiveStage } = useStore();
  const [visibleRange, setVisibleRange] = useState({ start: 0, end: 30 });

  const totalHeight = commits.length * NODE_GAP + 100;

  const handleScroll = useCallback(() => {
    const container = containerRef.current;
    if (!container) return;
    const scrollTop = container.scrollTop;
    const containerHeight = container.clientHeight;
    const start = Math.max(0, Math.floor((scrollTop - 200) / NODE_GAP));
    const end = Math.min(commits.length, Math.ceil((scrollTop + containerHeight + 200) / NODE_GAP));
    setVisibleRange({ start, end });

    const centerIndex = Math.floor((scrollTop + containerHeight / 2) / NODE_GAP);
    const stage = stages.find(
      (s) => centerIndex >= s.startIndex && centerIndex <= s.endIndex
    );
    setActiveStage(stage || null);
  }, [setActiveStage]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    container.addEventListener('scroll', handleScroll);
    handleScroll();
    return () => container.removeEventListener('scroll', handleScroll);
  }, [handleScroll]);

  const getStageForIndex = (index: number) => {
    return stages.find((s) => index >= s.startIndex && index <= s.endIndex);
  };

  return (
    <div
      ref={containerRef}
      className="relative h-[calc(100vh-80px)] overflow-y-auto"
      style={{ scrollBehavior: 'smooth' }}
    >
      <svg width="100%" height={totalHeight} style={{ minWidth: 600 }}>
        {/* Stage background bars */}
        {stages.map((stage) => {
          const y1 = stage.startIndex * NODE_GAP + 40;
          const y2 = (stage.endIndex + 1) * NODE_GAP + 40;
          return (
            <rect
              key={stage.id}
              x={0}
              y={y1}
              width={STAGE_BAR_WIDTH}
              height={y2 - y1}
              fill={stage.color}
              opacity={0.6}
              rx={3}
            />
          );
        })}

        {/* Central timeline line */}
        <line
          x1={LEFT_MARGIN}
          y1={30}
          x2={LEFT_MARGIN}
          y2={totalHeight - 30}
          stroke="#8b6914"
          strokeWidth={2}
          strokeDasharray="8 4"
        />

        {/* Render visible commits */}
        {commits.slice(visibleRange.start, visibleRange.end).map((commit, idx) => {
          const actualIndex = visibleRange.start + idx;
          const cy = actualIndex * NODE_GAP + 40;
          const stage = getStageForIndex(actualIndex);
          const isMerge = commit.isMerge;

          return (
            <g key={commit.hash}>
              {/* Date label */}
              <text
                x={LEFT_MARGIN - 20}
                y={cy - 16}
                textAnchor="end"
                fill="#c4a86b"
                fontSize={10}
                fontFamily="JetBrains Mono, monospace"
              >
                {commit.date}
              </text>

              {/* Connection line from timeline to node */}
              <line
                x1={LEFT_MARGIN}
                y1={cy}
                x2={LEFT_MARGIN + 30}
                y2={cy}
                stroke={typeColors[commit.type]}
                strokeWidth={1.5}
                opacity={0.5}
              />

              {/* Commit node */}
              {isMerge ? (
                <polygon
                  points={`${LEFT_MARGIN + 40},${cy - NODE_RADIUS} ${LEFT_MARGIN + 40 + NODE_RADIUS},${cy} ${LEFT_MARGIN + 40},${cy + NODE_RADIUS} ${LEFT_MARGIN + 40 - NODE_RADIUS},${cy}`}
                  fill={typeColors[commit.type]}
                  stroke="#f5deb3"
                  strokeWidth={2}
                  className="cursor-pointer transition-all hover:opacity-80"
                  onClick={() => setSelectedCommit(commit)}
                  onMouseEnter={() => setHoveredCommit(commit)}
                  onMouseLeave={() => setHoveredCommit(null)}
                />
              ) : (
                <circle
                  cx={LEFT_MARGIN + 40}
                  cy={cy}
                  r={NODE_RADIUS}
                  fill={typeColors[commit.type]}
                  stroke="#f5deb3"
                  strokeWidth={2}
                  className="cursor-pointer transition-all hover:r-3"
                  onClick={() => setSelectedCommit(commit)}
                  onMouseEnter={() => setHoveredCommit(commit)}
                  onMouseLeave={() => setHoveredCommit(null)}
                />
              )}

              {/* Type badge */}
              <rect
                x={LEFT_MARGIN + 60}
                y={cy - 10}
                width={32}
                height={16}
                rx={3}
                fill={typeColors[commit.type]}
                opacity={0.85}
              />
              <text
                x={LEFT_MARGIN + 76}
                y={cy + 3}
                textAnchor="middle"
                fill="white"
                fontSize={9}
                fontWeight="bold"
              >
                {typeLabels[commit.type]}
              </text>

              {/* Commit subject */}
              <text
                x={LEFT_MARGIN + 100}
                y={cy + 4}
                fill="#f0e6d3"
                fontSize={12}
                fontFamily="JetBrains Mono, monospace"
                className="cursor-pointer"
                onClick={() => setSelectedCommit(commit)}
                onMouseEnter={() => setHoveredCommit(commit)}
                onMouseLeave={() => setHoveredCommit(null)}
              >
                {commit.subject.length > 70
                  ? commit.subject.slice(0, 70) + '...'
                  : commit.subject}
              </text>

              {/* Short hash */}
              <text
                x={LEFT_MARGIN + 100}
                y={cy + 18}
                fill="#8b6914"
                fontSize={9}
                fontFamily="JetBrains Mono, monospace"
              >
                {commit.short}
              </text>

              {/* Stage indicator dot */}
              {stage && (
                <circle
                  cx={LEFT_MARGIN + 40}
                  cy={cy}
                  r={NODE_RADIUS + 4}
                  fill="none"
                  stroke={stage.color}
                  strokeWidth={1.5}
                  opacity={0.4}
                  strokeDasharray="3 2"
                />
              )}
            </g>
          );
        })}

        {/* Start marker */}
        <text
          x={LEFT_MARGIN}
          y={25}
          textAnchor="middle"
          fill="#daa520"
          fontSize={11}
          fontWeight="bold"
        >
          START
        </text>

        {/* End marker */}
        <text
          x={LEFT_MARGIN}
          y={totalHeight - 15}
          textAnchor="middle"
          fill="#daa520"
          fontSize={11}
          fontWeight="bold"
        >
          HEAD
        </text>
      </svg>
    </div>
  );
}
