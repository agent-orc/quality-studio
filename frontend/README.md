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
