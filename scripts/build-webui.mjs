/**
 * 面板 bundle：`src/webui/client.ts` → `dist/webui.js`，产物名写在 package.json 的
 * `cortico.webUiClient`。浏览器侧只 `import type` 框架，cortico/* 不进 bundle。
 * dist/ 下还有插件 DLL（build:plugin），这里只覆盖 webui.js。
 */
import * as esbuild from 'esbuild';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));

await esbuild.build({
  entryPoints: [{ in: `${root}src/webui/client.ts`, out: 'webui' }],
  outdir: `${root}dist`,
  bundle: true,
  format: 'esm',
  target: 'es2022',
  platform: 'browser',
  minify: true,
  logLevel: 'info',
});
