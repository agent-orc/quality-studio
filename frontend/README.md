# Quality Studio frontend

Standalone Angular 20 shell for browsing repository quality data. It provides a virtualized hierarchy, virtualized code viewer, review lanes, and shared light/dark theme tokens without a component library.

## Development

```powershell
npm install
npm start
```

For the full product from the repository root, use `npm start`. That command
boots the API and frontend together with the repository-owned launcher, while
the standalone frontend development server still runs at
`http://localhost:4200` and proxies `/api` to the QS API at
`http://127.0.0.1:5127` by default. If the API is unavailable, the shell shows
clearly labeled preview data so the workspace remains inspectable.

Run `npm run build` for the production bundle and `npm run perf` against a running server for the interaction-budget harness. See [PERF.md](./PERF.md) for the acceptance numbers and Chrome tracing procedure, and [DESIGN-KINSHIP.md](./DESIGN-KINSHIP.md) for the Agent Studio token mapping.

## Workspace layout

The Explorer and Review panel can be collapsed to give the editor more room, and both side panes can be resized by dragging the handle on their border (double-click a handle to reset that pane to its default width). Layout state — which panes are visible and how wide they are — persists in `localStorage` under `qs-layout`, separate from the `qs-theme` key.

Keyboard shortcuts:

- `Ctrl+B` — toggle the Explorer
- `Ctrl+Alt+B` — toggle the Review panel

## Embedded URL preview contract

When the shell runs inside an iframe, every selected repository, path, or
review-kind change updates the shell URL and sends exactly this v1 message to
its parent:

```json
{
  "source": "url-preview-embed",
  "type": "navigation",
  "url": "https://quality.example/?repo=default&path=src%2FExample.cs&kind=code"
}
```

The sender uses `window.parent.postMessage(message, '*')`. The contract is
navigation-only: it carries no commands, finding bodies, source snippets,
credentials, or mutation requests. A future message that adds sensitive data
or commands must use a new version and a configured parent origin. Receivers
must validate the iframe source, message tags, URL syntax, and URL origin.
