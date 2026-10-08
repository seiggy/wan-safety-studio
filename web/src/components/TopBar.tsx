import { AppearanceMenu } from './AppearanceMenu';

interface Props {
  accountName: string | null;
  onSignOut: () => void;
}

export function TopBar({ accountName, onSignOut }: Props) {
  return (
    <header className="topbar">
      <a className="brand" href="/" aria-label="Video Safety Studio home">
        <span className="brand-mark" aria-hidden="true">V</span>Video Safety Studio
      </a>
      <span className="local-label">Local workspace</span>
      <div className="account">
        <AppearanceMenu />
        <span id="account-name">{accountName ?? 'Sign-in required'}</span>
        <button id="signout" type="button" className="text-button" hidden={accountName === null} onClick={onSignOut}>Sign out</button>
      </div>
    </header>
  );
}
