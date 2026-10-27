import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';

if (location.pathname === '/') location.replace((navigator.language || '').toLowerCase().startsWith('es') ? '/es' : '/en');
document.documentElement.lang = location.pathname.startsWith('/es') ? 'es' : 'en';
bootstrapApplication(App, appConfig)
  .catch((err) => console.error(err));
