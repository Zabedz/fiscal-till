/** A till key. Category must exist in config/markets.json for the market; prices include VAT. */
export interface CatalogueItem {
  sku: string;
  name: string;
  category: string;
  unitPriceCents: number;
}

export interface CatalogueGroup {
  title: string;
  items: CatalogueItem[];
}

/** Demo price lists per market, in the venue's language. */
export const Catalogue: Record<string, CatalogueGroup[]> = {
  FR: [
    {
      title: 'Entrées',
      items: [
        { sku: 'ADM-ADULTE', name: 'Entrée adulte', category: 'admission', unitPriceCents: 2900 },
        { sku: 'ADM-ENFANT', name: 'Entrée enfant', category: 'admission', unitPriceCents: 2200 },
      ],
    },
    {
      title: 'Café',
      items: [
        { sku: 'CREPE', name: 'Crêpe au sucre', category: 'food-onsite', unitPriceCents: 450 },
        { sku: 'CROQUE', name: 'Croque-monsieur', category: 'food-onsite', unitPriceCents: 850 },
        { sku: 'LIMONADE', name: 'Limonade', category: 'soft-drink-onsite', unitPriceCents: 380 },
        { sku: 'VIN', name: 'Verre de vin', category: 'alcohol', unitPriceCents: 650 },
      ],
    },
    {
      title: 'À emporter',
      items: [
        { sku: 'SANDWICH', name: 'Jambon-beurre', category: 'food-takeaway', unitPriceCents: 590 },
        { sku: 'BAGUETTE', name: 'Baguette', category: 'staple-goods', unitPriceCents: 130 },
      ],
    },
    {
      title: 'Boutique',
      items: [
        { sku: 'PELUCHE', name: 'Peluche', category: 'merchandise', unitPriceCents: 1800 },
        { sku: 'CARTE', name: 'Carte postale', category: 'merchandise', unitPriceCents: 250 },
      ],
    },
  ],
  DE: [
    {
      title: 'Eintritt',
      items: [
        { sku: 'EINTRITT-ERW', name: 'Erwachsene', category: 'admission', unitPriceCents: 3900 },
        { sku: 'EINTRITT-KIND', name: 'Kind', category: 'admission', unitPriceCents: 3200 },
      ],
    },
    {
      title: 'Imbiss',
      items: [
        { sku: 'BRATWURST', name: 'Bratwurst', category: 'food-onsite', unitPriceCents: 450 },
        { sku: 'BREZEL', name: 'Brezel', category: 'food-onsite', unitPriceCents: 350 },
        { sku: 'SCHORLE', name: 'Apfelschorle', category: 'soft-drink-onsite', unitPriceCents: 390 },
        { sku: 'BIER', name: 'Bier 0,5 l', category: 'alcohol', unitPriceCents: 550 },
      ],
    },
    {
      title: 'Zum Mitnehmen',
      items: [
        { sku: 'BROETCHEN', name: 'Belegtes Brötchen', category: 'food-takeaway', unitPriceCents: 480 },
        { sku: 'MILCH', name: 'Milch 1 l', category: 'staple-goods', unitPriceCents: 129 },
      ],
    },
    {
      title: 'Shop',
      items: [
        { sku: 'PLUESCH', name: 'Plüschtier', category: 'merchandise', unitPriceCents: 1900 },
        { sku: 'KARTE', name: 'Postkarte', category: 'merchandise', unitPriceCents: 200 },
      ],
    },
  ],
  AT: [
    {
      title: 'Eintritt',
      items: [
        { sku: 'EINTRITT-ERW', name: 'Erwachsene', category: 'admission', unitPriceCents: 2400 },
        { sku: 'EINTRITT-KIND', name: 'Kind', category: 'admission', unitPriceCents: 1600 },
      ],
    },
    {
      title: 'Café',
      items: [
        { sku: 'SACHER', name: 'Sachertorte', category: 'food-onsite', unitPriceCents: 590 },
        { sku: 'WUERSTEL', name: 'Frankfurter Würstel', category: 'food-onsite', unitPriceCents: 520 },
        { sku: 'ALMDUDLER', name: 'Almdudler', category: 'soft-drink-onsite', unitPriceCents: 390 },
        { sku: 'ACHTERL', name: 'Achterl Wein', category: 'alcohol', unitPriceCents: 450 },
      ],
    },
    {
      title: 'Zum Mitnehmen',
      items: [
        { sku: 'LEBERKAES', name: 'Leberkässemmel', category: 'food-takeaway', unitPriceCents: 450 },
        { sku: 'MILCH', name: 'Milch 1 l', category: 'staple-goods', unitPriceCents: 149 },
      ],
    },
    {
      title: 'Shop',
      items: [
        { sku: 'PLUESCH', name: 'Plüschtier', category: 'merchandise', unitPriceCents: 1900 },
        { sku: 'KARTE', name: 'Postkarte', category: 'merchandise', unitPriceCents: 200 },
      ],
    },
  ],
};
