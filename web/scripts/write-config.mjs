import { writeFileSync } from 'node:fs';
const value = process.env.CORPUSLENS_API_BASE_URL || 'https://api.corpuslens.cobollatin.com/api/v1';
if (!/^https:\/\/[^\s]+\/api\/v1\/?$/.test(value) && value !== '/api/v1') throw new Error('CORPUSLENS_API_BASE_URL must be an HTTPS /api/v1 URL.');
writeFileSync(new URL('../public/config.js', import.meta.url), `globalThis.CORPUSLENS_API_BASE_URL = ${JSON.stringify(value.replace(/\/$/, ''))};\n`);
