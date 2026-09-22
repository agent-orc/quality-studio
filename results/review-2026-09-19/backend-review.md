# Backend-Review Quality Studio, 19.09.2026

## Ergebnis

Sieben zusammenhaengende Verbesserungen sind umgesetzt und gezielt verifiziert:

- **Review-Freshness:** Der Quellmanifest wird vor Promptaufbau/Sensoren aufgenommen und danach
  erneut verglichen. Eine waehrenddessen geaenderte Datei oder ein geaendertes Aggregat fuehrt zum
  Abbruch vor dem Agentenlauf; alter Promptinhalt wird nicht mehr mit dem danach gelesenen Hash
  als frisch gespeichert.
- **Host-Richtlinien:** Direkt mit `ReviewRequest` gelieferte globale/projektbezogene
  Richtlinien beeinflussen den effektiven Hash. Hinzufuegen, Aendern und Entfernen loesen einen
  neuen Review aus. Neue Standards speichern nur Digests. Bestehende Hashes ohne Zusatzkontext
  und die bisherige oeffentliche Einparameter-Signatur bleiben kompatibel.
- **Regel-Provenienz:** Ein Finding darf nur Regeln zitieren, deren Text tatsaechlich das
  Promptbudget erreicht hat. Vollstaendig ausgelassene Regeln werden auf die Basisregel
  zurueckgefuehrt.
- **Boundary-Sensor 1.1.0:** Unbekannte Authentifizierung bleibt unbekannt. Unverifizierte
  Lesezugriffe erhalten Medium, unverifizierte Prozess-/Mutationsrisiken High; explizit anonyme
  Prozess-/Mutationspfade bleiben Critical. `AllowAnonymous` in einer geschuetzten Route-Gruppe
  wird erkannt. Statische Dateien und Health-Routen werden nicht pauschal ausgenommen.
  Typisierte Aufrufe des gemeinsamen Sensor-Command-Runners bleiben als Prozessgrenzen
  mit Request-Inputs und API-Consumerbezug sichtbar; der direkte Betriebssystem-Sink bleibt erhalten.

- **Lesende Git-Abfragen:** Ein gemeinsamer Optionspraefix deaktiviert konfigurierte
  fsmonitor-Hooks und optionale Indexlocks in neun Produktwrappern. Drei echte
  Hook-Regressionen belegen Ausfuehrung vorher und sichere Abfrage nachher.
  Details und verbleibende Prozessgrenzen: [git-process-review.md](git-process-review.md).

- **Windows npm-/npx-Start:** Der Standardrunner startet die jeweilige gebuendelte
  JavaScript-CLI mit Node und einzelnen ArgumentList-Elementen. Absolute Host-PATH-Eintraege,
  bevorzugt passendes Node, kein CWD-Suchen und keine Shell. Leerzeichen/Metazeichen bleiben
  Daten. Alle acht Windows-Regressionen inklusive echter npm-/npx-Proben sind gruen;
  POSIX bleibt unveraendert.

- **npm-Audit-Scope:** Jedes gefundene Lockfile bestimmt ueber einen expliziten absoluten
  Prefix sein Auditprojekt. Ein Eltern-package.json kann den Scan nicht mehr umlenken.
  Die Lock-only-Fixture bleibt sichtbar und liefert bei echtem Audit ihren semver-High-Befund.
  [Reale Prefix-/Auditprobe](npm-prefix-probe.json)

Insgesamt kamen **25 neue Regressionstests** hinzu. Es wurden keine Commits/Pushes vorgenommen.

## Testevidenz

Alle Laeufe nutzten Release / .NET 10. Die gezielte Core-Auswahl umfasst
`ReviewRunnerTests`, `InputResolverTests`, `RequestGuidelineInputsTests`,
`StalenessEvaluatorTests`, `AggregateReviewTests`, `ReviewMetaReaderTests` und
`ReviewMetaContractTests`. Der Boundary-Lauf selektiert `BoundaryInventorySensorTests`.
Die roten Laeufe sind absichtliche Reproduktionen vor der jeweiligen Korrektur.

