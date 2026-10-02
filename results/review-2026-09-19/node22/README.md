# Pinned Node runtime validation

Repository .nvmrc pins Node 22.23.1. The current global runtime is Node 24.18.0. Targeted NVM environment/known installation paths had no Node 22.23.1, so an official portable Windows x64 ZIP was used in a unique temporary directory.

The official archive returned HTTP 200 before download. SHA256 was checked against the same release's official SHASUMS256.txt; sha256sum reported OK.

- [Official archive](https://nodejs.org/dist/v22.23.1/node-v22.23.1-win-x64.zip)
- [Official checksums](https://nodejs.org/dist/v22.23.1/SHASUMS256.txt)
- SHA256: 7df0bc9375723f4a86b3aa1b7cc73342423d9677a8df4538aca31a049e309c29

Only the child commands had the portable runtime prepended to PATH; its bundled npm-cli.js invoked the existing project scripts. No global installation, persistent environment change, npm ci, or node_modules replacement was performed.

| Check | Result | Evidence |
| --- | --- | --- |
| Runtime | v22.23.1 | runtime.log |
| npm run test:coverage, installed Edge | 215 passed | coverage-green.log |
| CI line-coverage validation | 74.88%; baseline 60.82%, floor 58.82% | coverage-validation.log |
| npm run build | Passed, bundle budgets passed | build.log |

Existing Angular NG8102 and Prism CommonJS warnings remain. The earlier Node 24 run measured 75.00% line coverage; both randomized full suites comfortably pass the same gate. The real-API performance harness was run under Node 24, as its separate report states; it was not repeated under Node 22.

After commands completed, the resolved temporary runtime directory was verified to equal C:/Users/rmisc/AppData/Local/Temp/qs-node22-verify-509pfs, then that directory and its own pointer file were removed. Global node --version still reports v24.18.0. No product changes were made for this runtime validation.
