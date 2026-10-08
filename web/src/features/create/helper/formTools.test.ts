import { describe, expect, it } from 'vitest';
import { applyHelperTool, formStateBlock, type HelperForm } from './formTools';

const limits = { aspects: ['16:9', '4:3', '1:1'], modes: ['draft', 'fast'] };
const form: HelperForm = {
  scenes: [{ key: 1, prompt: 'a', hasImage: true }],
  negativePrompt: '',
  aspect: '16:9',
  duration: 5,
  takes: 1,
  mode: 'draft',
};
const ok = (name: string, args: unknown, from = form) => {
  const outcome = applyHelperTool(from, name, args, limits);
  if ('error' in outcome) throw new Error(outcome.error);
  return outcome.form;
};

describe('helper form tools', () => {
  it('edits prompts and settings', () => {
    expect(ok('set_scene_prompt', { scene: 1, prompt: 'new' }).scenes[0]?.prompt).toBe('new');
    expect(ok('set_negative_prompt', { prompt: 'blurry' }).negativePrompt).toBe('blurry');
    expect(ok('set_aspect_ratio', { value: '4:3' }).aspect).toBe('4:3');
    expect(ok('set_duration', { seconds: '7' }).duration).toBe(7);
    expect(ok('set_takes', { count: 3 }).takes).toBe(3);
    expect(ok('set_quality', { mode: 'fast' }).mode).toBe('fast');
  });

  it('adds scenes without a key up to the cap, and never removes scene 1', () => {
    let current = form;
    for (let i = 0; i < 4; i += 1) current = ok('add_scene', { prompt: `s${i}` }, current);
    expect(current.scenes.map((scene) => scene.key)).toEqual([1, null, null, null, null]);
    expect(applyHelperTool(current, 'add_scene', {}, limits)).toHaveProperty('error');
    expect(applyHelperTool(current, 'remove_scene', { scene: 1 }, limits)).toHaveProperty('error');
    expect(ok('remove_scene', { scene: 2 }, current).scenes).toHaveLength(4);
  });

  it('rejects values the form cannot take', () => {
    for (const [name, args] of [
      ['set_scene_prompt', { scene: 2, prompt: 'x' }],
      ['set_scene_prompt', { scene: 1, prompt: '  ' }],
      ['set_aspect_ratio', { value: '21:9' }],
      ['set_duration', { seconds: 11 }],
      ['set_takes', { count: 2 }],
      ['set_quality', { mode: 'prime' }],
      ['nope', {}],
    ] as const) {
      expect(applyHelperTool(form, name, args, limits)).toHaveProperty('error');
    }
  });

  it('describes the form for the model', () => {
    const block = formStateBlock(form, ['draft']);
    expect(block).toContain('<form_state>');
    expect(JSON.parse(block.replace(/<\/?form_state>/g, '')).totalGpuJobs).toBe(1);
  });
});
