import { DOCUMENT } from '@angular/common';
import { Component, effect, inject, signal } from '@angular/core';

/** Development-only reference surface bootstrapped by style-reference/main.ts. */
@Component({
  selector: 'qs-style-reference',
  standalone: true,
  templateUrl: './style-reference.html',
  styleUrl: './style-reference.css',
})
export class StyleReference {
  private readonly document = inject(DOCUMENT);
  readonly theme = signal<'light' | 'dark'>('light');
  readonly ascending = signal(true);
  readonly feedback = signal('Ready for keyboard and pointer interaction.');

  constructor() {
    effect(() => this.document.documentElement.dataset['theme'] = this.theme());
  }

  toggleSort(): void {
    this.ascending.update(value => !value);
  }

  toggleTheme(): void {
    this.theme.update(theme => theme === 'light' ? 'dark' : 'light');
  }
}