| Auswahl | Bestanden | Fehlgeschlagen | Nicht ausgefuehrt | Nachweis |
| --- | ---: | ---: | ---: | --- |
| Guideline-Wechsel vor Fix | 0 | 2 | 0 | [guidelines-red.trx](backend-evidence/guidelines-red.trx) |
| Codeaenderung waehrend Sensoren vor Fix | 0 | 2 | 0 | [preparation-race-red.trx](backend-evidence/preparation-race-red.trx) |
| Nicht injizierte Regel vor Fix | 0 | 1 | 0 | [rule-citation-red.trx](backend-evidence/rule-citation-red.trx) |
| Betroffene Core-Suites nach allen Core-Fixes | 84 | 0 | 0 | [core-green.trx](backend-evidence/core-green.trx) |
| Boundary-Klassifikation vor Fix | 7 | 3 | 0 | [boundaries-red.trx](backend-evidence/boundaries-red.trx) |
| Boundary-Klassifikation nach Fix | 10 | 0 | 0 | [boundaries-green.trx](backend-evidence/boundaries-green.trx) |
| Delegierter Prozessaufruf vor Integrationsfix | 0 | 1 | 0 | [boundary-delegation-red.trx](backend-evidence/boundary-delegation-red.trx) |
| Boundary-Suite nach Integrationsfix | 11 | 0 | 0 | [boundary-delegation-green.trx](backend-evidence/boundary-delegation-green.trx) |
| Git-fsmonitor vor Fix | 0 | 3 | 0 | [git-fsmonitor-red.trx](backend-evidence/git-fsmonitor-red.trx) |
| Git-fsmonitor nach Fix | 3 | 0 | 0 | [git-fsmonitor-green.trx](backend-evidence/git-fsmonitor-green.trx) |
| Betroffene Git-/Core-Suites nach Git-Fix | 102 | 0 | 0 | [git-suites-green.trx](backend-evidence/git-suites-green.trx) |
| Windows-npm-Start vor Fix | 0 | 2 | 0 | [windows-npm-red.trx](backend-evidence/windows-npm-red.trx) |
| Windows-npm + Dependency-Parser + Prozessrunner nach Fix | 28 | 0 | 3 | [windows-npm-green.trx](backend-evidence/windows-npm-green.trx) |
| Effektiver npm-Audit-Projektroot vor Prefixfix | 0 | 1 | 0 | [npm-prefix-red.trx](backend-evidence/npm-prefix-red.trx) |
| npm-Prefix + Windows + Parser + Runner nach Prefixfix | 29 | 0 | 3 | [npm-prefix-green.trx](backend-evidence/npm-prefix-green.trx) |
| ReviewRunner-Suites nach ToolBound-Testverschiebung | 34 | 0 | 0 | [review-lane-move-green.trx](backend-evidence/review-lane-move-green.trx) |
| npm/npx-Standardprobe vor npx-Fix | 1 | 1 | 0 | [windows-npx-red.trx](backend-evidence/windows-npx-red.trx) |
| npm/npx + Prefix + Parser + Runner nach npx-Fix | 31 | 0 | 3 | [windows-npx-green.trx](backend-evidence/windows-npx-green.trx) |

Die abschliessenden gezielten Ergebnisse sind **84/84 Core** und **11/11 Boundary**, ohne
uebersprungene Tests. Der Core-Lauf prueft unter anderem einen festen v1-Kompatibilitaetshash,
Richtlinienwechsel/-entfernung, Scan/Hierarchie-Roundtrip mit gespeichertem Hostkontext und
PolicyDrift nach Aenderung der aktuellen Repository-Regeln. Der zusaetzliche Integrationsfix
behebt den vom konsolidierten Portable-Lauf erkannten Verlust des Gitleaks-Aufrufkontexts nach
Zentralisierung des Prozessstarts. Der vorhandene Selbstscan-Assert wurde nicht abgeschwaecht.
`git diff --check` war sauber. Dies ersetzt nicht die vom Root-Agenten ausgefuehrten
kompletten named lanes / Coverage-Gates.

Der zusaetzliche Git-Fix wurde mit **102/102** bestehenden/ergaenzten Tests geprueft:
ReadOnlyGit, RepositoryHierarchy (alle passenden Suites), StalenessEvaluator,
CoverageSensor, ChangeSetReview, QualityReport, AttackCoverage, GitleaksSecurityScanner
und BoundaryInventorySensor. Diese Auswahl ueberlappt fruehere Laeufe und wird nicht
zu einer Gesamtzahl addiert. Den finalen API-/Gesamtbuild koordiniert der Root-Agent.

Der erste Windows-npm-Lauf enthaelt alle damaligen **5/5 neuen npm-Tests** sowie bestehende
Dependency-/Prozessrunner-Tests: **28 bestanden, 0 Fehler, 3 bekannte POSIX-only Skips**.
Die beiden echten Standardaufrufe waren vor dem Fix rot (npm nicht startbar). Die weiteren
Tests pruefen echte Node-Ausfuehrung mit Leerzeichen/Metazeichen, wortgetreue Argumente,
Arbeitsverzeichnis, bevorzugtes passendes Node und abgewiesene relative/empty PATH-Eintraege.
Die vorherigen Core-/Git-Laufzahlen gelten fuer ihre jeweils dokumentierte Auswahl.
Die reale Netzwerk-Auditprobe ist im nachfolgenden Prefix-Abschnitt dokumentiert.

