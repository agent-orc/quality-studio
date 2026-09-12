import { Injectable, OnDestroy, computed, effect, signal } from '@angular/core';

const LAYOUT_STORAGE_KEY = 'qs-layout';
const RESIZE_HANDLE_WIDTH = 6;
const EXPLORER_DEFAULT_WIDTH = 280;
const EXPLORER_MIN_WIDTH = 180;
const EXPLORER_MAX_WIDTH = 560;
const EXPLORER_VIEWPORT_PERCENT = 28;
const REVIEW_DEFAULT_WIDTH = 320;
const REVIEW_MIN_WIDTH = 240;
const REVIEW_MAX_WIDTH = 640;
const REVIEW_VIEWPORT_PERCENT = 30;

interface PersistedWorkspaceLayout {
  explorerVisible: boolean;
  reviewVisible: boolean;
  explorerWidth: number;
  reviewWidth: number;
}

export type ResizablePane = 'explorer' | 'review';

/** Pane sizing, keyboard interaction and persisted layout, scoped to one shell. */
@Injectable()
export class WorkspaceLayoutState implements OnDestroy {
  // Panel visibility/width and drag state. Persisted layout is loaded once here so the
  // initial signal values already reflect it (no flash of the default layout on load).
  private readonly initialLayout = this.loadLayout();
  readonly explorerVisible = signal(this.initialLayout.explorerVisible);
  readonly reviewVisible = signal(this.initialLayout.reviewVisible);
  readonly explorerWidth = signal(this.initialLayout.explorerWidth);
  readonly reviewWidth = signal(this.initialLayout.reviewWidth);
  readonly dragging = signal<ResizablePane | null>(null);
  readonly gridTemplateColumns = computed(() => {
    const explorerTrack = this.explorerVisible() ? `min(${this.explorerWidth()}px, ${EXPLORER_VIEWPORT_PERCENT}vw) ${RESIZE_HANDLE_WIDTH}px` : '0px 0px';
    const reviewTrack = this.reviewVisible() ? `${RESIZE_HANDLE_WIDTH}px min(${this.reviewWidth()}px, ${REVIEW_VIEWPORT_PERCENT}vw)` : '0px 0px';
    return `${explorerTrack} minmax(0,1fr) ${reviewTrack}`;
  });
  private dragStartX = 0;
  private dragStartWidth = 0;
  private dragFrame: number | null = null;
  private pendingClientX = 0;

  constructor() {
    effect(() => {
      const layout: PersistedWorkspaceLayout = {
        explorerVisible: this.explorerVisible(), reviewVisible: this.reviewVisible(),
        explorerWidth: this.explorerWidth(), reviewWidth: this.reviewWidth(),
      };
      try { localStorage.setItem(LAYOUT_STORAGE_KEY, JSON.stringify(layout)); } catch { /* Keep session layout when storage is unavailable. */ }
    });
  }

  ngOnDestroy(): void { this.onDragEnd(); }

  toggleExplorer(): void { this.explorerVisible.update(visible => !visible); }

  toggleReview(): void { this.reviewVisible.update(visible => !visible); }

  resetExplorerWidth(): void { this.explorerWidth.set(EXPLORER_DEFAULT_WIDTH); }

  resetReviewWidth(): void { this.reviewWidth.set(REVIEW_DEFAULT_WIDTH); }

  // Ctrl+B toggles the Explorer, Ctrl+Alt+B toggles the Review panel.
  onKeydown(event: KeyboardEvent): void {
    if (!event.ctrlKey || event.key.toLowerCase() !== 'b') return;
    event.preventDefault();
    if (event.altKey) this.toggleReview(); else this.toggleExplorer();
  }

  startExplorerDrag(event: PointerEvent): void { this.beginDrag('explorer', event); }

  startReviewDrag(event: PointerEvent): void { this.beginDrag('review', event); }

  onDragMove(event: PointerEvent): void {
    if (!this.dragging()) return;
    // Coalesce rapid pointermove events to one grid-column update per frame.
    this.pendingClientX = event.clientX;
    if (this.dragFrame !== null) return;
    this.dragFrame = requestAnimationFrame(() => {
      this.dragFrame = null;
      this.applyDrag();
    });
  }

