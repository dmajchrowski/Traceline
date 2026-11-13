import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The built UI is served by the .NET app from its wwwroot folder.
// During UI development, `npm run dev` proxies API calls to a running Traceline on port 7878.
export default defineConfig({
  plugins: [react()],
  build: { outDir: '../src/Traceline.App/wwwroot', emptyOutDir: true },
  server: { proxy: { '/api': 'http://localhost:7878' } },
})
