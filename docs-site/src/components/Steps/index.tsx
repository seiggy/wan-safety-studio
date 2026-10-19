import type {ReactNode} from 'react';
import Link from '@docusaurus/Link';
import styles from './styles.module.css';

export interface Step {
  title: string;
  to?: string;
  body: ReactNode;
}

/** A numbered flow of steps, for request paths and deployment sequences. */
export default function Steps({steps}: {steps: Step[]}): ReactNode {
  return (
    <ol className={styles.steps}>
      {steps.map((step) => (
        <li key={step.title}>
          <strong>{step.to ? <Link to={step.to}>{step.title}</Link> : step.title}</strong>
          <div>{step.body}</div>
        </li>
      ))}
    </ol>
  );
}
