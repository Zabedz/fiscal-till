import { Injectable, signal } from '@angular/core';

export interface VenueConfig {
  venueId: string;
  name: string;
  market: string;
  currency: string;
  locale: string;
  timeZone: string;
  tillCount: number;
  apiKey: string;
}

export interface TillSettings {
  apiUrl: string;
  venues: VenueConfig[];
}

const SetupHint = 'Run scripts/till-config.sh, then reload the page.';

/** Runtime settings from public/config.json, written by scripts/till-config.sh and kept out of git. */
@Injectable({ providedIn: 'root' })
export class TillConfig {
  readonly settings = signal<TillSettings | null>(null);
  readonly problem = signal<string | null>(null);

  async load(): Promise<void> {
    try {
      const response = await fetch('config.json', { cache: 'no-store' });
      if (!response.ok) {
        this.problem.set(`config.json returned ${response.status}. ${SetupHint}`);
        return;
      }
      const settings = (await response.json()) as Partial<TillSettings>;
      if (!settings.apiUrl || !Array.isArray(settings.venues) || settings.venues.length === 0) {
        this.problem.set(`config.json has no API URL or no venues. ${SetupHint}`);
        return;
      }
      this.settings.set(settings as TillSettings);
    } catch (error) {
      this.problem.set(`config.json could not be read (${error instanceof Error ? error.message : String(error)}). ${SetupHint}`);
    }
  }
}
