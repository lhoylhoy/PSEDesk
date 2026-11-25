import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
  },
  build: {
    rollupOptions: {
      output: {
        // The dialog chunks and the Pro table all depend on the same React + Radix runtime.
        // Splitting those vendors out means opening a dialog downloads its own code only,
        // instead of re-downloading (or duplicating) the framework in every async chunk.
        // Vite 8 bundles with rolldown, whose `manualChunks` only accepts a function.
        manualChunks(id: string): string | null {
          if (!id.includes('node_modules')) {
            return null;
          }

          if (id.includes('@radix-ui/')) {
            return 'vendor-radix';
          }

          if (/node_modules[\\/](react|react-dom|scheduler)[\\/]/.test(id)) {
            return 'vendor-react';
          }

          return null;
        },
      },
    },
  },
});
