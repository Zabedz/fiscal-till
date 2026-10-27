import { registerLocaleData } from '@angular/common';
import localeDe from '@angular/common/locales/de';
import localeDeAt from '@angular/common/locales/de-AT';
import localeFr from '@angular/common/locales/fr';
import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { appConfig } from './app/app.config';

// The currency, date and number pipes throw for a venue locale that is not registered here.
registerLocaleData(localeFr);
registerLocaleData(localeDe);
registerLocaleData(localeDeAt);

bootstrapApplication(App, appConfig).catch((error) => console.error(error));
