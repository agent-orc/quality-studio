# Git-Prozessreview, 19.09.2026

## Ergebnis und kleiner Fix

Ein konfiguriertes `core.fsmonitor`-Programm wurde bei scheinbar lesenden Produktabfragen
ausgefuehrt. Der isolierte Nachweis mit Git 2.55.0.windows.1 zeigt die Ausfuehrung bei
`status --porcelain` und beiden verwendeten `ls-files`-Varianten. Der Hook schrieb nur
einen Marker in sein Wegwerf-Repository; dieses wurde danach entfernt.
[Probe](git-fsmonitor-probe.json)

Der neue gemeinsame Helfer `ReadOnlyGit.WithSafetyOptions` stellt den Git-Argumenten
`--no-optional-locks -c core.fsmonitor=false` voran. Er ist in neun Produktwrappern
eingebunden: HierarchyCache, HierarchyAdapters, StalenessEvaluator, ProjectDashboard,
Coverage, GitPlumbing/ChangeSets, QualityReport, AttackCoverageService und den direkten
Git-Hilfsabfragen des GitleaksSecurityScanner. Es werden keine Schreibbefehle geaendert,
keine Repository-Konfiguration gespeichert und keine bestehenden Signaturen umgebaut.

Drei echte Regressionen fuer Cache, generischen Adapter und Staleness-Scan scheiterten
zuerst am ausgefuehrten Hook und bestehen nach dem Fix. Jede testet zuerst positiv, dass
Git den harmlosen Hook im Kontrollaufruf wirklich ausfuehrt. Danach bleibt der Marker aus,
der Index bytegleich, ignorierte Dateien bleiben ausgeschlossen und der Cache erkennt
eine echte Quelltextaenderung weiterhin.
[Rot: 0/3](backend-evidence/git-fsmonitor-red.trx),
[Gruen: 3/3](backend-evidence/git-fsmonitor-green.trx),
[betroffene Suites: 102/102](backend-evidence/git-suites-green.trx).

Git dokumentiert die Programmausfuehrung durch
[core.fsmonitor](https://git-scm.com/docs/git-config#Documentation/git-config.txt-corefsmonitor).
Die boolesche Deaktivierung setzt modernes Git voraus (Git 2.36 oder neuer); aeltere
Versionen interpretieren boolesche Werte teilweise als Hookpfade.
[Optionale Locks](https://git-scm.com/docs/git#Documentation/git.txt---no-optional-locks)
koennen unter anderem den Index-Refresh als Nebeneffekt eines Hintergrund-Status verhindern.

## Verbleibende konkrete Prozessbefunde

| Aufruf | Aktueller Vertrag / Risiko |
| --- | --- |
| RepositoryHierarchyCache / Adapters | Synchrones unbefristetes ReadToEnd/WaitForExit; der Cache haelt dabei Repository-Gates. Beide Pipes werden drainiert, stdout wird unbegrenzt gehalten. |
| Coverage.GitValue / ProjectDashboard.RunGit | Ebenfalls synchron, ohne Timeout oder Outputgrenze; nullable Fallback bei bestimmten Fehlern. |
| GitPlumbing / QualityReport | Async und Caller-Cancellation, aber kein eigener Timeout, kein Kill bei Cancel, unbegrenzte Ausgabe. RequireRepository blockiert zusaetzlich synchron. |
| AttackCoverageService.GitAsync | Keine Cancellation/Frist; stderr ist redirected und wird nicht gelesen, daher moegliche Pipeblockade. |
| Coverage-Konvertierung | dotnet-coverage wird synchron ohne Timeout ausgefuehrt; beide Pipes werden drainiert. Separater Nicht-Git-Pfad. |

Der kleine Fix behebt keine dieser Lifecycle-Grenzen. Er verhindert auch keine beliebigen
Git-Erweiterungen: externe Diff-/Textconv-Programme sind eine weitere Grenze
([Git-Dokumentation](https://git-scm.com/docs/git-diff#Documentation/git-diff.txt---no-ext-diff)).
Gitleaks kann im Range-Modus selbst Git starten; die geaenderten eigenen Git-Hilfsabfragen
sind davon getrennt.

## Minimaler Folgeschritt fuer begrenzte Prozesse

Als eigener Folgeschritt einen Git-spezifischen Adapter um die vorhandene
`ProcessSensorCommandRunner`-Mechanik verwenden: gemeinsame Frist, getrennte begrenzte
Pipes, Kill des Prozessbaums bei Timeout/Cancel und keine Rueckgabe abgeschnittener Daten.
Die Git-Schicht muss Fehler in die bisherigen Domainausnahmen/Unavailable-Zustaende
uebersetzen; eine Sensor-Ausnahme darf nicht unbesehen als falscher API-Fehlertitel austreten.

Die synchrone Hierarchy-API zunaechst beibehalten und nur begrenzt warten; danach getrennt
auf Async/Gates umstellen. Das reduziert das erste Diff, gibt jedoch weiterhin einen
Requestthread pro wartender Abfrage nicht frei. Timeouts und Limits brauchen Git-spezifische
Budgets: die Sensorvoreinstellung 5 Minuten und 1 Mio. Zeichen ist fuer interaktive
Hierarchieabfragen beziehungsweise grosse Dateilisten kein belastbarer gemeinsamer Vertrag.
Timeout/Outputueberlauf darf weder einen unvollstaendigen Index als frisch behandeln noch
ueber den Adapter-Fallback unbemerkt einen grossen Dateisystemscan ausloesen.

Passende bestehende Fixtures: `GitTestRepository`, `RepositoryHierarchyToolBoundTests`
(TTL, Unavailable, Invalidierung), `RepositoryHierarchyMetadataCacheTests`,
`RepositoryHierarchyBuilderTests` (.gitignore), `ChangeSetReviewTests` (Index/Worktree
unveraendert), `CoverageSensorTests` (Churn) und `ProcessSensorCommandRunnerTests`
(Output, Timeout, Cancel, Prozessbaum). Letztere haben fuer Timeout/Cancel noch POSIX-only
Faelle; die neue Output-Prozessbaumprobe nutzt Node auch auf Windows.

Performance: Ohne fsmonitor muss Git den Indexzustand selbst pruefen. Die vorhandene
1-Sekunden-TTL bleibt erhalten; grosse Repositorys koennen langsamere kalte Pruefungen zeigen.
Dafuer wird kein fremdes Monitorprogramm gestartet und kein optionales Indexlock angelegt.
Ein vollstaendiger Async-/Lifecycle-Umbau braucht getrennte Leistungs- und Fehlerpfadtests;
er ist bewusst kein Teil dieses eng begrenzten Fixes.
