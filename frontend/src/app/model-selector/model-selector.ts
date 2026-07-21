import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  computed,
  effect,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { ChatCliOption, ChatModelOption, ChatModelSelection, shortModelLabel } from 'coding-agent-chat/core';

/**
 * QS-styled CLI + model picker, built against the same catalog contract as
 * coding-agent-chat's `<cac-model-selector>` (`ChatCliOption` / `ChatModelOption` /
 * `ChatModelSelection` from `coding-agent-chat/core`). coding-agent-chat's Angular
 * component itself targets Angular >=21 and cannot be installed alongside this
 * Angular 20 workspace's peer dependencies, so this renders the same trigger-chip +
 * popover interaction against DESIGN-KINSHIP tokens instead of importing the
 * library's UI. The host feeds `models` for the CLI last requested via
 * `catalogRequested` and answers with a fresh catalog; nothing here hardcodes a
 * model id.
 */
@Component({
  selector: 'qs-model-selector',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './model-selector.html',
  styleUrl: './model-selector.css',
})
export class ModelSelector {
  readonly cliOptions = input<readonly ChatCliOption[]>([]);
  readonly cliType = input<string | null>(null);
  readonly model = input<string | null>(null);
  readonly thinkingLevel = input<string | null>(null);
  /** Catalog for the CLI named by the latest `catalogRequested` emission. */
  readonly models = input<readonly ChatModelOption[]>([]);
  readonly catalogLoading = input<boolean>(false);
  readonly catalogError = input<string | null>(null);
  readonly disabled = input<boolean>(false);
  readonly disabledReason = input<string | null>(null);
  readonly compact = input<boolean>(false);

  /** Atomic commit, emitted from Done. */
  readonly commit = output<ChatModelSelection>();
  /** Asks the host to (re)load the catalog for a CLI. */
  readonly catalogRequested = output<string>();

  readonly open = signal(false);
  readonly draftCliType = signal<string | null>(null);
  readonly draftModel = signal('');
  readonly draftThinkingLevel = signal<string | null>(null);
  /**
   * Viewport-anchored (`position: fixed`) coordinates for the popover, computed from
   * the trigger's bounding rect. A host can embed this picker inside a container with
   * `overflow: hidden`/`auto` (e.g. the repository dialog) — `position: absolute`
   * would then be clipped by that ancestor, so this escapes it the same way the
   * chat library's own anchored-popover directive does.
   */
  readonly pickerStyle = signal<string>('');

  private readonly triggerRef = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  readonly displayLabel = computed<string>(() => shortModelLabel(this.model()));

  readonly cliIcon = computed<string>(() => {
    const id = this.cliType();
    return (id && this.cliOptions().find(o => o.id === id)?.icon) || '·';
  });

  readonly badgeText = computed<string>(() => this.describe(this.cliType(), this.model(), this.thinkingLevel()));

  readonly draftAvailableModels = computed<readonly ChatModelOption[]>(() =>
    this.models().filter(m => m.available !== false));

  readonly draftThinkingLevels = computed<readonly string[]>(
    () => this.draftAvailableModels().find(m => m.id === this.draftModel())?.thinkingLevels ?? []);

  readonly hasChanges = computed<boolean>(() => {
    if (!this.open()) return false;
    const cliChanged = this.draftCliType() !== this.cliType();
    const modelChanged = this.draftModel() !== (this.model() ?? '').trim();
    const levelChanged = this.draftThinkingLevel() !== this.thinkingLevel();
    return cliChanged || modelChanged || levelChanged;
  });

  constructor() {
    // A host can disable the picker (e.g. a review is running) while it is open.
    effect(() => {
      if (!this.disabled() || !this.open()) return;
      untracked(() => this.close());
    });
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open()) this.close();
  }

  @HostListener('window:resize')
  onWindowResize(): void {
    if (this.open()) this.updatePosition();
  }

  toggle(event: MouseEvent): void {
    if (this.disabled()) return;
    event.preventDefault();
    event.stopPropagation();
    if (this.open()) {
      this.close();
      return;
    }
    const cli = this.cliType();
    this.draftCliType.set(cli);
    this.draftModel.set((this.model() ?? '').trim());
    this.draftThinkingLevel.set(this.thinkingLevel());
    this.open.set(true);
    this.updatePosition();
    if (cli) this.catalogRequested.emit(cli);
  }

  private updatePosition(): void {
    const trigger = this.triggerRef()?.nativeElement;
    if (!trigger) return;
    const rect = trigger.getBoundingClientRect();
    const width = 260;
    const margin = 8;
    let left = rect.left;
    if (left + width > window.innerWidth - margin) left = Math.max(margin, window.innerWidth - width - margin);
    const spaceBelow = window.innerHeight - rect.bottom - margin;
    const spaceAbove = rect.top - margin;
    const openUpward = spaceBelow < 200 && spaceAbove > spaceBelow;
    const maxHeight = Math.max(160, Math.min(380, openUpward ? spaceAbove : spaceBelow));
    this.pickerStyle.set(openUpward
      ? `left:${left}px;bottom:${window.innerHeight - rect.top + 6}px;max-height:${maxHeight}px;width:${width}px`
      : `left:${left}px;top:${rect.bottom + 6}px;max-height:${maxHeight}px;width:${width}px`);
  }

  close(): void {
    this.open.set(false);
    queueMicrotask(() => this.triggerRef()?.nativeElement.focus());
  }

  onBackdropClick(): void {
    this.close();
  }

  selectCli(id: string): void {
    if (id !== this.draftCliType()) {
      this.draftCliType.set(id);
      this.draftModel.set('');
      this.draftThinkingLevel.set(null);
    }
    this.catalogRequested.emit(id);
  }

  selectDefaultModel(): void {
    this.draftModel.set('');
    this.draftThinkingLevel.set(null);
  }

  selectModel(id: string): void {
    this.draftModel.set(id);
    const info = this.draftAvailableModels().find(m => m.id === id);
    this.draftThinkingLevel.set(info?.defaultThinkingLevel ?? (info?.thinkingLevels?.[0] ?? null));
  }

  selectThinkingLevel(level: string): void {
    this.draftThinkingLevel.set(level);
  }

  done(): void {
    if (!this.open()) return;
    const cli = this.draftCliType();
    if (cli && this.hasChanges()) {
      this.commit.emit({ cliType: cli, model: this.draftModel(), thinkingLevel: this.draftThinkingLevel() });
    }
    this.close();
  }

  cancel(): void {
    this.close();
  }

  onRefresh(): void {
    const cli = this.draftCliType();
    if (cli) this.catalogRequested.emit(cli);
  }

  cliLabel(id: string): string {
    return this.cliOptions().find(o => o.id === id)?.label ?? id;
  }

  cliIconFor(id: string): string {
    return this.cliOptions().find(o => o.id === id)?.icon ?? '·';
  }

  private describe(cliType: string | null, model: string | null, level: string | null): string {
    const cli = cliType ? this.cliLabel(cliType) : 'No CLI';
    const m = model && model.trim() ? shortModelLabel(model) : 'CLI default';
    return level ? `${cli} · ${m} · ${level}` : `${cli} · ${m}`;
  }
}
