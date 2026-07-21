import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChatModelSelection } from 'coding-agent-chat/core';

import { ModelSelector } from './model-selector';

describe('ModelSelector', () => {
  let fixture: ComponentFixture<ModelSelector>;
  let component: ModelSelector;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ModelSelector] }).compileComponents();
    fixture = TestBed.createComponent(ModelSelector);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('cliOptions', [
      { id: 'codex', label: 'Codex', icon: '◆' },
      { id: 'claude', label: 'Claude Code', icon: '✳' },
    ]);
    fixture.componentRef.setInput('cliType', 'codex');
    fixture.componentRef.setInput('model', null);
    fixture.componentRef.setInput('models', [
      { id: 'gpt-5.6', label: 'GPT-5.6', isDefault: true, thinkingLevels: ['low', 'high', 'xhigh'], defaultThinkingLevel: 'high' },
      { id: 'gpt-5', label: 'GPT-5', available: false },
    ]);
    fixture.detectChanges();
  });

  it('requests the catalog for the current CLI when opened', () => {
    const requested: string[] = [];
    component.catalogRequested.subscribe(id => requested.push(id));

    (fixture.nativeElement.querySelector('.model-selector') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(component.open()).toBeTrue();
    expect(requested).toEqual(['codex']);
  });

  it('hides unavailable models and shows a default badge on the entry marked isDefault', () => {
    (fixture.nativeElement.querySelector('.model-selector') as HTMLButtonElement).click();
    fixture.detectChanges();

    const rows = fixture.nativeElement.querySelectorAll('[data-testid^="qs-model-selector-model-"]');
    const ids = Array.from(rows).map(row => (row as HTMLElement).getAttribute('data-testid'));
    expect(ids).toContain('qs-model-selector-model-gpt-5.6');
    expect(ids).not.toContain('qs-model-selector-model-gpt-5');
  });

  it('commits the picked model and thinking level on Done, closing the popover', () => {
    const commits: ChatModelSelection[] = [];
    component.commit.subscribe(selection => commits.push(selection));

    (fixture.nativeElement.querySelector('.model-selector') as HTMLButtonElement).click();
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="qs-model-selector-model-gpt-5.6"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="qs-model-selector-done"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(commits).toEqual([{ cliType: 'codex', model: 'gpt-5.6', thinkingLevel: 'high' }]);
    expect(component.open()).toBeFalse();
  });

  it('does not open when disabled', () => {
    fixture.componentRef.setInput('disabled', true);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.model-selector') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(component.open()).toBeFalse();
  });
});
