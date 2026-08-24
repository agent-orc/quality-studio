import { languageForPath } from './language';

describe('languageForPath', () => {
  it('labels the catalogued extensions from the last path segment', () => {
    expect(languageForPath('src/QualityStudio.Api/Program.cs')).toBe('C#');
    expect(languageForPath('src/QualityStudio.Api/QualityStudio.Api.csproj')).toBe('C# Project');
    expect(languageForPath('QualityStudio.sln')).toBe('Solution');
    expect(languageForPath('frontend/src/app/app.ts')).toBe('TypeScript');
    expect(languageForPath('frontend/src/app/app.html')).toBe('HTML');
    expect(languageForPath('docs/README.md')).toBe('Markdown');
    expect(languageForPath('.github/workflows/ci.yml')).toBe('YAML');
    expect(languageForPath('deploy/values.yaml')).toBe('YAML');
  });

  it('ignores case in the extension', () => {
    expect(languageForPath('src/Program.CS')).toBe('C#');
    expect(languageForPath('DOCS/NOTES.MD')).toBe('Markdown');
  });

  it('reads dotfiles whose whole name is the extension', () => {
    expect(languageForPath('.gitignore')).toBe('Ignore List');
    expect(languageForPath('frontend/.editorconfig')).toBe('EditorConfig');
  });

  it('uses the final dot when a name carries several', () => {
    expect(languageForPath('app.component.ts')).toBe('TypeScript');
    expect(languageForPath('.env.local')).toBe('Plain text');
  });

  it('only inspects the file name, not the directories above it', () => {
    expect(languageForPath('releases/v1.2.3/CHANGELOG')).toBe('Plain text');
    expect(languageForPath('releases/v1.2.3/notes.md')).toBe('Markdown');
    expect(languageForPath('src/app/')).toBe('Plain text');
  });

  it('falls back to plain text for anything uncatalogued or absent', () => {
    expect(languageForPath('LICENSE')).toBe('Plain text');
    expect(languageForPath('Makefile')).toBe('Plain text');
    expect(languageForPath('archive.tar.gz')).toBe('Plain text');
    expect(languageForPath('trailing.')).toBe('Plain text');
    expect(languageForPath('.')).toBe('Plain text');
    expect(languageForPath('')).toBe('Plain text');
    expect(languageForPath(undefined)).toBe('Plain text');
    expect(languageForPath(null)).toBe('Plain text');
  });
});
