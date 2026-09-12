import { typeColors, typeLabels } from '@/data/commits';

export default function Legend() {
  const types = Object.entries(typeColors) as Array<[keyof typeof typeColors, string]>;

  return (
    <div
      className="flex flex-wrap items-center gap-3 rounded-lg border px-4 py-2 text-xs"
      style={{
        backgroundColor: '#2a2018',
        borderColor: '#3a2a1a',
        color: '#c4a86b',
      }}
    >
      <span className="font-bold" style={{ color: '#daa520' }}>图例:</span>
      {types.map(([type, color]) => (
        <div key={type} className="flex items-center gap-1.5">
          <span
            className="inline-block h-2.5 w-2.5 rounded-full"
            style={{ backgroundColor: color }}
          />
          <span>{typeLabels[type]}</span>
        </div>
      ))}
      <div className="flex items-center gap-1.5">
        <span
          className="inline-block h-2.5 w-2.5 rotate-45"
          style={{ backgroundColor: '#daa520' }}
        />
        <span>合并</span>
      </div>
    </div>
  );
}
