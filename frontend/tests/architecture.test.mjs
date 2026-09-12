import { describe, it } from 'node:test';
import path from 'node:path';
import { RuleTester } from 'eslint';
import tseslint from 'typescript-eslint';
import { layerImports } from '../lint/architecture.mjs';
import { angularArchitecture } from '../lint/architecture.config.mjs';

RuleTester.describe = describe;
RuleTester.it = it;
const tester = new RuleTester({ languageOptions: { parser: tseslint.parser } });
const fixture = (filename, code, messageId) => ({
  filename: path.join(angularArchitecture.appRoot, filename),
  code,
  options: [angularArchitecture],
  ...(messageId ? { errors: [{ messageId }] } : {}),
});

tester.run('quality-architecture/layer-imports', layerImports, {
  valid: [
    fixture('core/api/client.ts', "import { Model } from '../models/contracts';"),
    fixture('core/api/client.ts', "import { flattenTree } from '../../shared/utils/tree-utils';"),
    fixture('shared/ui/badge.ts', "import type { Model } from '../../core/models/contracts';"),
    fixture('features/code/editor/editor.ts', "import { View } from '../container-view/container-view';"),
    fixture('features/code/editor/editor.ts', "import { Client } from '../../../core/api/client';"),
    fixture('shell/workbench/workbench.ts', "import { Editor } from '../../features/code/editor/editor';"),
    fixture('core/api/client.ts', "import { Injectable } from '@angular/core';"),
    fixture('core/api/client.ts', "const text = \"import X from '../../features/code/editor/editor'\";"),
    fixture('core/api/client.ts', "// import X from '../../features/code/editor/editor';"),
    fixture('shared/utils/tree.ts', "export type { Model } from '../../core/models/contracts';"),
  ],
  invalid: [
    fixture('core/api/client.ts', "import { Editor } from '../../features/code/editor/editor';", 'forbidden'),
    fixture('core/models/contracts.ts', "import { Client } from '../api/client';", 'forbidden'),
    fixture('shared/ui/badge.ts', "import { Client } from '../../core/api/client';", 'forbidden'),
    fixture('shared/utils/tree.ts', "import { Badge } from '../ui/badge';", 'forbidden'),
    fixture('features/code/editor/editor.ts', "import { History } from '../../reviews/run-history/run-history';", 'forbidden'),
    fixture('features/code/editor/editor.ts', "export * from '../../reviews/run-history/run-history';", 'forbidden'),
    fixture('core/api/client.ts', "export { Editor } from '../../features/code/editor/editor';", 'forbidden'),
    fixture('core/api/client.ts', "const feature = import('../../features/code/editor/editor');", 'forbidden'),
    fixture('core/api/client.ts', "type Editor = import('../../features/code/editor/editor').Editor;", 'forbidden'),
    fixture('core/api/client.ts', "import Editor = require('../../features/code/editor/editor');", 'forbidden'),
    fixture('shared/ui/badge.ts', "import { Client } from '@app/core/api/client';", 'forbidden'),
    fixture('core/api/client.ts', "import { Sneaky } from '../../../features-unchecked';", 'outside'),
    fixture('core/api/client.ts', "import { Sneaky } from '@app/../features-unchecked';", 'outside'),
    fixture('core/api/client.ts', "import { Shadow } from '../../shared/utils-shadow/anything';", 'forbidden'),
  ],
});
