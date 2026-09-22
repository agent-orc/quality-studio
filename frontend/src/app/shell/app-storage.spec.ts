import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { QualityApi } from '../core/api/quality-api';
import { App } from './app';

describe('App shell without persistent storage', () => {
  let fixture: ComponentFixture<App> | undefined;
  let api: QualityApi;
  const originalUrl = location.href;

  beforeEach(async () => {
    history.replaceState(null, '', location.pathname);
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).overrideComponent(App, { set: { imports: [], template: '' } }).compileComponents();
    api = TestBed.inject(QualityApi);
    spyOn(api, 'loadRepositories').and.resolveTo();
    spyOn(api, 'loadModelCatalog').and.resolveTo();
    spyOn(api, 'loadProjectDashboard').and.resolveTo();
    spyOn(api, 'loadTree').and.resolveTo();
    spyOn(api, 'loadReviewRuns').and.resolveTo();
    spyOn(api, 'loadUsage').and.resolveTo();
    spyOn(api, 'loadQuotas').and.resolveTo();
  });

  afterEach(() => {
    fixture?.destroy();
    fixture = undefined;
    history.replaceState(null, '', originalUrl);
  });

  it('starts and loads the workspace when browser storage is blocked', async () => {
    spyOn(Storage.prototype, 'getItem').and.throwError('Storage denied');
    spyOn(Storage.prototype, 'setItem').and.throwError('Storage denied');

    expect(() => { fixture = TestBed.createComponent(App); }).not.toThrow();
    if (!fixture) return;
    fixture.detectChanges();
    await new Promise<void>(resolve => setTimeout(resolve, 0));

    expect(fixture.componentInstance.theme()).toBe('dark');
    expect(api.loadRepositories).toHaveBeenCalledWith(null);
    expect(api.loadModelCatalog).toHaveBeenCalled();
    expect(api.loadReviewRuns).toHaveBeenCalled();
  });

  it('keeps theme changes and repository switching usable when storage writes fail', async () => {
    fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await new Promise<void>(resolve => setTimeout(resolve, 0));
    spyOn(Storage.prototype, 'setItem').and.throwError('Storage quota exceeded');
    const select = spyOn(api, 'selectRepository').and.resolveTo();
    const app = fixture.componentInstance;
    app.theme.set('dark');

    expect(() => app.setTheme()).not.toThrow();
    expect(app.theme()).toBe('light');
    await expectAsync(app.switchRepository('next-repository')).toBeResolved();
    expect(select).toHaveBeenCalledWith('next-repository');
  });
});
