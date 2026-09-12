import { AfterViewInit, Directive, ElementRef, OnDestroy, inject, output } from '@angular/core';

const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), '
  + 'textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"])';

/**
 * The stack of currently open modals. Only the topmost one traps focus and answers Escape, so a
 * confirmation raised from inside a dialog is not fought over by the dialog underneath it.
 */
const openModals: Modal[] = [];

/**
 * Turns an element carrying `role="dialog" aria-modal="true"` into an actual modal: initial focus
 * inside it, Tab confined to it, Escape dismissing it, and focus returned to whatever opened it.
 * Every dialog in the shell uses this instead of restating the behaviour, or omitting it.
 */
@Directive({
  selector: '[qsModal]',
  host: {
    tabindex: '-1',
    '(keydown)': 'onKeydown($event)',
    '(document:focusin)': 'onDocumentFocusIn($event)',
  },
})
export class Modal implements AfterViewInit, OnDestroy {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly opener = typeof document === 'undefined' ? null : document.activeElement as HTMLElement | null;
  readonly dismiss = output<void>();

  ngAfterViewInit(): void {
    openModals.push(this);
    queueMicrotask(() => this.focusInside());
  }

  ngOnDestroy(): void {
    const index = openModals.indexOf(this);
    if (index >= 0) openModals.splice(index, 1);
    if (this.opener?.isConnected) this.opener.focus();
  }

  onKeydown(event: KeyboardEvent): void {
    if (!this.topmost()) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.dismiss.emit();
      return;
    }
    if (event.key !== 'Tab') return;

    const focusable = this.focusable();
    if (!focusable.length) {
      event.preventDefault();
      this.element.nativeElement.focus();
      return;
    }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;
    if (event.shiftKey && (active === first || active === this.element.nativeElement)) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && active === last) {
      event.preventDefault();
      first.focus();
    }
  }

  /** Pointer interaction can drop focus onto the backdrop; bring it back inside the dialog. */
  onDocumentFocusIn(event: FocusEvent): void {
    if (!this.topmost()) return;
    const target = event.target as Node | null;
    if (target && !this.element.nativeElement.contains(target)) this.focusInside();
  }

  private topmost(): boolean {
    return openModals[openModals.length - 1] === this;
  }

  private focusable(): HTMLElement[] {
    // Closed details can retain layout boxes for invisible links. Native visibility
    // also checks that hidden content, so it cannot become the modal's last tab stop.
    return Array.from(this.element.nativeElement.querySelectorAll<HTMLElement>(FOCUSABLE))
      .filter(candidate => candidate.checkVisibility());
  }

  private focusInside(): void {
    const host = this.element.nativeElement;
    if (!host.isConnected) return;
    const preferred = host.querySelector<HTMLElement>('[data-modal-autofocus]');
    (preferred ?? this.focusable()[0] ?? host).focus();
  }
}

/**
 * The click-to-dismiss backdrop behind a modal. It only dismisses when the backdrop itself was
 * clicked, so the dialog above it needs no stopPropagation handler, and it is presentational: the
 * accessible ways out are the close button and Escape.
 */
@Directive({
  selector: '[qsModalBackdrop]',
  host: {
    role: 'presentation',
    '(click)': 'onClick($event)',
  },
})
export class ModalBackdrop {
  readonly dismiss = output<void>();

  onClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.dismiss.emit();
  }
}
