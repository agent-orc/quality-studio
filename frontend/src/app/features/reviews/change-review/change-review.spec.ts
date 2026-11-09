import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ChangeReviewRange, ChangeReviewStoredRecord, ChangeReviewVerdict } from '../../../core/models/contracts';
import { ChangeRoute } from '../../../core/navigation/change-route';
import { ChangeReviewView } from './change-review';

const BASE = 'a'.repeat(40);
const HEAD = 'b'.repeat(40);
const PREVIOUS = 'c'.repeat(40);
const RANGE_URL = `/api/v1/repos/proj-002/change-reviews?base=${BASE}&head=${HEAD}`;

function record(verdict: ChangeReviewVerdict, overrides: Partial<ChangeReviewStoredRecord> = {}): ChangeReviewStoredRecord {
  const decisive = ['pass', 'concerns', 'block'].includes(verdict);
  return {
    key: {
      repository: 'proj-002', baseSha: BASE, headSha: HEAD, policyHash: 'sha256:' + '1'.repeat(64),
      ruleSetHash: 'sha256:' + '1a2b9c'.padEnd(64, '0'), catalogueVersion: '1.6.0', digest: 'sha256:' + 'd'.repeat(64),
    },
    keyDigest: 'sha256:' + 'd'.repeat(64),
    provenance: { producer: 'agent-studio-review-plane', cardKey: 'AGT-3352', round: 2, attemptId: 'attempt-7', clientId: 'agent-studio' },
    recordedAt: '2026-11-01T14:32:00Z',
    documentDigest: 'sha256:' + 'e'.repeat(64),
    current: true,
    decisive,
    document: {
      schema: 'https://agent-orchestrator.dev/quality/schemas/change-review-verdict.v1.schema.json', schemaVersion: 1,
      repository: 'proj-002', baseSha: BASE, headSha: HEAD, verdict, policyHash: 'sha256:' + '1'.repeat(64),
      ruleSetHash: 'sha256:' + '1a2b9c'.padEnd(64, '0'), catalogueVersion: '1.6.0',
      failureReason: decisive ? null : 'codex exited before answering',
      model: 'gpt-5.5',
      findings: verdict === 'concerns' || verdict === 'block' ? [
        {
          id: 'finding-' + 'f'.repeat(64), fingerprint: 'sha256:' + 'f'.repeat(64), ruleId: 'QS-NET-004', ruleVersion: '1.0.0',
          severity: 'high', path: 'backend/Export.cs', side: 'head', line: 88, message: 'File name reaches Path.Combine without a root check.',
          evidenceChecked: 'The added Path.Combine call', missing: 'A root confinement check', disposition: 'block',
        },
        {
          id: 'finding-' + '9'.repeat(64), fingerprint: 'sha256:' + '9'.repeat(64), ruleId: 'QS-NG-011', ruleVersion: '1.0.0',
          severity: 'medium', path: 'frontend/export.ts', side: 'head', line: 41, message: 'Subscription without teardown.',
          evidenceChecked: 'The added subscribe call', missing: 'takeUntilDestroyed', disposition: 'concerns',
        },
      ] : [],
    },
    ...overrides,
  };
}

function range(overrides: Partial<ChangeReviewRange> = {}): ChangeReviewRange {
  return {
    repository: 'proj-002', baseSha: BASE, headSha: HEAD,
    commits: { base: true, head: true, previous: null },
    currentKey: null,
    permissions: { runReview: true },
    records: [],
    excerpts: {},
    roundOnly: null,
    ...overrides,
  };
}

