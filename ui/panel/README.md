# GPTino Panel

React/TypeScript UI embedded in the GPTino Rhino per-document panel. The Rhino panel is the only dashboard surface; session terminals attach to the same AgentHost state.

## Local development

```powershell
npm install
npm run dev
```

Open `http://localhost:5173/?demo=1` to use the in-memory demo runtime. When a development AgentHost is unavailable, Vite development mode also falls back to demo data after the initial request fails.

## AgentHost API contract

The panel uses same-origin `/api/v1` by default. The host may set `window.__GPTINO__.apiBase` or the `apiBase` query parameter when a different origin is required.

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/v1/runtime` | Complete immutable runtime projection |
| `GET` | `/api/v1/events` | SSE stream; default or `state` events contain the runtime projection |
| `POST` | `/api/v1/sessions` | Create a session with `{ name, role, modelProfile }` |
| `PUT` | `/api/v1/sessions/order` | `{ orderedSessionIds, orderVersion }` CAS reorder |
| `PUT` | `/api/v1/sessions/{id}/pause` | Pause or resume a session |
| `PUT` | `/api/v1/sessions/{id}/mode` | Set `plan` or `auto` |
| `PUT` | `/api/v1/sessions/{id}/model` | Set `auto`, `fast`, `standard`, or `deep` |
| `POST` | `/api/v1/sessions/{id}/messages` | Submit an instruction with an idempotent `clientMessageId` |
| `POST` | `/api/v1/sessions/{id}/terminal` | Open or focus the attached terminal |
| `PUT` | `/api/v1/runtime/pause` | Pause or resume all execution |
| `POST` | `/api/v1/runtime/stop-current` | Stop through the executor rollback path |
When SSE cannot connect, the client automatically polls `/api/v1/runtime` every 1.5 seconds.
