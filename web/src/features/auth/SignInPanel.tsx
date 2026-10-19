interface Props {
  error: string | null;
}

export function SignInPanel({ error }: Props) {
  return (
    <section id="signin-panel" className="signin-panel">
      <div className="signin-symbol" aria-hidden="true">V</div>
      <h1>Your safety-video workspace</h1>
      <p>
        Create reference-image videos with the prepared Video workflow, follow Azure ML jobs, and review completed clips in one place.
      </p>
      <a className="primary-button signin-link" href="/auth/login">Sign in with Microsoft</a>
      <p className="secondary-copy">
        Access is limited to your organization’s approved Video Creators group. Azure credentials stay on the local server.
      </p>
      <p id="signin-error" className="notice error" role="alert" hidden={!error}>{error}</p>
    </section>
  );
}