  onDragEnd(): void {
    if (this.dragFrame !== null) {
      cancelAnimationFrame(this.dragFrame);
      this.dragFrame = null;
    }
    this.dragging.set(null);
  }

  onHandleKeydown(event: KeyboardEvent, pane: ResizablePane): void {
    const step = 10;
    if (event.key === 'ArrowLeft') { this.nudgeWidth(pane, pane === 'explorer' ? -step : step); event.preventDefault(); }
    else if (event.key === 'ArrowRight') { this.nudgeWidth(pane, pane === 'explorer' ? step : -step); event.preventDefault(); }
    else if (event.key === 'Home' || event.key === 'Enter') {
      if (pane === 'explorer') this.resetExplorerWidth(); else this.resetReviewWidth();
      event.preventDefault();
    }
  }

  private beginDrag(pane: ResizablePane, event: PointerEvent): void {
    if (event.button !== 0) return;
    event.preventDefault();
    this.dragging.set(pane);
    this.dragStartX = event.clientX;
    this.dragStartWidth = this.effectiveWidth(pane);
    (event.target as HTMLElement).setPointerCapture(event.pointerId);
  }

  private applyDrag(): void {
    const pane = this.dragging();
    if (!pane) return;
    const delta = this.pendingClientX - this.dragStartX;
    if (pane === 'explorer') {
      this.explorerWidth.set(this.clampWidth(this.dragStartWidth + delta, EXPLORER_MIN_WIDTH, EXPLORER_MAX_WIDTH, EXPLORER_DEFAULT_WIDTH));
    } else {
      this.reviewWidth.set(this.clampWidth(this.dragStartWidth - delta, REVIEW_MIN_WIDTH, REVIEW_MAX_WIDTH, REVIEW_DEFAULT_WIDTH));
    }
  }

  private nudgeWidth(pane: ResizablePane, delta: number): void {
    if (pane === 'explorer') this.explorerWidth.set(this.clampWidth(this.effectiveWidth(pane) + delta, EXPLORER_MIN_WIDTH, EXPLORER_MAX_WIDTH, EXPLORER_DEFAULT_WIDTH));
    else this.reviewWidth.set(this.clampWidth(this.effectiveWidth(pane) + delta, REVIEW_MIN_WIDTH, REVIEW_MAX_WIDTH, REVIEW_DEFAULT_WIDTH));
  }

  /** CSS caps a preferred width on smaller viewports; interaction starts at its visible edge. */
  private effectiveWidth(pane: ResizablePane): number {
    const preferred = pane === 'explorer' ? this.explorerWidth() : this.reviewWidth();
    if (typeof window === 'undefined') return preferred;
    const percent = pane === 'explorer' ? EXPLORER_VIEWPORT_PERCENT : REVIEW_VIEWPORT_PERCENT;
    return Math.min(preferred, window.innerWidth * percent / 100);
  }

  private loadLayout(): PersistedWorkspaceLayout {
    const defaults: PersistedWorkspaceLayout = { explorerVisible: true, reviewVisible: true, explorerWidth: EXPLORER_DEFAULT_WIDTH, reviewWidth: REVIEW_DEFAULT_WIDTH };
    try {
      const raw = localStorage.getItem(LAYOUT_STORAGE_KEY);
      if (!raw) return defaults;
      const parsed = JSON.parse(raw);
      return {
        explorerVisible: typeof parsed.explorerVisible === 'boolean' ? parsed.explorerVisible : defaults.explorerVisible,
        reviewVisible: typeof parsed.reviewVisible === 'boolean' ? parsed.reviewVisible : defaults.reviewVisible,
        explorerWidth: this.clampWidth(parsed.explorerWidth, EXPLORER_MIN_WIDTH, EXPLORER_MAX_WIDTH, defaults.explorerWidth),
        reviewWidth: this.clampWidth(parsed.reviewWidth, REVIEW_MIN_WIDTH, REVIEW_MAX_WIDTH, defaults.reviewWidth),
      };
    } catch {
      return defaults;
    }
  }

  private clampWidth(value: unknown, min: number, max: number, fallback: number): number {
    return typeof value === 'number' && Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : fallback;
  }

}
