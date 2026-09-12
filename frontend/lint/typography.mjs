import postcss from 'postcss';

export const typographyRuleId = 'quality-architecture/minimum-font-size';
export const typographyRule = {
  meta: {
    type: 'problem',
    docs: { description: 'Keep literal text sizes and typography tokens at the repository minimum.' },
    schema: [],
  },
  // CSS diagnostics are authored by the processor below, with original CSS locations.
  create: () => ({}),
};

function pixels(token) {
  const value = token.trim().toLowerCase();
  if (value === '0') return 0;
  if (!value.endsWith('px')) return null;
  const numeric = value.slice(0, -2);
  if (!numeric.trim()) return null;
  const number = Number(numeric);
  return Number.isFinite(number) ? number : null;
}

export function checkTypography(text, filename, contract) {
  const messages = [];
  let root;
  try {
    root = postcss.parse(text, { from: filename });
  } catch (error) {
    if (error.name !== 'CssSyntaxError') throw error;
    return [{ ruleId: typographyRuleId, severity: 2, fatal: true,
      message: `Cannot check the typography contract: ${error.reason}`,
      line: error.line ?? 1, column: error.column ?? 1 }];
  }
  root.walkDecls(declaration => {
    const property = declaration.prop.toLowerCase();
    const token = contract.tokenPrefixes.some(prefix => property.startsWith(prefix));
    if (property !== 'font-size' && property !== 'font' && !token) return;
    // PostCSS parses declarations and its list tokenizer preserves strings/functions.
    // Only literal px sizes (and zero) have a provable fixed rendered size here. Relative
    // units, var(), calc(), clamp() and browser-dependent keywords remain unguessed.
    const size = property === 'font'
      ? postcss.list.space(declaration.value).map(part => pixels(part.split('/')[0])).find(value => value !== null)
      : pixels(declaration.value);
    if (size === null || size === undefined || size >= contract.minimumPx) return;
    const selector = declaration.parent?.type === 'rule' ? declaration.parent.selector : null;
    if (contract.exceptions.some(exception => exception.file === filename.replaceAll('\\', '/') &&
        exception.selector === selector && exception.property === property && exception.reason.trim())) return;
    const start = declaration.source.start;
    const end = declaration.source.end;
    messages.push({
      ruleId: typographyRuleId,
      severity: 2,
      message: `Typography contract: ${property}: ${declaration.value} is below ${contract.minimumPx}px. Use a shared typography token; decorative exceptions require an exact selector and reason in lint/typography.config.mjs.`,
      line: start.line,
      column: start.column,
      endLine: end.line,
      endColumn: end.column + 1,
    });
  });
  return messages;
}

export function typographyProcessor(contract) {
  const diagnostics = new Map();
  return {
    meta: { name: 'quality-studio-typography', version: '1.0.0' },
    preprocess(text, filename) {
      diagnostics.set(filename, checkTypography(text, filename, contract));
      return [];
    },
    postprocess(_messages, filename) {
      const result = diagnostics.get(filename) ?? [];
      diagnostics.delete(filename);
      return result;
    },
    supportsAutofix: false,
  };
}
