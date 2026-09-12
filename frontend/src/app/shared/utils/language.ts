const LANGUAGE_BY_EXTENSION: Record<string, string> = {
  cs: 'C#',
  csproj: 'C# Project',
  sln: 'Solution',
  ts: 'TypeScript',
  tsx: 'TypeScript JSX',
  js: 'JavaScript',
  jsx: 'JavaScript JSX',
  mjs: 'JavaScript',
  json: 'JSON',
  html: 'HTML',
  css: 'CSS',
  scss: 'SCSS',
  md: 'Markdown',
  yml: 'YAML',
  yaml: 'YAML',
  xml: 'XML',
  sh: 'Shell Script',
  ps1: 'PowerShell',
  py: 'Python',
  go: 'Go',
  rs: 'Rust',
  java: 'Java',
  sql: 'SQL',
  toml: 'TOML',
  gitignore: 'Ignore List',
  editorconfig: 'EditorConfig',
};

export function languageForPath(path: string | undefined | null): string {
  const fileName = (path ?? '').split('/').at(-1) ?? '';
  const dotIndex = fileName.lastIndexOf('.');
  const hasExtension = dotIndex > 0 || (dotIndex === 0 && fileName.length > 1 && fileName.indexOf('.', 1) === -1);
  const extension = hasExtension ? fileName.slice(dotIndex + 1).toLowerCase() : fileName.toLowerCase();
  return LANGUAGE_BY_EXTENSION[extension] ?? 'Plain text';
}
