# UI review runs

Quality Studio starts Coding Agent Runner reviews through the API host. The browser never launches an agent process. `POST /api/review` (or the repository-scoped equivalent) validates the selected hierarchy node, records its descendant file plan, enqueues the work, and immediately returns `202 Accepted` with a run ID.

The API runs file reviews with bounded concurrency (`ReviewJobs:MaxConcurrency`, default `2`). Container sweeps continue after individual file failures and write the selected project, module, or namespace review after all file attempts finish. Run snapshots expose each file's state and error. The v1 queue and recent-run history are in memory; an API restart cancels active runner tokens, and Coding Agent Runner terminates the owned CLI process trees.

The UI polls `GET /api/review/runs` every 1.5 seconds only while a run is queued or running. A terminal transition refreshes the hierarchy and the open file, so sidecar grades and staleness decorations update without a page reload. `DELETE /api/review/runs/{id}` cancels queued or active work.
