import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [
    react(),
    {
      name: 'development-csp',
      transformIndexHtml(html, context) {
        return context.server
          ? html.replace(/\s*<meta http-equiv="Content-Security-Policy"[^>]+>\s*/i, '\n')
          : html;
      },
    },
  ],
  clearScreen: false,
  server: {
    strictPort: true,
    port: 5173,
  },
  envPrefix: ['VITE_', 'TAURI_', 'NL_'],
});
