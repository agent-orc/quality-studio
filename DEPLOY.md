# Deploying the Quality Studio website

The contents of `website/` are published 1:1 to a single-commit `deploy` branch. There is no build step and no generated site output.

## Automatic publishing

Push a website change to `main`, or run the `deploy-website` workflow manually. The workflow initializes `website/` as a fresh Git repository and force-pushes its contents to this repository's `deploy` branch.

The workflow needs the repository's default `GITHUB_TOKEN` with `contents: write`. If organization or repository policy restricts workflow tokens, enable **Read and write permissions** under **Settings → Actions → General → Workflow permissions**.

## Host registration (operator step)

The private Agent Orchestrator website meta-repository controls which deploy branches are served. An operator must add this site to its `sites.json`; do not make that private-repository change from here.

Use these registration values:

```json
{
  "slug": "quality",
  "path": "/quality/",
  "repository": "agent-orc/quality-studio",
  "branch": "deploy"
}
```

Follow the surrounding `sites.json` schema if its field names differ; the required routing facts are slug `quality`, public path `/quality/`, and this repository's `deploy` branch.

After the host syncs the branch, verify:

- `https://agent-orchestrator.dev/quality/` returns the page.
- Assets and links resolve under `/quality/`.
- A later push to `main` replaces the deploy branch and reaches the host.

## Manual recovery

Prefer rerunning the workflow. If the branch must be reconstructed manually, publish only the contents of `website/` as the root of a new orphan `deploy` branch, matching the workflow exactly. Never copy the repository root into the deploy branch.
