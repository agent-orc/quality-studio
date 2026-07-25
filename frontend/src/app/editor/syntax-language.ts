import { SyntaxLanguage } from './syntax-types';

const SYNTAX_LANGUAGE_BY_EXTENSION: Record<string, SyntaxLanguage> = {
  cs: 'csharp',
  ts: 'typescript',
  tsx: 'typescript',
  html: 'markup',
  htm: 'markup',
  css: 'css',
  scss: 'scss',
  json: 'json',
  md: 'markdown',
  markdown: 'markdown',
};

export function syntaxLanguageForPath(path: string | undefined | null): SyntaxLanguage | null {
  const fileName = (path ?? '').split('/').at(-1) ?? '';
  const dotIndex = fileName.lastIndexOf('.');
  if (dotIndex < 0 || dotIndex === fileName.length - 1) return null;
  return SYNTAX_LANGUAGE_BY_EXTENSION[fileName.slice(dotIndex + 1).toLowerCase()] ?? null;
}
