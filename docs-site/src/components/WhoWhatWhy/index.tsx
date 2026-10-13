import type {ReactNode} from 'react';
import styles from './styles.module.css';

interface Props {
  who: ReactNode;
  what: ReactNode;
  why: ReactNode;
}

/** Opens a page with who works on this area, what it does, and why it exists. */
export default function WhoWhatWhy({who, what, why}: Props): ReactNode {
  return (
    <dl className={styles.grid}>
      <div><dt>Who</dt><dd>{who}</dd></div>
      <div><dt>What</dt><dd>{what}</dd></div>
      <div><dt>Why</dt><dd>{why}</dd></div>
    </dl>
  );
}
