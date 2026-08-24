import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  AttackCatalogueEntry, AttackCoverageCell, AttackCoverageMatrix, AttackCoverageRow, QualityApi, TreeNode,
} from '../quality-api';
import { AttackCoverage } from './attack-coverage';

function treeNode(id: string, path: string, children: TreeNode[] = []): TreeNode {
  return {
    id, name: path.split('/').at(-1) ?? id, level: children.length ? 'folder' : 'file', path,
    kinds: { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: null, band: null, metaPath: null } },
    children,
  };
}

function attack(id: string, boundaryKinds: string[]): AttackCatalogueEntry {
  return {
    id, version: '1', title: `${id} title`, description: `${id} description`,
    applicability: { boundaryKinds, directions: null }, evidenceRequirements: ['code'],
    severity: 'high', severityFrame: `${id} frame`, deterministicRuleIds: [],
    deterministicPassConclusive: false, enabled: true,
  };
}

function cell(boundaryId: string, attackId: string, overrides: Partial<AttackCoverageCell>): AttackCoverageCell {
  return {
    boundaryId, attackId, verdict: 'pass', reason: `${attackId} reason`, evidence: [],
    findingId: null, findingFingerprint: null, disagreement: false, deterministicOverride: false,
    needsHumanAttention: false, requiredJudgements: 2, independentJudgements: 2, confidence: 'high',
    checkedAt: '2026-08-10T09:00:00Z', ageDays: 12.7, stalenessReasons: [], provenance: [], history: [],
    ...overrides,
  };
}

function row(id: string, name: string, cells: AttackCoverageCell[], codeChangeCount = 0): AttackCoverageRow {
  return {
    boundary: { id, kind: 'httpEndpoint', direction: 'inbound', name, transport: 'http', location: { path: `src/${name}.cs`, line: 12 } },
    boundaryDefinitionHash: `sha256:${id}`, coveredCodeHash: `sha256:${id}-code`, codeChangeCount,
    oldestVerdictAt: '2026-08-10T09:00:00Z', cells,
  };
}

const passCell = cell('files', 'A1', {});
const attentionCell = cell('files', 'A2', {
  verdict: 'finding', ageDays: 0.4, confidence: 'medium', needsHumanAttention: true, disagreement: true,
  stalenessReasons: ['codeChanged'], findingId: 'finding-42', independentJudgements: 1,
});
const uncheckedCell = cell('health', 'A1', {
  verdict: 'notYetChecked', ageDays: null, checkedAt: null, confidence: 'low', independentJudgements: 0,
});

function buildMatrix(overrides: Partial<AttackCoverageMatrix> = {}): AttackCoverageMatrix {
  return {
    schemaVersion: 1, catalogueVersion: '2026.08', promptVersion: 'p7', promptHash: 'sha256:prompt',
    generatedAt: '2026-08-18T10:00:00Z', scope: 'src/QualityStudio.Api',
    attacks: [attack('A1', ['httpEndpoint']), attack('A2', ['httpEndpoint'])],
    rows: [row('files', 'FilesEndpoint', [passCell, attentionCell], 3), row('health', 'HealthEndpoint', [uncheckedCell])],
    cellCount: 3, notYetCheckedCount: 1, staleCount: 1, disagreementCount: 1,
    ...overrides,
  };
}

