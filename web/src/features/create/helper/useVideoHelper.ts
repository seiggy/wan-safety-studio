import type { HttpAgent } from '@ag-ui/client';
import { useCallback, useEffect, useRef, useState } from 'react';
import { api, errorMessage } from '../../../api/client';
import { toWireImage } from './images';
import { sanitizingFetch } from './sseSanitize';

export interface HelperStatus {
  enabled: boolean;
  model?: string | null;
  endpoint?: string | null;
  vision?: boolean;
  auth?: string;
  hint?: string | null;
}

export interface HelperMessage {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  /** Scenes whose reference image was sent with this message. */
  scenes?: number[];
  /** What the helper changed in the form while writing this message. */
  actions: { summary: string; error?: boolean }[];
}

interface Options {
  /** Applies a tool call to the form and returns what happened. */
  applyTool: (name: string, args: unknown) => { summary: string; error?: boolean };
  /** The live form, prepended to each user message. */
  formState: () => string;
  /** Every scene's reference image by 1-based scene number; the hook sends each file once. */
  sceneImages: () => { scene: number; file: File }[];
}

const AGENT_URL = '/api/agent';
let serial = 0;
const nextId = (): string => `m${++serial}`;

export function useVideoHelper({ applyTool, formState, sceneImages }: Options) {
  const seen = useRef(new WeakSet<File>());
  const [status, setStatus] = useState<HelperStatus | null>(null);
  const [messages, setMessages] = useState<HelperMessage[]>([]);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const agent = useRef<HttpAgent | null>(null);
  const history = useRef<HelperMessage[]>([]);
  history.current = messages;

  useEffect(() => {
    let alive = true;
    api
      .get<HelperStatus>('/api/agent/status')
      .then((value) => alive && setStatus(value))
      .catch(() => alive && setStatus({ enabled: false, hint: 'The helper status could not be read.' }));
    return () => {
      alive = false;
    };
  }, []);

  const patchMessage = useCallback((id: string, update: (message: HelperMessage) => HelperMessage) => {
    setMessages((current) => current.map((message) => (message.id === id ? update(message) : message)));
  }, []);

  const send = useCallback(
    async (text: string) => {
      const prompt = text.trim();
      if (!prompt || running) return;
      setError(null);
      const images = status?.vision ? sceneImages().filter(({ file }) => !seen.current.has(file)) : [];
      const user: HelperMessage = { id: nextId(), role: 'user', text: prompt, actions: [], scenes: images.map((image) => image.scene) };
      const reply: HelperMessage = { id: nextId(), role: 'assistant', text: '', actions: [] };
      const prior = history.current.filter((message) => message.text);
      setMessages([...prior, user, reply]);
      setRunning(true);

      try {
        // The server is stateless: resend the transcript, with the live form and any new reference images on this turn only.
        const encoded = await Promise.all(images.map(async ({ scene, file }) => ({ scene, file, image: await toWireImage(file) })));
        const label = encoded.length ? `Reference images for ${encoded.map(({ scene }) => `scene ${scene}`).join(', ')} are attached, in that order.\n\n` : '';
        const content = [
          { type: 'text' as const, text: `${formState()}\n\n${label}${prompt}` },
          ...encoded.map(({ image }) => ({ type: 'image' as const, source: { type: 'data' as const, value: image.data, mimeType: image.mimeType } })),
        ];
        const wire = [
          ...prior.map(({ id, role, text: body }) => ({ id, role, content: body })),
          { id: user.id, role: 'user' as const, content },
        ];
        encoded.forEach(({ file }) => seen.current.add(file));
        const { HttpAgent: Agent } = await import('@ag-ui/client');
        const http = new Agent({ url: AGENT_URL, initialMessages: wire, fetch: sanitizingFetch });
        agent.current = http;
        await http.runAgent(
          {},
          {
            onTextMessageContentEvent: ({ event }) => patchMessage(reply.id, (message) => ({ ...message, text: message.text + event.delta })),
            onToolCallEndEvent: ({ toolCallName, toolCallArgs }) => {
              const outcome = applyTool(toolCallName, toolCallArgs);
              patchMessage(reply.id, (message) => ({ ...message, actions: [...message.actions, outcome] }));
            },
            onRunErrorEvent: ({ event }) => setError(event.message),
          },
        );
      } catch (failure) {
        if (!(failure instanceof DOMException && failure.name === 'AbortError')) setError(errorMessage(failure));
      } finally {
        agent.current = null;
        setRunning(false);
      }
    },
    [applyTool, formState, sceneImages, patchMessage, running, status?.vision],
  );

  const stop = useCallback(() => agent.current?.abortRun(), []);
  const clear = useCallback(() => {
    agent.current?.abortRun();
    setMessages([]);
    setError(null);
  }, []);

  return { status, messages, running, error, send, stop, clear };
}
