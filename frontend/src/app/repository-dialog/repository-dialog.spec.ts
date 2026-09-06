import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RepositoryRegistration, RepositoryRegistrationRequest } from '../contracts';
import { RepositoryDialog } from './repository-dialog';

const registered: RepositoryRegistration = {
  id: 'payments', displayName: 'Payments service', rootPath: 'C:\\Projects\\payments',
  globalInputsDirectory: null, inputBudgetCharacters: 12000,
  enabledReviewKinds: ['code', 'security'], archived: false,
  defaultReviewTokenCap: 100_000, defaultReviewCostCap: null,
};

describe('RepositoryDialog', () => {
  let fixture: ComponentFixture<RepositoryDialog>;
  let form: RepositoryRegistrationRequest;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [RepositoryDialog] }).compileComponents();
    fixture = TestBed.createComponent(RepositoryDialog);
    form = {
      displayName: 'Payments service', rootPath: 'C:\\Projects\\payments', globalInputsDirectory: null,
      inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security'],
      defaultReviewTokenCap: 100_000, defaultReviewCostCap: null,
    };
    const dialog = fixture.componentInstance;
    dialog.repositories = [registered];
    dialog.editingRepositoryId = 'payments';
    dialog.editingRepository = registered;
    dialog.form = form;
    dialog.tokenCapText = '100k';
    dialog.reviewKinds = ['code', 'security', 'performance'];
    fixture.detectChanges();
  });

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('titles itself by whether it is onboarding or configuring', () => {
    expect(element().querySelector('h2')?.textContent).toBe('Configure repository');

    fixture.componentInstance.editingRepositoryId = null;
    fixture.detectChanges();
    expect(element().querySelector('h2')?.textContent).toBe('Onboard a repository');
  });

  it('reports a review-kind change with its new state', () => {
    let change: { kind: string; enabled: boolean } | null = null;
    fixture.componentInstance.reviewKindChanged.subscribe(event => change = event);

    const performance = Array.from(element().querySelectorAll<HTMLInputElement>('.kind-option input'))[2];
    expect(performance.checked).toBeFalse();
    performance.checked = true;
    performance.dispatchEvent(new Event('change'));

    expect(change).toEqual({ kind: 'performance', enabled: true } as never);
  });

  it('blocks saving while a token cap is invalid or a required field is empty', () => {
    const save = () => element().querySelector('.primary-button') as HTMLButtonElement;
    expect(save().disabled).toBeFalse();

    fixture.componentInstance.tokenCapError = 'Enter 1 to 1B tokens.';
    fixture.detectChanges();
    expect(save().disabled).toBeTrue();

    fixture.componentInstance.tokenCapError = '';
    form.enabledReviewKinds = [];
    fixture.detectChanges();
    expect(save().disabled).withContext('a repository with no review kind cannot be saved').toBeTrue();
  });

  it('offers archiving for a registered repository but never for the legacy default', () => {
    expect(element().querySelector('.danger-button')).not.toBeNull();

    fixture.componentInstance.editingRepository = { ...registered, id: 'default' };
    fixture.detectChanges();
    expect(element().querySelector('.danger-button')).toBeNull();
  });

  it('closes on Escape through the shared modal behaviour', () => {
    let closed = 0;
    fixture.componentInstance.closed.subscribe(() => closed++);

    (element().querySelector('.repository-dialog') as HTMLElement)
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(closed).toBe(1);
  });
});
