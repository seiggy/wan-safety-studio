import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage } from './api/client';
import type { SessionInfo } from './api/types';
import { TopBar } from './components/TopBar';
import { SignInPanel } from './features/auth/SignInPanel';
import { signinWasRejected } from './features/auth/signinRejection';
import { clearSavedBatch } from './features/create/batch';
import { Workspace } from './Workspace';

const REJECTED_MESSAGE =
  'Sign-in was not accepted. Confirm your tenant and membership in the approved creator group, then start a new sign-in.';

export function App() {
  const [session, setSession] = useState<SessionInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [signinError, setSigninError] = useState<string | null>(() => (signinWasRejected() ? REJECTED_MESSAGE : null));

  const signedOut = useCallback(() => {
    api.clearSession();
    setSession(null);
  }, []);

  useEffect(() => {
    api.setUnauthorizedHandler(signedOut);
    let current = true;
    api
      .loadSession()
      .then((info) => {
        if (current) setSession(info);
      })
      .catch((error: unknown) => {
        if (!current) return;
        const message = errorMessage(error);
        if (!message.startsWith('Sign in with')) setSigninError(message);
      })
      .finally(() => {
        if (current) setLoading(false);
      });
    return () => {
      current = false;
    };
  }, [signedOut]);

  const signOut = async () => {
    try {
      await api.post('/auth/logout');
      clearSavedBatch();
      signedOut();
    } catch {
      // A failed sign-out leaves the session in place; the server error is not actionable here.
    }
  };

  return (
    <>
      <a className="skip-link" href="#main">Skip to workspace</a>
      <TopBar accountName={session?.name ?? null} onSignOut={() => void signOut()} />
      <main id="main" tabIndex={-1}>
        {session ? <Workspace /> : !loading && <SignInPanel error={signinError} />}
      </main>
    </>
  );
}
