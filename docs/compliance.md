# Fiscal rules by country

The signing partner is one mock for all three markets: a counter per till and an HMAC under a shared secret, called from
a queue after the sale. Each country requires more. Rules as in force on 28 October 2026.

## Across the three countries

| Requirement | Here |
| --- | --- |
| The sale is secured before its receipt. Germany starts a TSE transaction at the first item and finishes it before the receipt; Austria signs and logs a sale before the next one is recorded; certified French software signs at checkout and keeps signing when offline. | CreateSale answers 202 and the sale is signed later from a queue, minutes later during an outage. |
| Selling while the signer is down. Germany and Austria issue the receipt at once, marked as an outage receipt, and document the outage. France sets no procedure: certified software signs locally when offline. | Sales wait unsigned on the queue and dead-letter after 10 receives. |
| Germany and Austria: a receipt for every sale, naming the seller and its address. France: a note before payment for services of EUR 25 or more, and a receipt on request. | Receipts go out only by email, when the customer gives an address, and name the venue only. |
| Refunds and training sales are recorded; Germany also secures aborted baskets, and certified French software logs voids. | None of these exist; clearing the basket leaves no record. |
| Records kept unaltered: 10 years in Germany, 7 in Austria, 6 in France. | The journal runs in Docker on a laptop and its admin login can change it; the receipts bucket is deleted with the stack. |

## Germany: §146a AO and KassenSichV

| Requirement | Here |
| --- | --- |
| A TSE certified by the BSI. The law names no provider; fiskaly SIGN DE is one certified cloud TSE. | HMAC under a shared secret. |
| The TSE gives each sale a transaction number at its start; the signature counter rises with every signature, so at least twice per sale. | One counter per till, raised once per signed sale; no start or end time and no process type. |
| Process data `Kassenbeleg-V1`: event type, five gross containers, payments; payments of 0.00 are left out. | The same layout, with two gaps: a zero-total sale still writes `0.00:Bar` or `0.00:Unbar`, and the containers are matched by percentage, so a change to the general rate would be refused. |
| The receipt shows the seller's name and address, start and end time, transaction number, signature counter, till and TSE serial numbers, and the finish signature (KassenSichV §6), with TSE values printed exactly as the TSE returned them (AEAO to §146a, 2.4.4). | One device id, a counter and a signature, with the signed data recomputed when the receipt is rendered. |
| A Kassenabschluss per till with a non-resettable number, a DSFinV-K export and a TSE export. | An on-demand Z-report query; no exports. |
| Every till reported to the tax office through ELSTER, with its TSE (required since 2025). | Not modelled. |

