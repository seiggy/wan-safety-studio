import type {ReactNode} from 'react';
import styles from './styles.module.css';

export interface Setting {
  name: string;
  default?: string;
  description: ReactNode;
}

/** A settings reference. `name` is the configuration key, for example `AzureMl__Armed`. */
export default function SettingsTable({settings}: {settings: Setting[]}): ReactNode {
  return (
    <table className={styles.table}>
      <thead>
        <tr><th>Setting</th><th>Default</th><th>What it does</th></tr>
      </thead>
      <tbody>
        {settings.map((setting) => (
          <tr key={setting.name}>
            <td><code>{setting.name}</code></td>
            <td>{setting.default ? <code>{setting.default}</code> : 'none'}</td>
            <td>{setting.description}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
