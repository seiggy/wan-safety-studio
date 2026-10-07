let rejected: boolean | null = null;

/** True when the OIDC callback redirected back with ?signin; strips the query once so reloads are clean. */
export function signinWasRejected(): boolean {
  if (rejected === null) {
    rejected = new URLSearchParams(location.search).has('signin');
    if (rejected) history.replaceState(null, '', location.pathname);
  }
  return rejected;
}