describe('ChangeReviewView', () => {
  let fixture: ComponentFixture<ChangeReviewView>;
  let http: HttpTestingController;

  async function open(route: Partial<ChangeRoute> = {}, url = RANGE_URL, response?: ChangeReviewRange): Promise<HTMLElement> {
    fixture.componentRef.setInput('repositoryId', 'proj-002');
    fixture.componentRef.setInput('route', { base: BASE, head: HEAD, card: 'AGT-3352', previous: null, valid: true, ...route });
    fixture.detectChanges();
    if (response) {
      http.expectOne(url).flush(response);
      await fixture.whenStable();
      fixture.detectChanges();
    }
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ChangeReviewView],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(ChangeReviewView);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('renders a stored verdict with severity counts, findings anchored to the diff and provenance', async () => {
    const element = await open({}, RANGE_URL, range({
      records: [record('block')],
      excerpts: {
        ['sha256:' + 'f'.repeat(64)]: [
          { kind: 'context', baseLine: 86, headLine: 87, text: 'var root = options.Root;', anchor: false },
          { kind: 'added', baseLine: null, headLine: 88, text: 'var path = Path.Combine(root, name);', anchor: true },
        ],
      },
    }));

    expect(element.querySelector('h1')?.textContent).toContain('AGT-3352');
    expect(element.querySelector('[data-verdict-state="critical"]')).not.toBeNull();
    expect(element.querySelector('.verdict-badge')?.textContent?.trim()).toBe('Block');
    const counts = element.querySelector('.severity-strip')?.textContent ?? '';
    expect(counts).toContain('1 high');
    expect(counts).toContain('1 medium');
    expect(counts).toContain('0 critical');
    expect(element.querySelectorAll('.change-finding').length).toBe(2);
    expect(element.textContent).toContain('backend/Export.cs:88');
    const anchor = element.querySelector('.diff-line.anchor');
    expect(anchor?.textContent).toContain('+var path = Path.Combine(root, name);');
    const provenance = element.querySelector('.provenance')?.textContent ?? '';
    expect(provenance).toContain('Agent Studio Review Plane');
    expect(provenance).toContain('AGT-3352');
    expect(provenance).toContain('attempt-7');
    expect(provenance).toContain('1a2b9c');
    expect(element.querySelector('.run-review')).withContext('a decided range is never offered for a second review').toBeNull();
  });

  it('shows "Not reviewed" and an operator "Run review" that stores and reloads the verdict', async () => {
    const element = await open({}, RANGE_URL, range());

    expect(element.querySelector('[data-change-state="not-reviewed"]')?.textContent).toContain('Not reviewed');
    const button = Array.from(element.querySelectorAll('button')).find(candidate => candidate.textContent?.includes('Run review'));
    expect(button).toBeDefined();

    button!.click();
    const post = http.expectOne('/api/repos/proj-002/change-review');
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual({ baseSha: BASE, headSha: HEAD });
    post.flush(record('pass').document);
    await fixture.whenStable();
    http.expectOne(RANGE_URL).flush(range({ records: [record('pass')] }));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(element.querySelector('.verdict-badge')?.textContent?.trim()).toBe('Pass');
    expect(element.querySelector('[data-change-state="not-reviewed"]')).toBeNull();
  });

  it('withholds "Run review" from a caller without the spend permission', async () => {
    const element = await open({}, RANGE_URL, range({ permissions: { runReview: false } }));

    expect(element.querySelector('[data-change-state="not-reviewed"]')).not.toBeNull();
    expect(element.querySelector('.run-review')).toBeNull();
  });

  it('says a commit is not present on this host and offers no review', async () => {
    const element = await open({}, RANGE_URL, range({ commits: { base: true, head: false, previous: null } }));

    const state = element.querySelector('[data-change-state="missing-commit"]');
    expect(state?.textContent).toContain('Commit not present on this host');
    expect(state?.textContent).toContain(HEAD);
    expect(element.querySelector('.run-review')).toBeNull();
  });

  for (const verdict of ['provider-unavailable', 'timeout', 'auth-failure', 'unparseable'] as const) {
    it(`renders the infrastructure verdict ${verdict} as unavailable, never as pass`, async () => {
      const element = await open({}, RANGE_URL, range({ records: [record(verdict)] }));

      expect(element.querySelector('[data-verdict-state="unavailable"]')).not.toBeNull();
      expect(element.querySelector('.verdict-badge')?.textContent?.trim()).toBe('Unavailable');
      expect(element.querySelector('.verdict-badge')?.getAttribute('data-tone')).toBeNull();
      expect(element.querySelector('[data-verdict-state="positive"]')).toBeNull();
      expect(element.textContent).not.toMatch(/\bPass\b/);
      expect(element.textContent).toContain('codex exited before answering');
      expect(element.querySelector('.run-review')).withContext('an unavailable verdict can be retried').not.toBeNull();
    });
  }

  it('narrows the findings to this round when a previous head is given', async () => {
    const url = `${RANGE_URL}&previous=${PREVIOUS}`;
    const element = await open({ previous: PREVIOUS }, url, range({
      commits: { base: true, head: true, previous: true },
      records: [record('block')],
      roundOnly: { previousHeadSha: PREVIOUS, available: true, fingerprints: ['sha256:' + '9'.repeat(64)] },
    }));
    expect(element.querySelectorAll('.change-finding').length).toBe(2);

    const toggle = element.querySelector<HTMLInputElement>('.round-toggle input')!;
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(element.querySelectorAll('.change-finding').length).toBe(1);
    expect(element.textContent).toContain('QS-NG-011');
    expect(element.querySelector('.severity-strip')?.textContent).toContain('0 high');
  });

  it('refuses a link without full commit SHAs without calling the API', async () => {
    const element = await open({ base: 'main', valid: false });

    expect(element.querySelector('[data-change-state="invalid"]')).not.toBeNull();
    http.expectNone(() => true);
  });
});
