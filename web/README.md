# Video Safety Studio web

React 19 + TypeScript single-page app (Vite). Replaces the static `app/web` front end.

## Develop

Run the stack through Aspire (the apphost starts this app and sets `API_URL`). The dev server listens on
`http://localhost:51881` and proxies `/api` and `/auth` to `API_URL` (default `http://localhost:51882`) with
`changeOrigin: false`, because the backend validates Host/Origin as `localhost:51881`.

Standalone: `npm install && npm run dev`.

## Build and test

- `npm run build` — typecheck and emit `dist/` (external JS/CSS only)
- `npm run typecheck`
- `npm test` — vitest + Testing Library (jsdom)

## CSP rule

The server sends `default-src 'self'; script-src 'self'; style-src 'self'; ...`. Do not use inline `style=` attributes or
React `style` props, inline scripts, CSS-in-JS runtimes, external fonts or CDNs. Use class names and the `hidden`
attribute; keep styles in `src/styles/`. `build.assetsInlineLimit` is 0 so no assets are inlined as `data:` URLs.
