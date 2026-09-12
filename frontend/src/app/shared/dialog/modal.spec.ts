import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ConfirmDialog } from './confirm-dialog';
import { Modal } from './modal';
import { ResumeCapDialog } from './resume-cap-dialog';
import { ReviewRun } from '../../core/models/contracts';

@Component({
  selector: 'qs-modal-host',
  imports: [Modal],
  template: `
    <button #opener type="button">Open</button>
    @if (open()) {
      <section class="probe" qsModal (dismiss)="dismissed = dismissed + 1" role="dialog" aria-modal="true">
        <button class="first" type="button" data-modal-autofocus>First</button>
        <button class="middle" type="button">Middle</button>
        <button class="last" type="button">Last</button>
      </section>
    }
  `,
})
class ModalHost {
  readonly open = signal(false);
  dismissed = 0;
}

describe('Modal behaviour', () => {
  let fixture: ComponentFixture<ModalHost>;
  let host: ModalHost;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ModalHost] }).compileComponents();
    fixture = TestBed.createComponent(ModalHost);
    host = fixture.componentInstance;
    fixture.detectChanges();
    opener().focus();
    host.open.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
  });

  function dialog(): HTMLElement {
    return fixture.nativeElement.querySelector('.probe') as HTMLElement;
  }

  function opener(): HTMLElement {
    return fixture.nativeElement.querySelector('button') as HTMLElement;
  }

  it('moves initial focus to the marked control', () => {
    expect(document.activeElement).toBe(dialog().querySelector('.first'));
  });

  it('dismisses on Escape', () => {
    dialog().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(host.dismissed).toBe(1);
  });

  it('wraps Tab and Shift+Tab inside the dialog', () => {
    const first = dialog().querySelector('.first') as HTMLElement;
    const last = dialog().querySelector('.last') as HTMLElement;

    last.focus();
    last.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(first);

    first.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(last);
  });

  it('includes a native explanation summary when wrapping Tab and Shift+Tab', () => {
    const details = document.createElement('details');
    const summary = document.createElement('summary');
    summary.textContent = 'Explanation';
    const hiddenLink = document.createElement('a');
    hiddenLink.href = '#implementation';
    hiddenLink.textContent = 'Implementation';
    details.append(summary, hiddenLink);
    dialog().append(details);
    const first = dialog().querySelector('.first') as HTMLElement;

    summary.focus();
    const tab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
    summary.dispatchEvent(tab);
    expect(tab.defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(first);
    first.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(summary);
  });

  it('pulls focus back when a pointer drops it outside', () => {
    const outside = opener();
    outside.focus();
    outside.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));

    expect(dialog().contains(document.activeElement)).toBeTrue();
  });

  it('returns focus to the opener when it closes', () => {
    const trigger = opener();
    host.open.set(false);
    fixture.detectChanges();

    expect(document.activeElement).toBe(trigger);
  });
});

describe('ConfirmDialog', () => {
  it('offers cancel and confirm without a native dialog', () => {
    const fixture = TestBed.createComponent(ConfirmDialog);
    fixture.componentRef.setInput('heading', 'Archive Payments?');
    fixture.componentRef.setInput('message', 'No repository file is changed.');
    fixture.componentRef.setInput('confirmLabel', 'Archive');
    fixture.componentRef.setInput('danger', true);
    let confirmed = 0;
    let cancelled = 0;
    fixture.componentInstance.confirmed.subscribe(() => confirmed++);
    fixture.componentInstance.cancelled.subscribe(() => cancelled++);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('Archive Payments?');
    expect(element.querySelector('.danger-button')?.textContent?.trim()).toBe('Archive');

    (element.querySelector('.danger-button') as HTMLButtonElement).click();
    (element.querySelector('.secondary-button') as HTMLButtonElement).click();

    expect(confirmed).toBe(1);
    expect(cancelled).toBe(1);
  });
});

describe('ResumeCapDialog', () => {
  function run(overrides: Partial<ReviewRun>): ReviewRun {
    return { id: 'run-1', kind: 'code', path: 'src', skippedFiles: 3, tokenCap: null, costCap: null, currency: null, ...overrides } as ReviewRun;
  }

  it('suggests twice the token cap and emits a parsed token cap', () => {
    const fixture = TestBed.createComponent(ResumeCapDialog);
    fixture.componentRef.setInput('run', run({ tokenCap: 100_000 }));
    let emitted: unknown = null;
    fixture.componentInstance.submitted.subscribe(cap => emitted = cap);
    fixture.detectChanges();

    expect(fixture.componentInstance.entered()).toBe('200k');
    fixture.componentInstance.submit();
    expect(emitted).toEqual({ tokenCap: 200_000 });
  });

  it('switches to the cost cap and rejects a value that is not a positive amount', () => {
    const fixture = TestBed.createComponent(ResumeCapDialog);
    fixture.componentRef.setInput('run', run({ costCap: 2.5, currency: 'EUR' }));
    let emitted: unknown = null;
    fixture.componentInstance.submitted.subscribe(cap => emitted = cap);
    fixture.detectChanges();

    expect(fixture.componentInstance.byTokens()).toBeFalse();
    expect(fixture.componentInstance.entered()).toBe('5');

    fixture.componentInstance.setValue('nonsense');
    fixture.componentInstance.submit();
    expect(emitted).toBeNull();
    expect(fixture.componentInstance.error()).toContain('EUR');

    fixture.componentInstance.setValue('7.25');
    fixture.componentInstance.submit();
    expect(emitted).toEqual({ costCap: 7.25 });
  });
});
