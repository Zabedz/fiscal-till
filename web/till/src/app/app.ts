import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { Subscription, catchError, defer, filter, fromEvent, of, repeat, takeUntil, takeWhile, timer } from 'rxjs';
import { Catalogue, CatalogueItem } from './catalogue';
import { SaleRequest, SaleView, TillApi } from './till-api';
import { TillConfig, VenueConfig } from './till-config';

interface BasketLine {
  item: CatalogueItem;
  quantity: number;
}

/** A paid sale kept on this till until the server's answer is final; saleId is set once the till service accepted it. */
interface StoredSale {
  key: string;
  venueId: string;
  request: SaleRequest;
  paidAt: string;
  saleId?: string;
}

/** A paid sale on screen, followed until the journal gives it a receipt number. */
interface TrackedSale {
  key: string;
  venue: VenueConfig;
  request: SaleRequest;
  paidAt: string;
  progress: 'sending' | 'retrying' | 'held' | 'tracking' | 'refused';
  view: SaleView | null;
  note: string | null;
}

interface PaperLine {
  quantity: number;
  description: string;
  grossCents: number;
}

type Step = 'done' | 'current' | 'waiting' | 'failed';

const StoragePrefix = 'till.sale.';
const AcceptedSaleKeptMs = 24 * 60 * 60 * 1000;
const ResendSweepMs = 30_000;
const PollIntervalMs = 1500;
const FastPolls = 8;
const SlowestPollMs = 30_000;
const PollLimitMs = 20 * 60 * 1000;
const ClockTickMs = 15_000;
const SalesShown = 12;
const ReceiptDigits = 8;
const MaxQuantity = 99;
const MarketNames: Record<string, string> = { FR: 'France', DE: 'Germany', AT: 'Austria' };

