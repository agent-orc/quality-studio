import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReviewPolicyApi } from '../../../../core/api/review-policy-api';
import { NamedReviewRule, ReviewRuleCatalogue } from '../../../../core/models/review-policy';
import { RuleOverrideEditor } from './rule-override-editor';
import { RulePoolManager, ruleTemplate } from './rule-pool-manager';

function rule(id: string, overrides: Partial<NamedReviewRule> = {}): NamedReviewRule {
  return {
    id, version: '1.0.0', title: `Rule ${id}`, technology: 'dotnet', category: 'async', kinds: ['code'],
    statement: 'Do it.', rationale: 'Because.', detection: 'Look.', goodExample: 'good', badExample: 'bad',
    severity: 'high', authoredSeverity: 'high', enabled: true, defaultOn: true, autofixable: false,
    deterministicRuleIds: [], relatedGuideline: null, since: '1.0.0', origin: 'built-in', selectedBy: ['house-style'],
    ...overrides,
  };
}

function pool(overrides: Partial<ReviewRuleCatalogue> = {}): ReviewRuleCatalogue {
  const empty = { writable: true, location: '.quality/rules', overrides: [], applicability: null, customRules: [], packs: [] };
  return {
    catalogueVersion: '1.5.0', sources: ['built-in', 'project'], valid: true, diagnostics: [],
    rules: [rule('QS-CS-003'), rule('QS-CS-004', { enabled: false, severity: 'info', authoredSeverity: 'low' })],
    traces: [],
    applicability: { scope: 'default', packs: ['house-style'], reason: null },
    packs: [
      { id: 'house-style', version: '1.0.0', title: 'Quality Studio house style', description: 'Defaults.', projectTypes: ['any'],
        include: [{ defaultOn: true }], origin: 'built-in', ruleIds: ['QS-CS-003'], selected: true },
      { id: 'dotnet-service', version: '1.0.0', title: '.NET service or API', description: 'Services.', projectTypes: ['dotnet-api'],
        include: [{ technologies: ['dotnet', 'generic'], defaultOn: true }], origin: 'built-in', ruleIds: ['QS-CS-003'], selected: false },
    ],
    scopes: {
      project: { ...empty, overrides: [{ id: 'QS-CS-004', enabled: false, severity: 'info', reason: 'No tests yet.' }],
        customRules: [{ id: 'TEAM-GN-001', fileName: 'TEAM-GN-001.md', content: ruleTemplate('TEAM-GN-001', '2026-09-28') }] },
      global: { ...empty, writable: false, location: 'data root: rules' },
      sharedGlobal: { ...empty, writable: false, location: 'global inputs: rule-overrides.json' },
    },
    ...overrides,
  };
}

function click(fixture: ComponentFixture<unknown>, label: string): void {
  const button = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'))
    .find(candidate => candidate.textContent?.trim().startsWith(label));
  if (!button) throw new Error(`No button labelled '${label}'.`);
  button.click();
  fixture.detectChanges();
}

