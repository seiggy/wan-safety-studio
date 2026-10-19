// Flags AI-writing tells in docs pages, from the humanize-writing dictionary. Usage: node scripts/check-prose.mjs [files...]
// With no arguments it scans every .mdx under docs/ and deployment/. Exit code 1 when anything is flagged.
import {readFileSync, readdirSync, statSync} from 'node:fs';
import {join, dirname} from 'node:path';
import {fileURLToPath} from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const tier1 = /\b(delve|tapestry|landscape|pivotal|underscore[sd]?|testament|intricate|intricacies|meticulous(ly)?|nuanced|multifaceted|embark|spearhead|bolster(ed)?|garner|interplay|realm|labyrinth|symphony)\b/gi;
const tier2 = /\b(crucial|vibrant|foster|enhance|leverage|navigate|resonate|illuminate|showcase|enduring|robust|holistic|comprehensive|innovative|dynamic|seamless(ly)?|cutting-edge|game-changer)\b/gi;
const transitions = /\b(Furthermore|Moreover|Additionally|Consequently|Nevertheless|Subsequently|Nonetheless|Indeed)\b/g;
const phrases = /(let's dive in|let's unpack|here's the thing|it's worth noting|it's important to understand|in conclusion|in summary)/gi;

const walk = (dir) => readdirSync(dir).flatMap((name) => {
  const path = join(dir, name);
  return statSync(path).isDirectory() ? walk(path) : path.endsWith('.mdx') ? [path] : [];
});
const files = process.argv.length > 2 ? process.argv.slice(2) : [...walk(join(root, 'docs')), ...walk(join(root, 'deployment'))];

let flagged = 0;
for (const file of files) {
  // Prose only: drop fenced code and inline code.
  const text = readFileSync(file, 'utf8').replace(/```[\s\S]*?```/g, '').replace(/`[^`]*`/g, '');
  const words = text.split(/\s+/).length;
  const report = (label, matches) => { if (matches.length) { flagged++; console.log(`${file}: ${label}: ${[...new Set(matches.map((m) => m[0]))].join(', ')}`); } };
  report('tier-1 word', [...text.matchAll(tier1)]);
  report('phrase', [...text.matchAll(phrases)]);
  report('not-X-but-Y', [...text.matchAll(/\bnot (?:because |just |only )?[^.]{1,60}, but\b/gi)]);
  const counts = {};
  for (const m of text.matchAll(tier2)) counts[m[0].toLowerCase()] = (counts[m[0].toLowerCase()] ?? 0) + 1;
  const repeated = Object.entries(counts).filter(([, n]) => n > 1);
  if (repeated.length) { flagged++; console.log(`${file}: tier-2 word repeated: ${repeated.map(([w, n]) => `${w} x${n}`).join(', ')}`); }
  const transitionHits = [...text.matchAll(transitions)];
  if (transitionHits.length > 2) { flagged++; console.log(`${file}: ${transitionHits.length} formal transitions`); }
  const dashes = (text.match(/—/g) ?? []).length;
  if (dashes > Math.max(1, Math.ceil(words / 500))) { flagged++; console.log(`${file}: ${dashes} em dashes in ${words} words`); }
}
console.log(flagged ? `${flagged} issue(s) flagged.` : 'Prose check clean.');
process.exit(flagged ? 1 : 0);