describe('AttackCoverage', () => {
  let fixture: ComponentFixture<AttackCoverage>;
  let api: QualityApi;
  let http: HttpTestingController;

  const apiTree = [treeNode('root', '.', [treeNode('src', 'src', [treeNode('api', 'src/QualityStudio.Api', [treeNode('program', 'src/QualityStudio.Api/Program.cs')])])])];
  const noApiTree = [treeNode('root', '.', [treeNode('src', 'src', [treeNode('core', 'src/AgentOrchestrator.CodeQuality')])])];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AttackCoverage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    api = TestBed.inject(QualityApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function start(tree: TreeNode[]): void {
    api.tree.set(tree);
    fixture = TestBed.createComponent(AttackCoverage);
    fixture.detectChanges();
  }

  function pendingRequest() {
    return http.expectOne(request => request.url.endsWith('/security/attack-coverage'));
  }

  async function load(tree: TreeNode[], matrix: AttackCoverageMatrix = buildMatrix()): Promise<void> {
    start(tree);
    pendingRequest().flush(matrix);
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('scopes the probe to the API project when the tree exposes it at any depth', () => {
    start(apiTree);
    const request = pendingRequest();

    expect(request.request.params.get('path')).toBe('src/QualityStudio.Api');
    expect(fixture.componentInstance.scope()).toBe('src/QualityStudio.Api');
    expect(fixture.nativeElement.querySelector('#coverage-dialog-title').textContent)
      .toContain('src/QualityStudio.Api');
    request.flush(buildMatrix());
  });

  it('falls back to the repository root when no API project is in the tree', () => {
    start(noApiTree);
    const request = pendingRequest();

    expect(request.request.params.get('path')).toBe('.');
    expect(fixture.componentInstance.scope()).toBe('.');
    request.flush(buildMatrix());
  });

  it('falls back to the repository root when the tree has not loaded yet', () => {
    start([]);
    const request = pendingRequest();

    expect(request.request.params.get('path')).toBe('.');
    request.flush(buildMatrix());
  });

  it('opens on the cell needing human attention ahead of the first cell', async () => {
    await load(apiTree);

    expect(fixture.componentInstance.selectedCell()).toBe(attentionCell);
    const detail = fixture.nativeElement.querySelector('.coverage-detail');
    expect(detail.querySelector('.eyebrow').textContent).toContain('A2');
    expect(detail.querySelector('h3').textContent).toContain('finding');
    expect(detail.textContent).toContain('finding-42');
    expect(detail.textContent).toContain('codeChanged');
  });

  it('opens on the first cell of the first row when nothing needs attention', async () => {
    await load(apiTree, buildMatrix({
      rows: [row('files', 'FilesEndpoint', [passCell, { ...attentionCell, needsHumanAttention: false }])],
    }));

    expect(fixture.componentInstance.selectedCell()).toBe(passCell);
  });

  it('leaves no cell selected when the matrix has no rows', async () => {
    await load(apiTree, buildMatrix({ rows: [], cellCount: 0, notYetCheckedCount: 0, staleCount: 0, disagreementCount: 0 }));

    expect(fixture.componentInstance.selectedCell()).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.coverage-table tbody tr').length).toBe(0);
    expect(fixture.nativeElement.querySelector('.coverage-detail')).toBeNull();
  });

  it('renders a row per boundary and a column per catalogue attack, marking inapplicable pairs', async () => {
    await load(apiTree);

    const headers = fixture.nativeElement.querySelectorAll('.coverage-table thead th');
    const rows = fixture.nativeElement.querySelectorAll('.coverage-table tbody tr');
    expect(headers.length).toBe(3);
    expect(headers[1].textContent).toContain('A1');
    expect(rows.length).toBe(2);
    expect(rows[0].querySelector('.coverage-boundary-column').textContent).toContain('FilesEndpoint');
    expect(rows[0].querySelector('.change-count').textContent).toContain('3 code changes');
    expect(rows[1].querySelector('.change-count')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.coverage-cell').length).toBe(3);
    expect(fixture.nativeElement.querySelectorAll('.coverage-inapplicable').length).toBe(1);
    expect(fixture.nativeElement.querySelector('.coverage-summary span').textContent.replace(/\s+/g, ' ').trim())
      .toBe('3 applicable cells');
    expect(fixture.nativeElement.querySelector('.coverage-summary-deferred').textContent).toContain('1');
    expect(fixture.nativeElement.querySelector('.coverage-summary-version').textContent)
      .toContain('catalogue 2026.08');
  });

  it('renders each verdict with its glyph, age wording, and stale or disagreement flags', async () => {
    await load(apiTree);

    const cells = Array.from(fixture.nativeElement.querySelectorAll('.coverage-cell')) as HTMLElement[];
    expect(cells.map(button => button.querySelector('.cell-verdict')!.textContent!.trim())).toEqual(['✓', '!', '—']);
    expect(cells.map(button => button.querySelector('.cell-overlay')!.textContent!.trim()))
      .toEqual(['12d · high', '<1d · medium', 'unchecked · low']);
    expect(cells[0].classList).toContain('verdict-pass');
    expect(cells[1].classList).toContain('stale-cell');
    expect(cells[1].classList).toContain('disagreement-cell');
    expect(cells[0].classList).not.toContain('stale-cell');
    expect(cells[1].getAttribute('aria-label')).toBe('FilesEndpoint, A2 title: finding');
  });

  it('selects the clicked cell and lets the detail pane be dismissed', async () => {
    await load(apiTree);

    (fixture.nativeElement.querySelectorAll('.coverage-cell')[2] as HTMLElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.selectedCell()).toBe(uncheckedCell);
    expect(fixture.nativeElement.querySelector('.coverage-detail').textContent).toContain('No verdict evidence yet');
    expect(fixture.nativeElement.querySelector('.coverage-detail').textContent).toContain('No checks have been recorded');

    (fixture.nativeElement.querySelector('.coverage-detail .icon-button') as HTMLElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.selectedCell()).toBeNull();
    expect(fixture.nativeElement.querySelector('.coverage-detail')).toBeNull();
  });

  it('lists evidence and replays the trajectory newest first with each judgement', async () => {
    const judgement = {
      schemaVersion: 1, assessmentId: 'run-2', boundaryId: 'files', attackId: 'A1', verdict: 'pass' as const,
      reasoning: 'Path is canonicalised.', evidence: [], deterministicSensorInput: [], findingId: null,
      findingFingerprint: null, source: 'agent' as const,
      reviewer: { agent: 'security-reviewer', model: 'gpt-5', thinkingLevel: 'high' },
      promptVersion: 'p7', promptHash: 'sha256:prompt', catalogueVersion: '2026.08', catalogueEntryHash: 'sha256:entry',
      boundaryDefinitionHash: 'sha256:files', coveredCodeHash: 'sha256:files-code',
      tokenCost: { inputTokens: 900, outputTokens: 100, cachedInputTokens: 0, reasoningOutputTokens: 0, totalTokens: 1000 },
      checkedAt: '2026-08-10T09:00:00Z', commit: 'abc1234', commitRange: null,
    };
    const detailed = cell('files', 'A1', {
      evidence: [{ kind: 'code', reference: 'src/Files.cs:42', summary: 'Traversal guard present.' }],
      history: [
        { assessmentId: 'run-1', checkedAt: '2026-08-01T09:00:00Z', verdict: 'notYetChecked', disagreement: false, judgements: [], commit: null, commitRange: null },
        { assessmentId: 'run-2', checkedAt: '2026-08-10T09:00:00Z', verdict: 'pass', disagreement: false, judgements: [judgement], commit: 'abc1234', commitRange: null },
      ],
    });
    await load(apiTree, buildMatrix({ rows: [row('files', 'FilesEndpoint', [detailed])], attacks: [attack('A1', ['httpEndpoint'])], cellCount: 1 }));

    const detail = fixture.nativeElement.querySelector('.coverage-detail');
    expect(detail.querySelector('.coverage-evidence').textContent).toContain('src/Files.cs:42');
    expect(detail.querySelector('.coverage-evidence').textContent).toContain('Traversal guard present.');
    const history = Array.from(detail.querySelectorAll('.coverage-history')) as HTMLElement[];
    expect(history.length).toBe(2);
    expect(history[0].textContent).toContain('abc1234');
    expect(history[0].textContent).toContain('1 judgement(s)');
    expect(history[0].textContent).toContain('security-reviewer');
    expect(history[0].textContent).toContain('1000 tokens');
    expect(history[1].textContent).toContain('uncommitted');
    expect(history[1].textContent).toContain('0 judgement(s)');
  });

  it('emits close from the backdrop and from the close button but not from the dialog body', async () => {
    await load(apiTree);
    const closed: number[] = [];
    fixture.componentInstance.close.subscribe(() => closed.push(closed.length));

    (fixture.nativeElement.querySelector('.coverage-dialog') as HTMLElement).click();
    expect(closed.length).toBe(0);

    (fixture.nativeElement.querySelector('.coverage-header-actions .icon-button') as HTMLElement).click();
    (fixture.nativeElement.querySelector('.coverage-backdrop') as HTMLElement).click();
    expect(closed.length).toBe(2);
  });

  it('shows the derivation notice while loading and swaps it for the matrix once it lands', async () => {
    const revoke = spyOn(URL, 'revokeObjectURL');
    const create = spyOn(URL, 'createObjectURL');
    start(apiTree);

    const exportButton = fixture.nativeElement.querySelector('.coverage-header-actions .secondary-button') as HTMLButtonElement;
    expect(fixture.nativeElement.querySelector('.coverage-loading')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.coverage-table')).toBeNull();
    expect(exportButton.disabled).toBeTrue();
    fixture.componentInstance.export();
    expect(create).not.toHaveBeenCalled();
    expect(revoke).not.toHaveBeenCalled();

    pendingRequest().flush(buildMatrix());
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.coverage-loading')).toBeNull();
    expect(fixture.nativeElement.querySelector('.coverage-table')).not.toBeNull();
    expect(exportButton.disabled).toBeFalse();
  });

  it('keeps the dialog usable and reports the API error when the probe fails', async () => {
    start(apiTree);
    pendingRequest().flush('scope is not indexed', { status: 502, statusText: 'Bad Gateway' });
    await fixture.whenStable();
    fixture.detectChanges();

    const error = fixture.nativeElement.querySelector('.coverage-error');
    expect(error.getAttribute('role')).toBe('alert');
    expect(error.textContent.trim()).toBe(api.attackCoverageError());
    expect(error.textContent.length).toBeGreaterThan(0);
    expect(fixture.nativeElement.querySelector('.coverage-table')).toBeNull();
    expect(fixture.componentInstance.selectedCell()).toBeNull();
    expect((fixture.nativeElement.querySelector('.coverage-header-actions .secondary-button') as HTMLButtonElement).disabled).toBeTrue();
    expect(fixture.nativeElement.querySelector('.coverage-header-actions .icon-button')).not.toBeNull();
  });

  it('exports the loaded matrix as a JSON blob named for the selected repository', async () => {
    api.selectedRepositoryId.set('payments');
    await load(apiTree);
    const click = spyOn(HTMLAnchorElement.prototype, 'click');
    const create = spyOn(URL, 'createObjectURL').and.returnValue('blob:attack-coverage');
    const revoke = spyOn(URL, 'revokeObjectURL');

    (fixture.nativeElement.querySelector('.coverage-header-actions .secondary-button') as HTMLElement).click();

    expect(click).toHaveBeenCalledTimes(1);
    const anchor = click.calls.mostRecent().object as HTMLAnchorElement;
    expect(anchor.download).toBe('attack-coverage-payments.json');
    expect(anchor.getAttribute('href')).toBe('blob:attack-coverage');
    expect(revoke).toHaveBeenCalledWith('blob:attack-coverage');

    const blob = create.calls.mostRecent().args[0] as Blob;
    expect(blob.type).toBe('application/json');
    expect(JSON.parse(await blob.text())).toEqual(buildMatrix());
  });
});
