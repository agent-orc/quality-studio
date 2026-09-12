import path from 'node:path';
import { typographyRule } from './typography.mjs';

const normalize = value => value.replaceAll('\\', '/');
const within = (value, prefix) => value === prefix || value.startsWith(`${prefix}/`);

/**
 * Repository-local AST rule, deliberately configured by its consumer. It follows static
 * imports, re-exports, literal dynamic imports and import types; it does not guess at
 * runtime-generated module names. Package imports remain the package manager's concern.
 */
export const layerImports = {
  meta: {
    type: 'problem',
    docs: { description: 'Respect the repository-declared Angular dependency direction.' },
    schema: [{
      type: 'object',
      additionalProperties: false,
      required: ['appRoot', 'layers'],
      properties: {
        appRoot: { type: 'string', minLength: 1 },
        layers: {
          type: 'array',
          minItems: 1,
          items: {
            type: 'object',
            additionalProperties: false,
            required: ['path', 'allow'],
            properties: {
              path: { type: 'string', minLength: 1 },
              allow: { type: 'array', items: { type: 'string', minLength: 1 } },
            },
          },
        },
        aliases: { type: 'object', additionalProperties: { type: 'string' } },
      },
    }],
    messages: {
      forbidden: "Architecture boundary: '{{source}}' must not depend on '{{target}}'. Allowed: {{allowed}}. Move the dependency to its owning layer or compose features in shell.",
      outside: "Architecture boundary: local import '{{target}}' leaves the declared app root. Declare a deliberate boundary instead of bypassing the layer contract.",
    },
  },
  create(context) {
    const options = context.options[0];
    const appRoot = path.resolve(options.appRoot);
    const filename = path.resolve(context.filename);
    const source = normalize(path.relative(appRoot, filename));
    const layers = [...options.layers].sort((left, right) => right.path.length - left.path.length);
    const layer = layers.find(candidate => within(source, candidate.path));
    if (!layer) return {};
    const ownFeature = source.startsWith('features/') ? source.split('/').slice(0, 2).join('/') : null;
    const allowed = layer.allow.map(value => value === '$feature' ? ownFeature : value).filter(Boolean);
    const aliases = Object.entries(options.aliases ?? {}).sort(([left], [right]) => right.length - left.length);

    function check(node) {
      const specifier = node?.value;
      if (typeof specifier !== 'string') return;
      let target;
      if (specifier.startsWith('.')) {
        target = normalize(path.relative(appRoot, path.resolve(path.dirname(filename), specifier)));
      } else {
        const alias = aliases.find(([prefix]) => specifier.startsWith(prefix));
        if (!alias) return;
        target = normalize(path.relative(appRoot, path.resolve(appRoot, alias[1], specifier.slice(alias[0].length))));
      }
      if (target === '..' || target.startsWith('../') || path.isAbsolute(target)) {
        context.report({ node, messageId: 'outside', data: { target: specifier } });
      } else if (!allowed.some(prefix => within(target, prefix))) {
        context.report({ node, messageId: 'forbidden', data: { source, target, allowed: allowed.join(', ') } });
      }
    }

    return {
      ImportDeclaration: node => check(node.source),
      ExportNamedDeclaration: node => check(node.source),
      ExportAllDeclaration: node => check(node.source),
      ImportExpression: node => check(node.source),
      TSImportType: node => check(node.argument?.literal ?? node.argument ?? node.source),
      TSImportEqualsDeclaration: node => check(node.moduleReference?.expression),
    };
  },
};

export default { rules: { 'layer-imports': layerImports, 'minimum-font-size': typographyRule } };
