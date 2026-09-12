import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { QualityApi } from '../../../core/api/quality-api';
import { TreeNode } from '../../../core/models/contracts';
import { Explorer } from './explorer';

const kinds = { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 90, band: 'A', metaPath: null } } as TreeNode['kinds'];
const tree: TreeNode[] = [{
  id: 'repository:actual-root', name: 'Quality Studio', level: 'repository', path: '.', kinds, children: [{
    id: 'backend', name: 'backend', level: 'folder', path: 'backend', kinds, children: [
      { id: 'program', name: 'Program.cs', level: 'file', path: 'backend/Program.cs', kinds, children: [] },
    ],
  }],
}];

describe('Explorer container activation', () => {
  let fixture: ComponentFixture<Explorer>;
  let component: Explorer;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Explorer],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    TestBed.inject(QualityApi).tree.set(tree);
    fixture = TestBed.createComponent(Explorer);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('selectedPath', 'backend/Program.cs');
    fixture.componentRef.setInput('activeKind', 'code');
    fixture.componentRef.setInput('viewportHeight', 600);
    fixture.detectChanges();
  });

  it('selects and toggles a container when its row is clicked', () => {
    const opened: string[] = [];
    component.nodeOpen.subscribe(path => opened.push(path));
    const row = fixture.nativeElement.querySelector('[data-node-id="repository:actual-root"]') as HTMLButtonElement;

    expect(component.expanded().has('repository:actual-root')).toBeTrue();
    row.click();

    fixture.detectChanges();
    expect(component.expanded().has('repository:actual-root')).toBeFalse();
    expect(opened).toEqual(['.']);
  });

  it('only toggles a container when its chevron is clicked', () => {
    const opened: string[] = [];
    component.nodeOpen.subscribe(path => opened.push(path));
    const chevron = fixture.nativeElement.querySelector('[data-node-id="repository:actual-root"] .chevron') as HTMLElement;

    expect(component.expanded().has('repository:actual-root')).toBeTrue();
    chevron.click();

    fixture.detectChanges();
    expect(component.expanded().has('repository:actual-root')).toBeFalse();
    expect(opened).toEqual([]);
  });

  it('opens the real repository root once and keeps a collapsed root closed after refresh', () => {
    const api = TestBed.inject(QualityApi);
    expect(component.expanded().has('repository:actual-root')).toBeTrue();
    expect(component.expanded().has('quality-studio')).toBeFalse();
    expect(component.expanded().has('src')).toBeFalse();
    expect(component.filteredRows().map(row => row.path)).toContain('backend');

    const chevron = fixture.nativeElement.querySelector('[data-node-id="repository:actual-root"] .chevron') as HTMLElement;
    chevron.click();
    fixture.detectChanges();
    api.tree.set([{ ...tree[0], children: [...tree[0].children] }]);
    fixture.detectChanges();

    expect(component.expanded().has('repository:actual-root')).toBeFalse();
    expect(component.filteredRows().map(row => row.path)).toEqual(['.']);
  });

  it('expands a newly selected repository and loads only its real root children', async () => {
    const api = TestBed.inject(QualityApi);
    const loading = spyOn(api, 'loadTreeChildren').and.resolveTo();
    api.selectedRepositoryId.set('other');
    api.tree.set([{ id: 'other-root', name: 'Other', path: '.', level: 'repository', kinds,
      hasChildren: true, childrenLoaded: false, children: [] }]);
    fixture.componentRef.setInput('selectedPath', '.');
    fixture.detectChanges();

    expect([...component.expanded()]).toEqual(['other-root']);
    expect(loading).toHaveBeenCalledOnceWith(jasmine.objectContaining({ id: 'other-root' }));
    const focused = document.activeElement;
    api.tree.set([{ ...api.tree()[0], childrenLoaded: true, children: [{
      id: 'other-backend', name: 'backend', path: 'backend', level: 'folder', kinds, children: [],
    }] }]);
    fixture.detectChanges();

    expect(component.filteredRows().map(row => row.name)).toEqual(['Other', 'backend']);
    expect(loading).toHaveBeenCalledTimes(1);
    expect(document.activeElement).withContext('background hydration does not take keyboard focus').toBe(focused);
    expect(component.activeId()).toBe('other-root');
  });

  it('reloads an expanded root after refresh without reopening a collapsed root', () => {
    const api = TestBed.inject(QualityApi);
    const loading = spyOn(api, 'loadTreeChildren').and.resolveTo();
    const refreshed = { ...tree[0], hasChildren: true, childrenLoaded: false, children: [] };
    api.tree.set([refreshed]);
    fixture.detectChanges();
    expect(loading).toHaveBeenCalledOnceWith(jasmine.objectContaining({ id: 'repository:actual-root' }));

    component.expanded.set(new Set());
    fixture.detectChanges();
    api.tree.set([{ ...refreshed }]);
    fixture.detectChanges();
    expect(loading).toHaveBeenCalledTimes(1);
    expect(component.expanded().size).toBe(0);
  });

  it('applies the filter only after typing settles, and asks the server once', async () => {
    const http = TestBed.inject(HttpTestingController);
    component.setQuery('P');
    component.setQuery('Pr');
    component.setQuery('Program');

    expect(component.queryInput()).toBe('Program');
    expect(component.query()).withContext('filter is not applied per keystroke').toBe('');
    http.expectNone(request => request.url.endsWith('/tree/v2/search'));

    await new Promise(resolve => setTimeout(resolve, 200));

    // The tree only holds the expanded levels, so the matches come from the bounded server filter.
    http.expectOne(request => request.url === '/api/repos/default/tree/v2/search'
      && request.params.get('query') === 'Program' && request.params.get('view') === 'files').flush({
        schemaVersion: 2, parentId: null, path: 'search:Program', offset: 0, limit: 200,
        nextCursor: null, nodes: [{ id: 'program', name: 'Program.cs', level: 'file',
          path: 'backend/Program.cs', kinds, children: [] }],
      });
    await new Promise(resolve => setTimeout(resolve));

    expect(component.query()).toBe('Program');
    expect(component.filteredRows().map(row => row.name)).toEqual(['Program.cs']);
  });

  it('clears an active filter on Escape even when it matched nothing', async () => {
    component.setQuery('Program');
    await new Promise(resolve => setTimeout(resolve, 200));
    expect(component.filteredRows()).withContext('the server filter has not answered yet').toEqual([]);

    component.onTreeKeydown(new KeyboardEvent('keydown', { key: 'Escape' }));

    expect(component.queryInput()).toBe('');
    expect(component.query()).toBe('');
  });

  it('names an unreadable sidecar on the tree chip rather than showing it as not reviewed', () => {
    TestBed.inject(QualityApi).tree.set([{
      id: 'root', name: 'Root', level: 'repository', path: '.',
      kinds: { code: { direct: 'invalid', descendants: 'invalid', overall: 'invalid', score: null, band: null, metaPath: null } },
      children: [],
    }]);
    fixture.detectChanges();

    const chip = fixture.nativeElement.querySelector('[data-node-id="root"] .status') as HTMLElement;
    expect(chip.classList).toContain('invalid');
    expect(chip.getAttribute('title')).toBe('code: invalid (unreadable sidecar)');
    expect(fixture.nativeElement.querySelector('.legend')?.textContent).toContain('Invalid');
  });
});
