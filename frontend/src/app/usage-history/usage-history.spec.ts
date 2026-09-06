import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { QualityApi } from '../quality-api';
import { UsageReport } from '../contracts';
import { UsageHistory } from './usage-history';

describe('UsageHistory', () => {
  let fixture: ComponentFixture<UsageHistory>;
  let api: QualityApi;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [UsageHistory],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(UsageHistory);
    api = TestBed.inject(QualityApi);
    api.usage.set({
      generatedAt: '2026-07-25T10:00:00Z',
      runs: 2,
      inputTokens: 300,
      outputTokens: 75,
      cachedInputTokens: 120,
      reasoningOutputTokens: 15,
      durationMs: 3000,
      byModel: [{ key: 'gpt-5', runs: 2, inputTokens: 300, outputTokens: 75, cachedInputTokens: 120, reasoningOutputTokens: 15, durationMs: 3000 }],
      byKind: [],
      byDay: [{ key: '2026-07-25', runs: 2, inputTokens: 300, outputTokens: 75, cachedInputTokens: 120, reasoningOutputTokens: 15, durationMs: 3000 }],
      byReviewRun: [{ key: 'review-sweep-1', runs: 2, inputTokens: 300, outputTokens: 75, cachedInputTokens: 120, reasoningOutputTokens: 15, durationMs: 3000 }],
      recent: [{
        runId: 'cli-run-1',
        reviewRunId: 'review-sweep-1',
        timestamp: '2026-07-25T09:59:00Z',
        model: 'gpt-5',
        cliType: 'codex',
        tokens: { inputTokens: 200, outputTokens: 50, cachedInputTokens: 80, reasoningOutputTokens: 10, durationMs: 2000 },
        kind: 'code',
        level: 'file',
        path: 'src/a.ts',
        schemaVersion: 3,
        modelSource: 'policy-default',
        cost: { total: 0.0125, currency: 'USD', status: 'priced' },
      }],
      estimatedCost: 0.0125,
      costCurrency: 'USD',
      unpricedRuns: 1,
    } satisfies UsageReport);
    fixture.detectChanges();
  });

  it('renders totals and exposes recent-entry details through a native button', () => {
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('375');
    expect(element.textContent).toContain('gpt-5');
    const entry = element.querySelector('.entry-summary') as HTMLButtonElement;

    expect(entry.getAttribute('aria-expanded')).toBe('false');
    entry.click();
    fixture.detectChanges();

    expect(entry.getAttribute('aria-expanded')).toBe('true');
    expect(element.textContent).toContain('review-sweep-1');
    expect(element.textContent).toContain('cli-run-1');
  });

  it('closes with Escape for keyboard access', () => {
    let closed = false;
    fixture.componentInstance.closed.subscribe(() => closed = true);
    const dialog = fixture.nativeElement.querySelector('.usage-dialog') as HTMLElement;

    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(closed).toBeTrue();
  });

  it('keeps keyboard focus inside the modal dialog', () => {
    const dialog = fixture.nativeElement.querySelector('.usage-dialog') as HTMLElement;
    const close = dialog.querySelector('[aria-label="Close usage history"]') as HTMLButtonElement;
    const entry = dialog.querySelector('.entry-summary') as HTMLButtonElement;
    entry.focus();

    entry.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(close);

    close.focus();
    close.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'Tab',
      shiftKey: true,
      bubbles: true,
      cancelable: true,
    }));
    expect(document.activeElement).toBe(entry);
  });

  it('reports the ledger total cost with its currency and the unpriced remainder', () => {
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('0.0125 USD');
    expect(element.textContent).toContain('1 operation(s) unpriced');
  });

  it('shows the per-entry cost and how the model was chosen', () => {
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.entry-cost')?.textContent).toContain('0.0125 USD');

    (element.querySelector('.entry-summary') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(element.textContent).toContain('policy default');
    expect(element.textContent).toContain('priced');
  });

  it('states an unpriced entry as unpriced instead of showing a number', () => {
    const usage = api.usage();
    api.usage.set({
      ...usage,
      estimatedCost: null,
      costCurrency: null,
      unpricedRuns: 2,
      recent: [{ ...usage.recent[0], cost: { total: null, currency: null, status: 'unknownModel' } }],
    });
    fixture.detectChanges();

    const cost = fixture.nativeElement.querySelector('.entry-cost') as HTMLElement;
    expect(cost.textContent).toContain('unpriced');
    expect(cost.classList).toContain('unpriced');
    expect(fixture.nativeElement.querySelector('.cost-total strong')?.textContent).toContain('unpriced');
  });
});
