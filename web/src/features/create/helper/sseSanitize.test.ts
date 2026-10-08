import { describe, expect, it } from 'vitest';
import { cleanSseBlock } from './sseSanitize';

describe('cleanSseBlock', () => {
  it('drops nulls and the raw debug copy so the AG-UI client schema accepts the event', () => {
    const block = 'data: {"type":"RUN_STARTED","threadId":"t","runId":"r","parentRunId":null,"input":null,"rawEvent":{"a":1}}';
    expect(JSON.parse(cleanSseBlock(block).slice(5))).toEqual({ type: 'RUN_STARTED', threadId: 't', runId: 'r' });
  });

  it('cleans nested nulls and leaves non-data lines alone', () => {
    const block = 'event: x\ndata: {"outcome":{"type":"success","pendingToolCallIds":null},"usage":[{"model":null,"inputTokens":0}]}';
    const [event, data] = cleanSseBlock(block).split('\n');
    expect(event).toBe('event: x');
    expect(JSON.parse((data ?? '').slice(5))).toEqual({ outcome: { type: 'success' }, usage: [{ inputTokens: 0 }] });
  });
});
