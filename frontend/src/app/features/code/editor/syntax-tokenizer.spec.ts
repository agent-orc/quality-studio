import { syntaxLanguageForPath } from './syntax-language';
import { tokenizeSource } from './syntax-tokenizer';
import { TokenKind, TokenLine } from './syntax-types';

function textFor(line: TokenLine, kind: TokenKind): string {
  return line.filter(span => span.kind === kind).map(span => span.text).join('');
}

describe('syntax tokenizer', () => {
  it('maps every required file family without changing display labels', () => {
    expect(syntaxLanguageForPath('src/Program.cs')).toBe('csharp');
    expect(syntaxLanguageForPath('web/app.ts')).toBe('typescript');
    expect(syntaxLanguageForPath('web/component.tsx')).toBe('typescript');
    expect(syntaxLanguageForPath('web/index.html')).toBe('markup');
    expect(syntaxLanguageForPath('web/site.css')).toBe('css');
    expect(syntaxLanguageForPath('web/site.scss')).toBe('scss');
    expect(syntaxLanguageForPath('appsettings.json')).toBe('json');
    expect(syntaxLanguageForPath('README.md')).toBe('markdown');
    expect(syntaxLanguageForPath('LICENSE')).toBeNull();
  });

  it('keeps C# block comments, verbatim strings, and raw strings across lines', () => {
    const source = [
      'var verbatim = @"first',
      'second";',
      'var raw = """',
      '<unsafe>& content',
      'last',
      '""";',
      '/* comment first',
      'comment last */',
    ].join('\n');
    const lines = tokenizeSource(source, 'csharp');

    expect(textFor(lines[0], 'string')).toContain('@"first');
    expect(textFor(lines[1], 'string')).toContain('second"');
    expect(textFor(lines[3], 'string')).toBe('<unsafe>& content');
    expect(textFor(lines[4], 'string')).toBe('last');
    expect(textFor(lines[6], 'comment')).toContain('comment first');
    expect(textFor(lines[7], 'comment')).toContain('comment last');
  });

  it('keeps TypeScript templates and language-specific tokens', () => {
    const template = tokenizeSource('const message = `first\n${value} last`;', 'typescript');
    expect(textFor(template[0], 'string')).toContain('`first');
    expect(template[1].some(span => span.kind === 'variable' || span.kind === 'plain')).toBeTrue();

    expect(tokenizeSource('<!-- first\nlast -->', 'markup').every(line => textFor(line, 'comment'))).toBeTrue();
    expect(tokenizeSource('/* first\nlast */', 'css').every(line => textFor(line, 'comment'))).toBeTrue();
    expect(tokenizeSource('$accent: red;', 'scss')[0].some(span => span.kind === 'variable')).toBeTrue();
    expect(tokenizeSource('{"enabled": true}', 'json')[0].some(span => span.kind === 'property')).toBeTrue();
    expect(tokenizeSource('# Heading', 'markdown')[0].some(span => span.kind === 'heading')).toBeTrue();
  });

  it('preserves every source character as text spans', () => {
    const source = '<script>alert("xss")</script>\n& < > " \' /';
    const lines = tokenizeSource(source, 'markup');
    expect(lines.map(line => line.map(span => span.text).join('')).join('\n')).toBe(source);
  });
});