Die anschliessende Prefix-Regression nutzt echtes npm zur Aufloesung des effektiven
Projektroots und aufgezeichnete Advisorydaten, ohne Netzwerkabhaengigkeit im Test.
Vorher wurde das Elternprojekt gewaehlt; mit explizitem Prefix stimmt der Lockroot.
Der damalige Prefix-Lauf ergab **29 bestanden, 0 Fehler, 3 bekannte POSIX-only Skips**.
Die separate echte Registryprobe der vorhandenen semver-Lockfixture ist in
`npm-prefix-probe.json` aufgezeichnet: Exit 1 mit einem High-Befund, kein ENOLOCK.
Der vom Root-Agenten danach erneut ausgefuehrte echte End-to-end-Scan am 19.09.2026
um 21:42 UTC ist [verfuegbar](dependencies-cli-final.json): Sensor 1.1.0, dotnet 10.0.301,
npm 11.16.0; genau ein High-Finding GHSA-c2qf-rxjj-qqgw fuer semver 5.7.1 in
`backend/tests/AgentOrchestrator.CodeQuality.Tests/Fixtures/dependencies/package-lock.json`.
CLI-Exit 1 bedeutet hier den gefundenen Testdatensatz, keinen Scannerfehler.
Der separat vom Root-Agenten ausgefuehrte Produkt-Frontend-Audit meldet 0 Schwachstellen.

Die Git-verwendende Guideline-Roundtrip-Regression liegt abschliessend in
`ReviewRunnerToolBoundTests` mit Klassentraits; die portable `ReviewRunnerTests`-Klasse
bleibt portabel. Beide Runner-Suites sind danach 34/34 gruen. Der unveraenderte
`node --test tests/test-lanes.test.mjs`-Vertrag ist 6/6 gruen.

Die abschliessende angrenzende npx-Regression reproduzierte denselben Windows-Startfehler
beim eingebauten TypeScript-Profil: npm startete, npx nicht (**1 gruen, 1 rot**). Der
Resolver waehlt jetzt ausschliesslich zwischen den festen CLI-Namen `npm-cli.js` und
`npx-cli.js`; weitere Executables erhalten keine Sonderbehandlung. Beide Befehle bestehen
die echte Standardprobe und die ArgumentList-Regression mit Leerzeichen/Metazeichen.
Der letzte gezielte Lauf ist **31 bestanden, 0 Fehler, 3 bekannte POSIX-only Skips**,
darunter **8/8 Windows-Tests**. Zusaetzlich startete der echte Runner im Frontend
`npx --no-install tsc --version` erfolgreich mit **Version 5.9.3**, Exit 0:
[Runner-Probe](npx-tsc-runner-probe.json). Die kleine isolierte Probe hat keine
Buildbinaerdateien im Evidenzordner hinterlassen.

## Unabhaengiger Integrationsreview

Read-only geprueft: Local-Mode Host-/Origin-Pruefung, explizite Trusted Proxies und
Middleware-Reihenfolge, endpointbasierte Repository-ACLs, Frontend-Tokenbegrenzung auf
same-origin API-Aufrufe, authentifizierte Blob-Exports und Repository-Navigation. Im neuen
Diff ergab sich dabei kein weiterer konkreter Befund. Die erlaubte Origin des Dev-Launchers
entspricht der Browser-Origin auch bei umgeschriebenem API-Host und benutzerdefiniertem Port.
Die Hashmetadaten werden mit dem gespeicherten Requestkontext gelesen; neue Review-Requests
vergleichen immer ihren eigenen Kontext.

## Grenzen und offene Pruefpunkte

- Die Manifestvergleiche sind keine immutable Snapshot-Garantie. Eine vor dem naechsten
  Vergleich zurueckgenommene temporaere Aenderung (ABA) kann unentdeckt bleiben.
- Ein Standalone-Staleness-Scan verifiziert aktuelle Repository-Regeln im gespeicherten
  Hostkontext. Ohne neuen Host-Request kann er nicht bestaetigen, dass dessen Richtlinien
  unveraendert sind; beim naechsten Review werden die aktuellen Parameter verglichen.
- Boundary-Findings sind Hinweise fuer die Sicherheitspruefung, keine Exploitnachweise.
  Request-to-system-sink erkennt das gemeinsame Auftreten von Input und Systemoperation,
  keine vollstaendige Taint-Analyse.
- Nur analysierte Report-Kandidaten, unveraendert: `EnabledKinds` filtert Scores/Staleness,
  waehrend Findings alle Kinds enthalten; nichtdateibasierte entfernte Subjects koennen im
  Report verbleiben. Vor Umsetzung den gewuenschten Reportvertrag praezisieren.

Die fachlichen Vertraege und Grenzen stehen in [review-inputs.md](../../docs/review-inputs.md)
und [boundary-inventory.md](../../docs/boundary-inventory.md).
