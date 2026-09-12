import { TestBed } from '@angular/core/testing';
import { ResizablePane, WorkspaceLayoutState } from './workspace-layout-state';

describe('Responsive workspace sizing', () => {
  let state: WorkspaceLayoutState;
  let viewport: jasmine.Spy;
  let storedLayout: string | null;
  let frame: FrameRequestCallback | null;

  beforeEach(() => {
    storedLayout = localStorage.getItem('qs-layout');
    localStorage.removeItem('qs-layout');
    viewport = spyOnProperty(window, 'innerWidth', 'get').and.returnValue(1000);
    frame = null;
    spyOn(window, 'requestAnimationFrame').and.callFake(callback => { frame = callback; return 1; });
    spyOn(window, 'cancelAnimationFrame').and.stub();
    TestBed.configureTestingModule({ providers: [WorkspaceLayoutState] });
    state = TestBed.inject(WorkspaceLayoutState);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    if (storedLayout === null) localStorage.removeItem('qs-layout');
    else localStorage.setItem('qs-layout', storedLayout);
  });

  function drag(pane: ResizablePane, from: number, to: number): void {
    const handle = document.createElement('div');
    handle.setPointerCapture = () => undefined;
    const down = new PointerEvent('pointerdown', { button: 0, pointerId: 1, clientX: from });
    handle.dispatchEvent(down);
    if (pane === 'explorer') state.startExplorerDrag(down);
    else state.startReviewDrag(down);
    state.onDragMove(new PointerEvent('pointermove', { clientX: to }));
    frame?.(0);
    state.onDragEnd();
  }

  it('shrinks the explorer immediately from its visible 28vw edge after a viewport reduction', () => {
    state.explorerWidth.set(560);
    // At 1000px the preferred 560px pane is rendered at 280px. Move its edge by 50px.
    drag('explorer', 280, 230);
    expect(state.explorerWidth()).toBe(230);
  });

  it('shrinks the review pane immediately when its left edge moves right', () => {
    state.reviewWidth.set(640);
    // The right-aligned pane renders at 300px. Moving its left edge right removes 50px.
    drag('review', 700, 750);
    expect(state.reviewWidth()).toBe(250);
  });

  it('starts keyboard shrinking from the capped width on both sides', () => {
    state.explorerWidth.set(560);
    state.reviewWidth.set(640);
    state.onHandleKeydown(new KeyboardEvent('keydown', { key: 'ArrowLeft' }), 'explorer');
    state.onHandleKeydown(new KeyboardEvent('keydown', { key: 'ArrowRight' }), 'review');
    expect(state.explorerWidth()).toBe(270);
    expect(state.reviewWidth()).toBe(290);
  });

  it('does not accumulate invisible keyboard growth that delays the next shrink', () => {
    state.explorerWidth.set(560);
    for (let count = 0; count < 8; count++) {
      state.onHandleKeydown(new KeyboardEvent('keydown', { key: 'ArrowRight' }), 'explorer');
    }
    state.onHandleKeydown(new KeyboardEvent('keydown', { key: 'ArrowLeft' }), 'explorer');
    expect(state.explorerWidth()).toBe(270);
  });

  it('preserves the original resize direction when the preferred pane fits the viewport', () => {
    viewport.and.returnValue(1600);
    state.explorerWidth.set(300);
    state.reviewWidth.set(400);
    state.onHandleKeydown(new KeyboardEvent('keydown', { key: 'ArrowRight' }), 'explorer');
    state.onHandleKeydown(new KeyboardEvent('keydown', { key: 'ArrowRight' }), 'review');
    expect(state.explorerWidth()).toBe(310);
    expect(state.reviewWidth()).toBe(390);
  });
});
