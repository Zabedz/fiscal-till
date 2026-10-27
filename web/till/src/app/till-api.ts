import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, TimeoutError, retry, throwError, timeout, timer } from 'rxjs';
import { TillConfig, VenueConfig } from './till-config';

export interface SaleLine {
  sku: string;
  description: string;
  category: string;
  quantity: number;
  unitPriceCents: number;
}

export interface SaleRequest {
  tillId: string;
  lines: SaleLine[];
  payment: { method: 'card' | 'cash'; amountCents: number };
  customerEmail?: string;
}

export interface VatTotal {
  rate: number;
  grossCents: number;
  vatCents: number;
  netCents: number;
}

export interface SaleView {
  saleId: string;
  venueId: string;
  tillId: string;
  status: 'PENDING' | 'SIGNED' | 'FAILED';
  businessDate: string;
  createdAt: string;
  currency: string;
  grossCents: number;
  vatCents: number;
  netCents: number;
  vatByRate: VatTotal[];
  lines: (SaleLine & { vatRate: number; grossCents: number })[];
  signature: { deviceId: string; counter: number; value: string; signedAt: string } | null;
  receipt: { number: number; key: string; journaledAt: string } | null;
}

const MaxSendAttempts = 6;
const FirstRetryMs = 1000;
const LongestRetryMs = 8000;
const RequestTimeoutMs = 15_000;

/** No answer, throttled, or a server fault: the same request may succeed later. */
export function isRetryable(error: unknown): boolean {
  return error instanceof TimeoutError || (error instanceof HttpErrorResponse && [0, 429, 500, 502, 503, 504].includes(error.status));
}

@Injectable({ providedIn: 'root' })
export class TillApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(TillConfig);

  /** Posts a sale; isRetryable failures resend the same Idempotency-Key and body, so a lost 202 replays the original sale. */
  create(venue: VenueConfig, idempotencyKey: string, sale: SaleRequest, onRetry: (error: unknown) => void): Observable<SaleView> {
    return this.http
      .post<SaleView>(`${this.apiUrl()}/sales`, sale, {
        headers: { 'x-api-key': venue.apiKey, 'Idempotency-Key': idempotencyKey },
      })
      .pipe(
        timeout(RequestTimeoutMs),
        retry({
          count: MaxSendAttempts - 1,
          delay: (error, attempt) => {
            if (!isRetryable(error)) {
              return throwError(() => error);
            }
            onRetry(error);
            return timer(Math.min(FirstRetryMs * 2 ** (attempt - 1), LongestRetryMs) * (1 + Math.random() / 5));
          },
        }),
      );
  }

  get(venue: VenueConfig, saleId: string): Observable<SaleView> {
    return this.http
      .get<SaleView>(`${this.apiUrl()}/sales/${saleId}`, { headers: { 'x-api-key': venue.apiKey } })
      .pipe(timeout(RequestTimeoutMs));
  }

  private apiUrl(): string {
    const settings = this.config.settings();
    if (!settings) {
      throw new Error('Till settings are not loaded');
    }
    return settings.apiUrl;
  }
}
