import { AppearanceMenu } from './AppearanceMenu';

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
  datasetBuilder: boolean;
}

export function Sidebar({ view, onSelect, datasetBuilder }: Props) {
  return (
    <nav className="sidebar" aria-label="Studio views">
      {ITEMS.filter((item) => item.view !== 'vision' || datasetBuilder).map((item) => (
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
      <div className="nav-footer"><AppearanceMenu /></div>
    </nav>
  );
}
