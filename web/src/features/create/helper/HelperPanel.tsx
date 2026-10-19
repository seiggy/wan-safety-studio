import { Streamdown } from 'streamdown';
import 'streamdown/styles.css';
import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import type { HelperMessage, HelperStatus } from './useVideoHelper';

interface Props {
  status: HelperStatus | null;
  messages: HelperMessage[];
  running: boolean;
  error: string | null;
  onSend: (text: string) => void;
  onStop: () => void;
  onClear: () => void;
  onClose: () => void;
}

const SUGGESTIONS = [
  'Improve my scene prompt for Wan 2.2',
  'Suggest a negative prompt for a CCTV look',
  'Add a scene with a different hazard',
];

export function HelperPanel({ status, messages, running, error, onSend, onStop, onClear, onClose }: Props) {
  const [draft, setDraft] = useState('');
  const log = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    const element = log.current;
    if (element) element.scrollTop = element.scrollHeight;
  }, [messages]);

  useEffect(() => input.current?.focus(), []);

  const submit = (event?: FormEvent) => {
    event?.preventDefault();
    if (!draft.trim() || running) return;
    onSend(draft);
    setDraft('');
  };

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey) submit(event);
  };

  const last = messages.at(-1);

  return (
    <aside className="helper" aria-labelledby="helper-heading">
      <div className="helper-head">
        <h2 id="helper-heading">Video helper</h2>
        <span className="helper-tools">
          <button type="button" className="text-button" onClick={onClear} disabled={!messages.length}>Clear</button>
          <button type="button" className="text-button" onClick={onClose} aria-label="Close video helper">Close</button>
        </span>
      </div>
      {status?.enabled === false ? (
        <div className="helper-empty">
          <p>The helper isn’t set up yet.</p>
          <p className="field-help">{status.hint ?? 'Set Agent:Endpoint and Agent:Model, then restart the AppHost.'}</p>
        </div>
      ) : (
        <>
          <div ref={log} className="helper-log" role="log" aria-live="polite" aria-label="Conversation">
            {messages.length === 0 && (
              <div className="helper-empty">
                <p>Ask for help with prompts, settings or scenes. I can edit the form for you.</p>
                <div className="helper-suggestions">
                  {SUGGESTIONS.map((suggestion) => (
                    <button key={suggestion} type="button" className="secondary-button" disabled={running} onClick={() => onSend(suggestion)}>
                      {suggestion}
                    </button>
                  ))}
                </div>
              </div>
            )}
            {messages.map((message) => (
              <div key={message.id} className={`helper-message ${message.role}`}>
                {message.role === 'user' ? (
                  <>
                    <p>{message.text}</p>
                    {message.scenes && message.scenes.length > 0 && (
                      <small className="helper-attached">Sent {message.scenes.map((scene) => `scene ${scene}`).join(', ')} reference image</small>
                    )}
                  </>
                ) : (
                  <>
                    {message.text && (
                      <Streamdown className="helper-md" isAnimating={running && message === last}>
                        {message.text}
                      </Streamdown>
                    )}
                    {message.actions.length > 0 && (
                      <ul className="helper-actions">
                        {message.actions.map((action, index) => (
                          <li key={index} className={action.error ? 'error' : undefined}>{action.summary}</li>
                        ))}
                      </ul>
                    )}
                    {running && message === last && !message.text && !message.actions.length && <p className="field-help">Thinking…</p>}
                  </>
                )}
              </div>
            ))}
            {error && <p className="notice error" role="alert">{error}</p>}
          </div>
          <form className="helper-composer" onSubmit={submit}>
            <label className="visually-hidden" htmlFor="helper-input">Message the video helper</label>
            <textarea
              id="helper-input"
              ref={input}
              rows={3}
              maxLength={4000}
              placeholder="Ask about prompts, or say what to change…"
              value={draft}
              disabled={status === null}
              onChange={(event) => setDraft(event.target.value)}
              onKeyDown={onKeyDown}
            />
            {running ? (
              <button type="button" className="secondary-button" onClick={onStop}>Stop</button>
            ) : (
              <button type="submit" className="primary-button" disabled={!draft.trim() || status === null}>Send</button>
            )}
          </form>
        </>
      )}
    </aside>
  );
}
