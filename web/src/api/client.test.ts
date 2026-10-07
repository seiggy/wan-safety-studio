import { describe, expect, it, vi } from 'vitest';
import { ApiClient, ApiError } from './client';

const json = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status }));

describe('ApiClient', () => {
  it('sends the CSRF token from /api/session on mutating requests only', async () => {
    const fetchFn = vi.fn((input: string, _init?: RequestInit) =>
      input === '/api/session' ? json({ name: 'Ada', csrfToken: 'tok' }) : json({ ok: true }),
    );
    const client = new ApiClient(() => undefined, fetchFn);

    await client.get('/api/status');
    expect(fetchFn).toHaveBeenCalledTimes(1);
    const getInit = fetchFn.mock.calls[0]?.[1] as RequestInit;
    expect(getInit.headers).not.toHaveProperty('X-CSRF-Token');

    await client.post('/api/submit', { json: { a: 1 } });
    const postInit = fetchFn.mock.calls.at(-1)?.[1] as RequestInit;
    expect(postInit.headers).toMatchObject({ 'X-CSRF-Token': 'tok', 'Content-Type': 'application/json' });
    expect(postInit.body).toBe('{"a":1}');
  });

  it('treats 401 as signed out and surfaces the server message with its reference', async () => {
    const onUnauthorized = vi.fn();
    const client = new ApiClient(onUnauthorized, () => json({ error: 'Sign in with Microsoft.', requestId: 'r1' }, 401));

    const failure = await client.get('/api/jobs').catch((error: unknown) => error);

    expect(onUnauthorized).toHaveBeenCalledOnce();
    expect(failure).toBeInstanceOf(ApiError);
    expect(failure).toMatchObject({ status: 401, message: 'Sign in with Microsoft. Reference: r1.' });
  });

  it('falls back to a generic message for non-JSON errors without signing out', async () => {
    const onUnauthorized = vi.fn();
    const client = new ApiClient(onUnauthorized, () => Promise.resolve(new Response('boom', { status: 500 })));

    await expect(client.get('/api/status')).rejects.toThrow('Request failed (500).');
    expect(onUnauthorized).not.toHaveBeenCalled();
  });
});
