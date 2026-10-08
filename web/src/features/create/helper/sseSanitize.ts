/**
 * The Agent Framework server writes optional AG-UI fields as explicit nulls (for example `"parentRunId": null`) and attaches
 * a `rawEvent` debug copy to each event. The AG-UI client's schema accepts missing optional fields but rejects nulls, so the
 * stream is cleaned before the client parses it.
 */
const withoutNulls = (value: unknown): unknown => {
  if (Array.isArray(value)) return value.map(withoutNulls);
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .filter(([key, item]) => item !== null && key !== 'rawEvent')
        .map(([key, item]) => [key, withoutNulls(item)]),
    );
  }
  return value;
};

export function cleanSseBlock(block: string): string {
  return block
    .split(/\r?\n/)
    .map((line) => {
      if (!line.startsWith('data:')) return line;
      try {
        return `data: ${JSON.stringify(withoutNulls(JSON.parse(line.slice(5))))}`;
      } catch {
        return line;
      }
    })
    .join('\n');
}

export async function sanitizingFetch(url: string, init: RequestInit): Promise<Response> {
  const response = await fetch(url, init);
  if (!response.body || !response.headers.get('content-type')?.includes('text/event-stream')) return response;

  const decoder = new TextDecoder();
  const encoder = new TextEncoder();
  let buffer = '';
  const cleaned = response.body.pipeThrough(
    new TransformStream<Uint8Array, Uint8Array>({
      transform(chunk, controller) {
        buffer += decoder.decode(chunk, { stream: true });
        const blocks = buffer.split(/\r?\n\r?\n/);
        buffer = blocks.pop() ?? '';
        for (const block of blocks) controller.enqueue(encoder.encode(`${cleanSseBlock(block)}\n\n`));
      },
      flush(controller) {
        if (buffer.trim()) controller.enqueue(encoder.encode(`${cleanSseBlock(buffer)}\n\n`));
      },
    }),
  );
  return new Response(cleaned, { status: response.status, statusText: response.statusText, headers: response.headers });
}
