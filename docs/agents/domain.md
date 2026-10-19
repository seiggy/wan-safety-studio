# Domain docs

## Layout

This repo uses one domain context. Read the root `GLOSSARY.md` and relevant decisions under `docs\adr\` before exploring code. Developer guides remain in `docs-site\docs\`; Azure hosting guides live in `docs-site\deployment\`.

## Missing files

If the glossary or ADR folder is absent, proceed silently. Don't propose creating them upfront. The domain-modeling skill creates them when terms or decisions get resolved.

## Vocabulary and decisions

Use the glossary's terms when naming domain concepts in issues or code changes. If a needed concept is missing, check the code's vocabulary first and note genuine gaps for domain-modeling.

And surface any conflict with an existing ADR before proposing a different approach. Cite the ADR and explain why it should be reconsidered; leave it in place until the decision changes.
