import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { QualityApi } from '../quality-api';
import { TreeNode } from '../contracts';
import { Explorer } from './explorer';

const kinds = { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 90, band: 'A', metaPath: null } } as TreeNode['kinds'];
const tree: TreeNode[] = [{
  id: 'quality-studio', name: 'Quality Studio', level: 'repository', path: '.', kinds, children: [{
    id: 'api', name: 'QualityStudio.Api', level: 'project', path: 'src/QualityStudio.Api', kinds, children: [
      { id: 'program', name: 'Program.cs', level: 'file', path: 'src/QualityStudio.Api/Program.cs', kinds, children: [] },
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
    fixture.componentRef.setInput('selectedPath', 'src/QualityStudio.Api/Program.cs');
    fixture.componentRef.setInput('activeKind', 'code');
    fixture.componentRef.setInput('viewportHeight', 600);
    fixture.detectChanges();
  });

  it('selects and toggles a container when its row is clicked', () => {
    const opened: string[] = [];
    component.nodeOpen.subscribe(path => opened.push(path));
    const row = fixture.nativeElement.querySelector('[data-node-id="quality-studio"]') as HTMLButtonElement;

    expect(component.expanded().has('quality-studio')).toBeTrue();
    row.click();

    expect(component.expanded().has('quality-studio')).toBeFalse();
    expect(opened).toEqual(['.']);
  });

  it('only toggles a container when its chevron is clicked', () => {
    const opened: string[] = [];
    component.nodeOpen.subscribe(path => opened.push(path));
    const chevron = fixture.nativeElement.querySelector('[data-node-id="quality-studio"] .chevron') as HTMLElement;

    expect(component.expanded().has('quality-studio')).toBeTrue();
    chevron.click();

    expect(component.expanded().has('quality-studio')).toBeFalse();
    expect(opened).toEqual([]);
  });
});