@Component({
  selector: 'app-root',
  imports: [CurrencyPipe, DatePipe, DecimalPipe],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly api = inject(TillApi);
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);
  protected readonly config = inject(TillConfig);

  protected readonly venues = computed(() => this.config.settings()?.venues ?? []);
  private readonly venueId = signal<string | null>(null);
  protected readonly venue = computed(() => this.venues().find((v) => v.venueId === this.venueId()) ?? this.venues()[0] ?? null);
  protected readonly tillId = signal('T01');
  protected readonly tills = computed(() =>
    Array.from({ length: this.venue()?.tillCount ?? 0 }, (_, index) => `T${String(index + 1).padStart(2, '0')}`),
  );
  protected readonly groups = computed(() => Catalogue[this.venue()?.market ?? ''] ?? []);

  protected readonly basket = signal<BasketLine[]>([]);
  protected readonly total = computed(() => this.basket().reduce((sum, line) => sum + line.item.unitPriceCents * line.quantity, 0));
  protected readonly email = signal('');
  protected readonly emailValid = signal(true);
  protected readonly canPay = computed(() => this.basket().length > 0 && this.emailValid());

  protected readonly sales = signal<TrackedSale[]>([]);
  private readonly selectedKey = signal<string | null>(null);
  protected readonly selected = computed(() => this.sales().find((sale) => sale.key === this.selectedKey()) ?? null);
  protected readonly salesForUnknownVenues = signal(0);

  protected readonly now = signal(new Date());
  protected readonly marketNames = MarketNames;

  private readonly polls = new Map<string, Subscription>();
  private readonly sending = new Set<string>();

  constructor() {
    const clock = setInterval(() => this.now.set(new Date()), ClockTickMs);
    const sweep = setInterval(() => this.resendStored(), ResendSweepMs);
    const online = fromEvent(window, 'online').subscribe(() => this.resendStored());
    inject(DestroyRef).onDestroy(() => {
      clearInterval(clock);
      clearInterval(sweep);
      online.unsubscribe();
      this.polls.forEach((poll) => poll.unsubscribe());
    });
    this.restoreStored();
  }

  protected chooseVenue(venueId: string): void {
    this.venueId.set(venueId);
    this.tillId.set('T01');
    this.basket.set([]);
    this.selectedKey.set(null);
  }

  protected add(item: CatalogueItem): void {
    this.selectedKey.set(null);
    this.basket.update((lines) =>
      lines.some((line) => line.item.sku === item.sku)
        ? lines.map((line) => (line.item.sku === item.sku ? { ...line, quantity: Math.min(line.quantity + 1, MaxQuantity) } : line))
        : [...lines, { item, quantity: 1 }],
    );
  }

  protected removeOne(sku: string): void {
    const index = this.basket().findIndex((line) => line.item.sku === sku);
    const removesLine = this.basket()[index]?.quantity === 1;
    this.basket.update((lines) =>
      lines.map((line) => (line.item.sku === sku ? { ...line, quantity: line.quantity - 1 } : line)).filter((line) => line.quantity > 0),
    );
    if (removesLine) {
      // The focused button leaves the page with its line; keep focus on the basket, or on the keys once it is empty.
      this.afterRender(() => {
        const remaining = this.host.nativeElement.querySelectorAll<HTMLButtonElement>('.less');
        (remaining[Math.min(index, remaining.length - 1)] ?? this.host.nativeElement.querySelector<HTMLButtonElement>('.keycap'))?.focus();
      });
    }
  }

  protected setEmail(input: HTMLInputElement): void {
    this.email.set(input.value);
    this.emailValid.set(input.validity.valid);
  }

  protected clear(): void {
    this.basket.set([]);
    this.email.set('');
    this.emailValid.set(true);
  }

  protected closeSale(): void {
    this.selectedKey.set(null);
  }

  protected show(key: string): void {
    this.selectedKey.set(key);
  }

  protected pay(method: 'card' | 'cash'): void {
    const venue = this.venue();
    if (!venue || !this.canPay()) {
      return;
    }

    const email = this.email().trim();
    const stored: StoredSale = {
      key: crypto.randomUUID(),
      venueId: venue.venueId,
      paidAt: new Date().toISOString(),
      request: {
        tillId: this.tillId(),
        lines: this.basket().map(({ item, quantity }) => ({
          sku: item.sku,
          description: item.name,
          category: item.category,
          quantity,
          unitPriceCents: item.unitPriceCents,
        })),
        payment: { method, amountCents: this.total() },
        ...(email ? { customerEmail: email } : {}),
      },
    };

    this.store(stored);
    this.clear();
    this.send(stored, venue);
    this.selectedKey.set(stored.key);
    this.afterRender(() => {
      this.host.nativeElement.querySelector<HTMLButtonElement>('.clear')?.focus();
      this.host.nativeElement.querySelector('.printer')?.scrollIntoView({ block: 'nearest' });
    });
  }

  /** Lines for the paper: the server's once it answered, the till's own until then. */
  protected paperLines(sale: TrackedSale): PaperLine[] {
    return sale.view
      ? sale.view.lines.map((line) => ({ quantity: line.quantity, description: line.description, grossCents: line.grossCents }))
      : sale.request.lines.map((line) => ({
          quantity: line.quantity,
          description: line.description,
          grossCents: line.unitPriceCents * line.quantity,
        }));
  }

  /** UTC offset of the venue at that instant, for the date pipe, so times show in the venue's own clock. */
  protected offset(venue: VenueConfig, at: string | Date): string {
    const name = new Intl.DateTimeFormat('en-US', { timeZone: venue.timeZone, timeZoneName: 'longOffset' })
      .formatToParts(new Date(at))
      .find((part) => part.type === 'timeZoneName')?.value;
    const match = /GMT([+-]\d{2}):(\d{2})/.exec(name ?? '');
    return match ? `${match[1]}${match[2]}` : '+0000';
  }

  protected accepted(sale: TrackedSale): Step {
    return sale.view ? 'done' : sale.progress === 'refused' ? 'failed' : 'current';
  }

  protected signed(sale: TrackedSale): Step {
    if (!sale.view) {
      return 'waiting';
    }
    return sale.view.status === 'FAILED' ? 'failed' : sale.view.signature ? 'done' : 'current';
  }

  protected numbered(sale: TrackedSale): Step {
    return sale.view?.receipt ? 'done' : sale.view?.signature ? 'current' : 'waiting';
  }

  /** Colour of a sale's status: one of the four status inks, or none while it is being sent. */
  protected tone(sale: TrackedSale): 'sending' | 'waiting' | 'signed' | 'numbered' | 'failed' {
    if (sale.progress === 'refused' || sale.view?.status === 'FAILED') {
      return 'failed';
    }
    if (!sale.view) {
      return sale.progress === 'held' ? 'waiting' : 'sending';
    }
    return sale.view.receipt ? 'numbered' : sale.view.signature ? 'signed' : 'waiting';
  }

  protected receiptNumber(value: number): string {
    return String(value).padStart(ReceiptDigits, '0');
  }

  protected summary(sale: TrackedSale): string {
    if (sale.progress === 'refused') {
      return 'Refused';
    }
    if (!sale.view) {
      return { sending: 'Sending', retrying: 'Resending', held: 'Not sent yet', tracking: 'Checking', refused: 'Refused' }[sale.progress];
    }
    if (sale.view.status === 'FAILED') {
      return 'Signature refused';
    }
    if (sale.view.receipt) {
      return `Receipt ${this.receiptNumber(sale.view.receipt.number)}`;
    }
    return sale.view.signature ? 'Signed' : 'Waiting for signature';
  }

  private send(stored: StoredSale, venue: VenueConfig): void {
    const { key, request, paidAt } = stored;
    if (this.sending.has(key)) {
      return;
    }
    this.sending.add(key);
    this.track({ key, venue, request, paidAt, progress: 'sending', view: null, note: null });

    this.api.create(venue, key, request, (error) => this.update(key, { progress: 'retrying', note: describe(error) })).subscribe({
      next: (view) => {
        this.sending.delete(key);
        this.store({ ...stored, saleId: view.saleId });
        this.update(key, { progress: 'tracking', view, note: null });
        this.poll(key, venue, view.saleId);
      },
      error: (error: unknown) => {
        this.sending.delete(key);
        if (error instanceof HttpErrorResponse && (error.status === 400 || error.status === 422)) {
          this.unstore(key);
          this.update(key, { progress: 'refused', note: describe(error) });
          this.returnToBasket(stored, venue);
        } else {
          const kept = this.isStored(key)
            ? 'The sale is kept on this till and sent again automatically.'
            : 'This till could not keep the sale; ring it up again.';
          this.update(key, { progress: 'held', note: `${describe(error)} ${kept}` });
        }
      },
    });
  }

  private poll(key: string, venue: VenueConfig, saleId: string): void {
    this.polls.get(key)?.unsubscribe();
    let settled = false;
    this.polls.set(
      key,
      defer(() => this.api.get(venue, saleId).pipe(catchError(() => of(null))))
        .pipe(
          repeat({ delay: (polls) => timer(pollDelay(polls)) }),
          filter((view): view is SaleView => view !== null),
          takeWhile((view) => !isSettled(view), true),
          takeUntil(timer(PollLimitMs)),
        )
        .subscribe({
          next: (view) => {
            this.update(key, { view });
            if (isSettled(view)) {
              settled = true;
              this.unstore(key);
            }
          },
          complete: () => {
            this.polls.delete(key);
            if (!settled) {
              this.update(key, { note: 'Stopped checking after 20 minutes. The sale is stored; reload to check again.' });
            }
          },
        }),
    );
  }

  /** Puts a refused sale's items back so the cashier can correct and pay again. */
  private returnToBasket(stored: StoredSale, venue: VenueConfig): void {
    if (venue.venueId !== this.venue()?.venueId || this.basket().length > 0) {
      return;
    }
    const items = Catalogue[venue.market].flatMap((group) => group.items);
    this.basket.set(
      stored.request.lines.flatMap((line) => {
        const item = items.find((candidate) => candidate.sku === line.sku);
        return item ? [{ item, quantity: line.quantity }] : [];
      }),
    );
    this.email.set(stored.request.customerEmail ?? '');
  }

  private track(sale: TrackedSale): void {
    const existing = this.sales().find((candidate) => candidate.key === sale.key);
    if (existing) {
      this.update(sale.key, { progress: sale.progress, note: sale.note });
      return;
    }
    const kept = [sale, ...this.sales()];
    // Sales pushed off the strip stop polling here; they stay stored and resume after a reload.
    kept.slice(SalesShown).forEach((dropped) => {
      this.polls.get(dropped.key)?.unsubscribe();
      this.polls.delete(dropped.key);
    });
    this.sales.set(kept.slice(0, SalesShown));
  }

  private update(key: string, change: Partial<TrackedSale>): void {
    this.sales.update((sales) => sales.map((sale) => (sale.key === key ? { ...sale, ...change } : sale)));
  }

  private restoreStored(): void {
    let unknownVenue = 0;
    for (const stored of this.readStored()) {
      const venue = this.venues().find((v) => v.venueId === stored.venueId);
      if (!venue) {
        unknownVenue++;
      } else if (!stored.saleId) {
        this.send(stored, venue);
      } else if (Date.now() - Date.parse(stored.paidAt) > AcceptedSaleKeptMs) {
        this.unstore(stored.key);
      } else {
        this.track({ key: stored.key, venue, request: stored.request, paidAt: stored.paidAt, progress: 'tracking', view: null, note: null });
        this.poll(stored.key, venue, stored.saleId);
      }
    }
    this.salesForUnknownVenues.set(unknownVenue);
  }

  private resendStored(): void {
    for (const stored of this.readStored()) {
      const venue = this.venues().find((v) => v.venueId === stored.venueId);
      if (venue && !stored.saleId && !this.sending.has(stored.key)) {
        this.send(stored, venue);
      }
    }
  }

  private afterRender(action: () => void): void {
    afterNextRender(action, { injector: this.injector });
  }

  private readStored(): StoredSale[] {
    const stored: StoredSale[] = [];
    try {
      for (let index = 0; index < localStorage.length; index++) {
        const name = localStorage.key(index);
        if (name?.startsWith(StoragePrefix)) {
          try {
            stored.push(JSON.parse(localStorage.getItem(name) ?? ''));
          } catch (error) {
            if (!(error instanceof SyntaxError)) {
              throw error;
            }
          }
        }
      }
    } catch (error) {
      if (!(error instanceof DOMException)) {
        throw error;
      }
    }
    return stored.sort((a, b) => a.paidAt.localeCompare(b.paidAt));
  }

  private store(sale: StoredSale): void {
    try {
      localStorage.setItem(StoragePrefix + sale.key, JSON.stringify(sale));
    } catch (error) {
      // Blocked or full storage: the sale is still sent, and isStored() tells the cashier it is not kept.
      if (!(error instanceof DOMException)) {
        throw error;
      }
    }
  }

  private unstore(key: string): void {
    try {
      localStorage.removeItem(StoragePrefix + key);
    } catch (error) {
      if (!(error instanceof DOMException)) {
        throw error;
      }
    }
  }

  private isStored(key: string): boolean {
    try {
      return localStorage.getItem(StoragePrefix + key) !== null;
    } catch (error) {
      if (error instanceof DOMException) {
        return false;
      }
      throw error;
    }
  }
}

function isSettled(view: SaleView): boolean {
  return view.receipt !== null || view.status === 'FAILED';
}

/** 1.5 s while signing normally completes, then doubling to 30 s so a stalled pipeline does not use up the venue's quota. */
function pollDelay(polls: number): number {
  return polls < FastPolls ? PollIntervalMs : Math.min(PollIntervalMs * 2 ** (polls - FastPolls + 1), SlowestPollMs);
}

function describe(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return 'The till service did not answer in time.';
  }
  switch (error.status) {
    case 0:
      return 'No connection to the till service.';
    case 429:
      return 'This venue is sending more requests than its limit allows.';
    case 400:
    case 422: {
      const body = error.error as { message?: string; errors?: string[] } | null;
      return [body?.message, ...(body?.errors ?? [])].filter(Boolean).join(': ') || `Refused with status ${error.status}.`;
    }
    default:
      return error.status >= 500 ? 'The till service did not confirm the sale.' : `The till service answered with status ${error.status}.`;
  }
}