Sources: [§146a AO](https://www.gesetze-im-internet.de/ao_1977/__146a.html),
[KassenSichV](https://www.gesetze-im-internet.de/kassensichv/),
[DSFinV-K 2.4](https://www.bzst.de/SharedDocs/Downloads/DE/Aussenpruefung/dsfinv_k_v_2_4.zip?__blob=publicationFile&v=19).

## Austria: RKSV and BAO §132a

| Requirement | Here |
| --- | --- |
| A signature or seal creation unit with a qualified certificate per business: a smartcard or an HSM, which a service such as fiskaly SIGN AT may run. Suite R1 signs with ES256. | HMAC under one secret for every venue. |
| Signed data: prefix `_R1-AT<n>_`, Kassen-ID, a sequential receipt number, local date and time, five amount fields, the AES-256 encrypted turnover counter, the certificate serial and a hash of the previous signed receipt. `AT0` is reserved for closed systems approved under RKSV §20. | Prefix `R1-AT0` and the five amount fields in the right order, but the 32-character sale id stands in for the receipt number, and the counter, certificate serial and chain are missing. |
| The till keeps the turnover counter, a running gross total; the signature unit only signs and may serve several tills. | The mock keeps a count of signed sales in the signer, one per till. |
| Every receipt carries a QR code of the signed data and signature. Since 1 October 2026 an electronic receipt counts if the customer can open it right after paying; a printed one is due on request. | No QR code; receipts go out by email only. |
| Start, monthly, annual, closing and zero receipts. During an outage, receipts marked "Sicherheitseinrichtung ausgefallen" and a signed Sammelbeleg afterwards; outages beyond 48 hours reported. | None of these. |
| Each receipt enters the data log (DEP) before the next sale; quarterly backup; DEP7 export; tills and signature units registered in FinanzOnline. | None of these. |

Sources: [RKSV](https://www.ris.bka.gv.at/GeltendeFassung.wxe?Abfrage=Bundesnormen&Gesetzesnummer=20009390),
[BAO §132a](https://www.ris.bka.gv.at/NormDokument.wxe?Abfrage=Bundesnormen&Gesetzesnummer=10003940&Paragraf=132a),
[BMF Erlass on tills](https://findok.bmf.gv.at/findok/iwg/84/84683/84683.1.pdf).

## France: CGI 286 I 3° bis

| Requirement | Here |
| --- | --- |
| The till software meets four conditions: inalterability, security, conservation and archiving. Proof is an NF525 or LNE certificate, or the publisher's attestation, accepted again since 21 February 2026. There is no state signer, fiscal device or till registration. | A remote mock signer; no certificate or attestation. |
| Each record is signed with the register's sequence number and the previous signature; certified products use RSA or ECDSA with a certificate. | Each sale is signed on its own with an HMAC; the per-till counter is signed, but nothing is chained. |
| Daily, monthly and annual closures that store period and perpetual grand totals, signed and chained. | An on-demand Z-report query. |
| A technical event log (sign-ins, voids, copies, closures, archives), and copies marked as duplicates. | None of these. |
| Services of EUR 25 or more get a note before payment, with the seller's name and address and each item's price. Receipts are printed only on request, with a notice at the till. | An English HTML receipt by email. |
| B2C VAT totals per day and rate, sent through an approved platform: from 1 September 2026 for large and mid-sized companies, from 1 September 2027 for SMEs. | Not modelled. |

Sources: [CGI 286](https://www.legifrance.gouv.fr/codes/article_lc/LEGIARTI000042914666),
[arrêté 83-50/A](https://www.legifrance.gouv.fr/loda/id/JORFTEXT000000494187/),
[e-reporting](https://www.impots.gouv.fr/e-reporting-donnees-de-transaction). NF525's rules are not public; the
certified practice above follows
[Microsoft's NF525 implementation](https://learn.microsoft.com/en-us/dynamics365/commerce/localizations/emea-fra-cash-registers).

## VAT

The rates in [config/markets.json](https://github.com/Zabedz/fiscal-till/blob/main/config/markets.json) match the law
for the till's items and assume an attraction-type venue.

| Country | Where the rate depends on the venue or the item |
| --- | --- |
| DE | Admission is 19% for trampoline parks and attractions, 7% for swimming pools and zoos; sauna 19%. Food on site has been 7% since 1 January 2026; drinks stay 19%. |
| AT | Admission is 13% for venues with a variety of rides, pools and zoos; a single-activity hall is probably 20%, though no ruling was found. 4.9% applies from 1 July 2026 only to the staples listed in Anlage 3 when sold as goods; served, they stay 10%. |
| FR | Admission is 10% for amusement and water parks, 5.5% for zoos and 20% for sports facilities; trampoline parks are unsettled between 10% and 20%. Takeaway food is 10% when prepared for immediate consumption, 5.5% otherwise, except confectionery and chocolate at 20%; takeaway soft drinks are 10% in an open cup and 5.5% sealed. |

Sources: [DE §12 UStG](https://www.gesetze-im-internet.de/ustg_1980/__12.html),
[AT §10 UStG](https://www.ris.bka.gv.at/NormDokument.wxe?Abfrage=Bundesnormen&Gesetzesnummer=10004873&Paragraf=10),
[FR CGI 279](https://www.legifrance.gouv.fr/codes/article_lc/LEGIARTI000048827223).