function type(fixture: ComponentFixture<unknown>, selector: string, value: string, index = 0): void {
  const field = (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLInputElement | HTMLTextAreaElement>(selector)[index];
  field.value = value;
  field.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

describe('RulePoolManager', () => {
  let fixture: ComponentFixture<RulePoolManager>;
  let http: HttpTestingController;
  let policy: ReviewPolicyApi;

  beforeEach(async () => {
    localStorage.removeItem('qs-last-repository');
    await TestBed.configureTestingModule({
      imports: [RulePoolManager],
      providers: [ReviewPolicyApi, provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    policy = TestBed.inject(ReviewPolicyApi);
    http = TestBed.inject(HttpTestingController);
    policy.catalogue.set(pool());
    fixture = TestBed.createComponent(RulePoolManager);
    fixture.detectChanges();
  });
  afterEach(() => http.verify());

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function answerInputs(): void {
    http.expectOne('/api/repos/default/inputs').flush({ level: 'file', kinds: {} });
  }

  it('summarises applicability and offers global writes only to permitted credentials', () => {
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Quality Studio house style');
    expect(text).toContain('shipped default');
    expect(text).toContain('1 of 2 rules enabled');
    const global = fixture.nativeElement.querySelector('option[value="global"]') as HTMLOptionElement;
    expect(global.disabled).toBeTrue();
    fixture.componentInstance.setScope('global');
    expect(policy.editScope()).toBe('project');
  });

  it('blocks nothing silently: configuration problems are shown as an alert', () => {
    policy.catalogue.set(pool({ valid: false, diagnostics: [
      { scope: 'project', source: '.quality/rules/overrides.json', subject: 'QS-CS-999', message: "references unknown rule id 'QS-CS-999'." },
    ] }));
    fixture.detectChanges();
    const alert = fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('Reviews of this repository are blocked');
    expect(alert.textContent).toContain('.quality/rules/overrides.json');
    expect(alert.textContent).toContain('QS-CS-999');
  });

  it('saves the selected packs with a reason as this repository\'s applicability', async () => {
    click(fixture, 'Packs & applicability');
    const boxes = fixture.nativeElement.querySelectorAll('input[type="checkbox"]') as NodeListOf<HTMLInputElement>;
    expect(boxes[0].checked).toBeTrue();
    boxes[0].click();
    boxes[1].click();
    fixture.detectChanges();
    const save = Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>)
      .find(button => button.textContent!.startsWith('Save applicability'))!;
    expect(save.disabled).toBeTrue();
    type(fixture, '.pool-panel input[type="text"]', 'A .NET API.');
    click(fixture, 'Save applicability');

    const request = http.expectOne(candidate => candidate.url === '/api/repos/default/rules/applicability');
    expect(request.request.method).toBe('PUT');
    expect(request.request.params.get('scope')).toBe('project');
    expect(request.request.body).toEqual({ packs: ['dotnet-service'], reason: 'A .NET API.' });
    request.flush(pool({ applicability: { scope: 'project', packs: ['dotnet-service'], reason: 'A .NET API.' } }));
    await Promise.resolve();
    answerInputs();
    await settle();
    expect(fixture.nativeElement.textContent).toContain('Applicability saved.');
    expect(fixture.nativeElement.textContent).toContain('.NET service or API');
  });

  it('validates a new custom rule before it can be saved and saves it under the validated id', async () => {
    click(fixture, 'Custom rules');
    expect(fixture.nativeElement.textContent).toContain('TEAM-GN-001');
    click(fixture, '+ New custom rule');
    const textarea = fixture.nativeElement.querySelector('textarea') as HTMLTextAreaElement;
    expect(textarea.value).toContain('id: TEAM-GN-001');
    type(fixture, 'textarea', textarea.value.replace('TEAM-GN-001', 'TEAM-GN-002'));
    type(fixture, '.rule-draft input[type="text"]', 'Adopt the documentation rule.');
    const save = () => Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>)
      .find(button => button.textContent!.startsWith('Save custom rule'))!;
    expect(save().disabled).toBeTrue();

    click(fixture, 'Validate');
    const validation = http.expectOne(candidate => candidate.url === '/api/repos/default/rules/custom/validate');
    expect(validation.request.body.content).toContain('id: TEAM-GN-002');
    validation.flush({ valid: true, id: 'TEAM-GN-002', rule: rule('TEAM-GN-002', { origin: 'project' }), diagnostics: [] });
    await settle();
    expect(fixture.nativeElement.textContent).toContain('Valid:');
    expect(save().disabled).toBeFalse();

    save().click();
    const put = http.expectOne(candidate => candidate.url === '/api/repos/default/rules/custom/TEAM-GN-002');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body.reason).toBe('Adopt the documentation rule.');
    put.flush(pool());
    await Promise.resolve();
    answerInputs();
    await settle();
    expect(fixture.nativeElement.textContent).toContain('Custom rule TEAM-GN-002 saved.');
    expect(fixture.nativeElement.querySelector('textarea')).toBeNull();
  });

  it('shows validation diagnostics for an invalid custom rule', async () => {
    click(fixture, 'Custom rules');
    click(fixture, '+ New custom rule');
    click(fixture, 'Validate');
    http.expectOne(candidate => candidate.url === '/api/repos/default/rules/custom/validate').flush({
      valid: false, id: 'TEAM-GN-001', rule: null,
      diagnostics: [{ scope: 'project', source: 'custom rule', subject: 'TEAM-GN-001', message: "id 'TEAM-GN-001' is already defined by the project rule pool." }],
    });
    await settle();
    const alert = fixture.nativeElement.querySelector('.rule-draft [role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('already defined');
  });

  it('previews an import before it can be applied and reports the plan', async () => {
    click(fixture, 'Import / export');
    const component = fixture.componentInstance;
    const file = new File([JSON.stringify({ schemaVersion: 1, overrides: [], customRules: [], packs: [] })], 'team.json',
      { type: 'application/json' });
    await component.readImportFile({ item: () => file, length: 1 } as unknown as FileList);
    fixture.detectChanges();
    component.setImportMode('replace');
    type(fixture, '.pool-panel input[type="text"]', 'Adopt the team rule set.');
    const apply = () => Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>)
      .find(button => button.textContent!.startsWith('Apply import'))!;
    expect(apply().disabled).toBeTrue();

    click(fixture, 'Preview import');
    const preview = http.expectOne(candidate => candidate.url === '/api/repos/default/rules/import');
    expect(preview.request.body).toEqual(jasmine.objectContaining({ mode: 'replace', dryRun: true }));
    preview.flush({ valid: true, applied: false, modifiedSinceExport: true, diagnostics: [], pool: pool(),
      changes: [{ kind: 'custom-rule', id: 'TEAM-GN-001', change: 'removed' }, { kind: 'override', id: 'QS-CS-004', change: 'unchanged' }] });
    await settle();
    const plan = fixture.nativeElement.querySelector('.import-plan') as HTMLElement;
    expect(plan.textContent).toContain('Ready to apply');
    expect(plan.textContent).toContain('1 changes');
    expect(plan.textContent).toContain('edited since it was exported');
    expect(plan.textContent).toContain('removed custom-rule TEAM-GN-001');
    expect(apply().disabled).toBeFalse();

    apply().click();
    const applied = http.expectOne(candidate => candidate.url === '/api/repos/default/rules/import');
    expect(applied.request.body).toEqual(jasmine.objectContaining({ dryRun: false, reason: 'Adopt the team rule set.' }));
    applied.flush({ valid: true, applied: true, modifiedSinceExport: true, diagnostics: [], pool: pool(), changes: [] });
    await Promise.resolve();
    answerInputs();
    await settle();
    expect(fixture.nativeElement.textContent).toContain('Imported team.json into this repository.');
  });

  it('loads the audit trail of the selected scope', async () => {
    click(fixture, 'Audit trail');
    const request = http.expectOne(candidate => candidate.url === '/api/repos/default/rules/audit');
    expect(request.request.params.get('scope')).toBe('project');
    request.flush({ scope: 'project', entries: [
      { schemaVersion: 1, at: '2026-09-28T10:00:00Z', actor: 'local-development', scope: 'project', action: 'override.set', target: 'QS-CS-004', reason: 'No tests yet.' },
    ] });
    await settle();
    const list = fixture.nativeElement.querySelector('.audit-list') as HTMLElement;
    expect(list.textContent).toContain('override.set');
    expect(list.textContent).toContain('by local-development');
    expect(list.textContent).toContain('No tests yet.');
  });
});

@Component({
  imports: [RuleOverrideEditor],
  template: '<qs-rule-override-editor [rule]="rule" />',
})
class EditorHost {
  rule = rule('QS-CS-004', { enabled: false, severity: 'info', authoredSeverity: 'low' });
}

describe('RuleOverrideEditor', () => {
  let fixture: ComponentFixture<EditorHost>;
  let http: HttpTestingController;
  let policy: ReviewPolicyApi;

  beforeEach(async () => {
    localStorage.removeItem('qs-last-repository');
    await TestBed.configureTestingModule({
      imports: [EditorHost],
      providers: [ReviewPolicyApi, provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    policy = TestBed.inject(ReviewPolicyApi);
    http = TestBed.inject(HttpTestingController);
    policy.catalogue.set(pool());
    fixture = TestBed.createComponent(EditorHost);
    fixture.detectChanges();
  });
  afterEach(() => http.verify());

  it('starts from the scope\'s existing override and updates it with a reason', async () => {
    const selects = fixture.nativeElement.querySelectorAll('select') as NodeListOf<HTMLSelectElement>;
    expect(selects[0].value).toBe('disabled');
    expect(selects[1].value).toBe('info');
    expect((fixture.nativeElement.querySelector('input') as HTMLInputElement).value).toBe('No tests yet.');
    selects[1].value = 'medium';
    selects[1].dispatchEvent(new Event('change'));
    fixture.detectChanges();
    click(fixture, 'Update override');

    const request = http.expectOne(candidate => candidate.url === '/api/repos/default/rules/overrides/QS-CS-004');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ enabled: false, severity: 'medium', reason: 'No tests yet.' });
    request.flush(pool());
    await Promise.resolve();
    http.expectOne('/api/repos/default/inputs').flush({ level: 'file', kinds: {} });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Override saved.');
  });

  it('removes an override and shows a rejected change with its diagnostics', async () => {
    click(fixture, 'Remove override');
    const request = http.expectOne(candidate => candidate.url === '/api/repos/default/rules/overrides/QS-CS-004');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.params.get('reason')).toBe('No tests yet.');
    request.flush({ title: 'Rule configuration change rejected', detail: 'The change would make the rule pool invalid.',
      diagnostics: [{ scope: 'project', source: '.quality/rules/applicability.json', subject: null, message: "selects unknown pack 'x'." }] },
      { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();
    fixture.detectChanges();
    const alert = fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('The change would make the rule pool invalid.');
    expect(alert.textContent).toContain(".quality/rules/applicability.json selects unknown pack 'x'.");
  });

  it('is read-only for a global scope the credentials may not change', () => {
    policy.editScope.set('global');
    fixture.detectChanges();
    expect((fixture.nativeElement.querySelector('fieldset') as HTMLFieldSetElement).disabled).toBeTrue();
    expect(fixture.nativeElement.textContent).toContain('may not change the global rule pool');
  });
});
