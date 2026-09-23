import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import path from 'node:path';

export default defineConfig({
  plugins: [
    svelte(),
    {
      name: 'stable-generated-ui-line-endings',
      transformIndexHtml(html) {
        return html.replace(/\r\n?/g, '\n');
      },
      generateBundle(_options, bundle) {
        for (const output of Object.values(bundle)) {
          if (output.type === 'chunk') {
            // Svelte's whitespace-character template literal contains a literal tab
            // before a newline. Escaping those two characters preserves its value
            // while keeping generated diff whitespace checks meaningful.
            output.code = output.code
              .replace('` \t\n\\r\\f', '` \\t\\n\\r\\f')
              .trimEnd() + '\n';
          }
        }
      }
    }
  ],
  root: path.resolve(import.meta.dirname),
  base: './',
  build: {
    outDir: path.resolve(import.meta.dirname, '../src/ui'),
    emptyOutDir: true,
    target: 'es2022',
    sourcemap: false,
    rollupOptions: {
      output: {
        entryFileNames: 'app.js',
        chunkFileNames: 'chunks/[name]-[hash].js',
        assetFileNames: (assetInfo) => {
          if (assetInfo.names && assetInfo.names.some(n => n.endsWith('.css'))) {
            return 'styles.css';
          }
          if (assetInfo.name && assetInfo.name.endsWith('.css')) {
            return 'styles.css';
          }
          return 'assets/[name]-[hash][extname]';
        }
      }
    }
  },
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:5111',
        changeOrigin: true,
        headers: {
          origin: 'http://127.0.0.1:5111'
        }
      }
    }
  }
});
