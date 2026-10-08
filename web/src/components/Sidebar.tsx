export type ViewName = 'create' | 'library' | 'vision' | 'environment';

const ITEMS: { view: ViewName; icon: string; label: string }[] = [
  { view: 'create', icon: '+', label: 'Create video' },
  { view: 'library', icon: '▤', label: 'Video library' },
  { view: 'vision', icon: '▣', label: 'Vision datasets' },
  { view: 'environment', icon: '≡', label: 'Environment' },
];

interface Props {
  view: ViewName;
  onSelect: (view: ViewName) => void;
  local?: boolean;
}

export function Sidebar({ view, onSelect, local }: Props) {
  return (
    <nav className="sidebar" aria-label="Studio views">
      <span className="nav-title">Workspace</span>
      {ITEMS.map((item) => (
        <button
          key={item.view}
          type="button"
          data-view={item.view}
          className={`nav-item${item.view === view ? ' selected' : ''}`}
          aria-current={item.view === view ? 'page' : undefined}
          onClick={() => onSelect(item.view)}
        >
          <span aria-hidden="true">{item.icon}</span>
          {item.label}
        </button>
      ))}
      <div className="nav-footer">
        <strong>Video Generation</strong>
        <span>{local ? 'Local ComfyUI workflow' : 'Private Azure ML workflow'}</span>
        <span>One video per job</span>
      </div>
    </nav>
  );
}
