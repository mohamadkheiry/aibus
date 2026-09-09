import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { '/api': 'http://localhost:5050', '/v1': 'http://localhost:5050', '/health': 'http://localhost:5050' } },
  build: {
    rollupOptions: {
      output: {
        manualChunks: {
          'react-vendor': ['react', 'react-dom', 'react-hot-toast'],
          'charts': ['recharts'],
          'icons': ['lucide-react'],
        },
      },
    },
  },
})
