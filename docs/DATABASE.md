# TruckScale – Database: old (DATA.LIS) and new (SQLite)

This document describes the database of the old **TS.exe** (LIS120, VB6) program, the new SQLite schema that replaces it, and how data is migrated from one to the other.

---

## 1. The old database: `DATA.LIS`

- Format: **Microsoft Access 2000 / Jet 4.0** (`.mdb`, renamed to `.LIS`). No password. Schema version `VERSION.DBF_VERS = 4.4` (2022).
- It can only be opened with the 32-bit `Microsoft.Jet.OLEDB.4.0` driver, which is Windows-only. This is why the new program uses SQLite.
- Text fields are fixed width and **padded with spaces** (`"Árpa                "`), and weights are stored **as text** (`" 25920"`).
- Rows are soft-deleted: `DEL_MARK = True` means deleted.
- The empty date is `1899-12-30`, the Access "zero date".

### Copies found on this PC

| Path | Weighings | Last changed | Note |
|---|---:|---|---|
| `Documents\Truck Scale\AppdataVirtualStore\DATA.LIS` | **413** (ticket no. 4–416) | 2023-12-08 | **Production database, the migration source** |
| `Documents\TruckScale-0004-test\FILES\DATA.LIS` (+ VirtualStore copies) | 66 | 2026-11-04 | Test copy that branched off in Aug 2022; its 8 extra rows are test weighings from 2023-09-22 |
| `Documents\Truck Scale\Files\DATA.LIS`, `TruckScale\DATA.LIS` | 0 | – | Empty template |

### Tables

#### `W_RECEIPTS` – weighings (mérlegjegyek), 413 rows
| Column | Type | Meaning / observation |
|---|---|---|
| `ID` | autonumber | Record ID |
| `TicketNumber` | text(50) | Ticket number; so far it always equals `ID` |
| `OPER_TYPE` | int | Direction: **2 = Beszállítás (290 rows), 1 = Kiszállítás (123 rows)**. Established from the seasons: type 2 is concentrated in the harvest months (Jun–Sep), type 1 in winter (selling) |
| `ORG_ID` | int | → `ORGANIZATIONS.ID` (customer) |
| `ORG_ID_2` | int | Always empty or equal to `ORG_ID`, so it adds nothing |
| `CAR_ID` | int | → `CARS.ID` (registration plate) |
| `DRV_ID` | int | → `DRIVERS.ID` (driver) |
| `ART_ID` | int | → `ARTICLE.ID` (material) |
| `OPER_ID` | int | Operator, always 0 (not used) |
| `W_DATE_IN` + `W_TIME_IN` | date + time | Time of the 1st weighing. **Empty in 78 rows**, where the stored tare was used ("Használ") |
| `W_DATE_OUT` + `W_TIME_OUT` | date + time | Time of the 2nd (closing) weighing, always filled |
| `CAR_W` | text(10) | **Tare** (kg) |
| `ART_W` | text(10) | **Net** (kg) |
| `BRUTTO_W` | text(10) | **Gross** (kg). `CAR_W + ART_W = BRUTTO_W` holds in all 413 rows |
| `CALC_ART_W` | text(10) | Net recalculated for moisture; 0 in the stored-tare rows. Not needed |
| `ART_H` | text(10) | Measured moisture %, almost always 0. Not needed |
| `ART_REGION` | int | → `REGIONS` (Megjelölés), always 0 |
| `CHECK_W`, `W_ByHand`, `T_ByHand` | bool / text | Check weighing / manual weight / manual tare. All empty/0 |
| `DEL_MARK` | bool | Deleted, always False |

#### `TM_CARS` – trucks in the yard (after the 1st weighing), currently 0 rows
`ID, CAR_ID, CAR_NUM, CAR_TYPE, OPER_TYPE, ORG_ID, OPER_ID, ART_ID, DRV_ID, WEIGHT (1st weighing), DATE_IN, TIME_IN, ART_HUMI`.
When the 2nd weighing happens, the row moves from here into `W_RECEIPTS`.

#### `ORGANIZATIONS` – customers (Ügyfél), 27 rows
`ID, ORG_TYPE, ORG_NAME, ORG_ADR_TOWN, ORG_ADR_STREET, ORG_ADR_STREET_NUM(5), ORG_ADR_ZIP(6), ORG_LAST_CAR_ID, ORG_LAST_DRIVER_ID, ORG_LAST_DATE, DEL_MARK`
- `ORG_TYPE`: 10 = the built-in "UNKNOWN" placeholder, 0 = customer, **2 = own company** (Gunics Zsolt, the ticket header details)
- The same customer appears more than once (e.g. "Gunics Zsolt" 3 times).

#### `CARS` – registration plates, 133 rows
`ID, ORG_ID, CAR_NUM, LAST_W_DATE, LAST_W_TIME, LAST_CAR_W (last tare), LAST_W_ARTICLE_ID, DEL_MARK`
- **Tied to a customer** (`ORG_ID`), so the same truck was entered separately for each customer, with different spellings:
  `JOY-590/YJA-694`, `JOY590/YJA694`, `JOY590-YJA694`, `JOY-590/YJA694` …
- `LAST_CAR_W` is the tare used by the "Használ" checkbox.

#### `DRIVERS` – drivers, 92 rows
`ID, ORG_ID, NAME, SURNAME, DEL_MARK`
- Also tied to a customer, so "Gunics Zsolt" appears many times.
- `NAME` holds the **family name** and `SURNAME` the **given name** (the labels in the old program were swapped).

#### `ARTICLE` – materials (Anyag), 23 rows
`ID, ART_NAME, ART_CODE (Megjegyzés/"Kódja"), ART_REGION (→ REGIONS, Megjelölés), ART_HUMI, ART_STD_HUMI (standard moisture %), DEL_MARK`

#### `REGIONS` – Megjelölés list, 3 rows
`ID, REG_NAME, DEL_MARK` (e.g. "Búza malmi", "Búza euro"). Unused (`ART_REGION` is always 0), so it is **not migrated**. In the new program Megjelölés is free text on the weighing.

#### Other tables (not used / not migrated)
| Table | Content |
|---|---|
| `OPERATORS` | Operators, empty |
| `Users` | Logins, with 1 encrypted row; roles are switched off in TS.ini (`Access_Roles=0`) |
| `TICKETS` | Ticket layout in the database, empty; the layout is in the `.form` file |
| `T_NUMBERS` | Ticket counter, empty |
| `VERSION` | Schema version |

---

## 2. The new database: SQLite (`truckscale.db`)

Main changes:
- **Registration plates and drivers are independent of the customer.** One truck, one driver, any number of customers.
- Weights are stored as **integers**, dates and times as **one ISO timestamp**.
- **Trucks in the yard** are not a separate table: they are the weighings with `status = 'OPEN'`.
- The weighing **stores the names at the time of weighing as well** (snapshot), so a reprinted ticket shows the original data even if the customer is renamed later.
- Moisture calculation is dropped. The standard % is only a comment on the material.

```
partners ─┐
vehicles ─┼──< weighings
drivers  ─┤
materials ┘
settings (key/value)
```

### `partners` – customers
| Column | Type | Note |
|---|---|---|
| `id` | INTEGER PK | |
| `name` | TEXT NOT NULL | |
| `zip`, `town`, `street` | TEXT | `street` = old street + house number |
| `note` | TEXT | |
| `active` | INTEGER (0/1) | Instead of deleting |
| `legacy_ids` | TEXT | Old `ORGANIZATIONS.ID`s (comma-separated) for traceability |

### `vehicles` – registration plates
| Column | Type | Note |
|---|---|---|
| `id` | INTEGER PK | |
| `plate` | TEXT NOT NULL | As displayed, e.g. `NLB381/YKK063` |
| `plate_key` | TEXT UNIQUE | Normalised: uppercase, letters and digits only, `O`→`0`. Prevents duplicates |
| `last_tare` | INTEGER | Last tare (kg), used by "Használ" |
| `last_tare_at` | TEXT | When it was measured |
| `default_driver_id` | INTEGER → drivers | Suggested driver for this plate |
| `active` | INTEGER | |
| `legacy_ids` | TEXT | Old `CARS.ID`s |

### `drivers` – drivers
| Column | Type | Note |
|---|---|---|
| `id` | INTEGER PK | |
| `family_name` | TEXT NOT NULL | Vezetéknév (old `NAME`) |
| `given_name` | TEXT | Keresztnév (old `SURNAME`) |
| `active` | INTEGER | |
| `legacy_ids` | TEXT | Old `DRIVERS.ID`s |

### `materials` – materials
| Column | Type | Note |
|---|---|---|
| `id` | INTEGER PK | |
| `name` | TEXT NOT NULL | |
| `note` | TEXT | Old `ART_CODE` (Megjegyzés / Kódja) |
| `comment` | TEXT | E.g. "13%" (old `ART_STD_HUMI`), a comment only |
| `active` | INTEGER | |
| `legacy_ids` | TEXT | |

### `weighings` – weighings / tickets
| Column | Type | Note |
|---|---|---|
| `id` | INTEGER PK | |
| `ticket_no` | INTEGER UNIQUE | Ticket number; assigned when the weighing is closed |
| `direction` | TEXT | `IN` (Beszállítás) / `OUT` (Kiszállítás) |
| `status` | TEXT | `OPEN` (in the yard) / `DONE` / `VOID` (Stornó: cancelled, kept but not counted) |
| `partner_id`, `vehicle_id`, `driver_id`, `material_id` | INTEGER FK | |
| `partner_name`, `partner_address`, `plate`, `driver_name`, `material_name`, `material_note` | TEXT | Snapshot at the time of weighing |
| `marking` | TEXT | Megjelölés, free text |
| `first_weight`, `first_at` | INTEGER, TEXT | 1st weighing |
| `second_weight`, `second_at` | INTEGER, TEXT | 2nd weighing (empty while `OPEN`) |
| `closed_at` | TEXT | When the ticket was closed (`DONE`) or cancelled (`VOID`); reports filter on this. With "Használ" there is only a 1st weighing, and `closed_at` = `first_at` |
| `gross`, `tare`, `net` | INTEGER | Computed when closing. IN: 1st = gross, 2nd = tare; OUT: the reverse |
| `stored_tare` | INTEGER (0/1) | 1 = "Használ": the vehicle's stored tare was used, with only one weighing |
| `operator` | TEXT | Who weighed |
| `note` | TEXT | |
| `legacy_id` | INTEGER | Old `W_RECEIPTS.ID` |

### Editing rules
| State | What can change |
|---|---|
| New weighing (before Mérés) | Everything: direction, customer, plate, driver, material, Megjelölés, "Használ" |
| `OPEN` (after the 1st Mérés, truck in the yard) | **Read-only**; only the 2nd weighing can be recorded, or **Stornó** (→ `VOID`), then start again correctly |
| `DONE` (closed, ticket printed) | Read-only; can be reprinted |

The program records weights only from the scale, and only from a stable reading (`ST`).

### `settings`
`key TEXT PK, value TEXT`: serial port, scale protocol, printer, company details for the ticket header, form file, next ticket number, and so on. TS.ini values are imported here as well.

---

## 3. Migration (DATA.LIS → SQLite)

Runs **once, on the Windows PC** (because of the Jet driver), and can be repeated: it always rebuilds the SQLite file from scratch.

1. Trim the spaces from every text value; convert weights to integers; combine date + time into one value (`1899-12-30` → empty).
2. **Merge duplicates.** Deleted rows are kept only if a weighing refers to them; then they are marked `active = 0`.
   | | Old rows | After merge (actual) | Merge key |
   |---|---:|---:|---|
   | Registration plates | 133 | **88** | `plate_key` (letters and digits only, O→0) |
   | Drivers | 92 | **54** | family + given name, ignoring case and accents |
   | Customers | 27 | **24** ("UNKNOWN" dropped) | name, ignoring case and accents |
   | Materials | 23 | 23 | name + note |
   - For a merged plate, the displayed spelling is the one used in the most weighings, and `last_tare` is the tare from its latest weighing.
   - Plates that differ by a real character are **not** merged automatically (e.g. `NLB831/YJA694` is probably a typo of `NLB381/YJA694`). The program will need a "merge" function for these.
3. `W_RECEIPTS` → `weighings` (`status = DONE`):
   - `OPER_TYPE` 2 → `IN`, 1 → `OUT`
   - `BRUTTO_W`/`CAR_W`/`ART_W` → `gross`/`tare`/`net`
   - `W_*_IN` → `first_at`, `W_*_OUT` → `second_at`; if `first_at` is empty → `stored_tare = 1`
   - `TicketNumber` → `ticket_no`; the next ticket number = largest + 1
   - The 4 identical (double-saved) weighings are kept, because they have different ticket numbers.
4. `TM_CARS` → `weighings` with `status = OPEN`, in case there are trucks in the yard at migration time.
5. Not migrated: `REGIONS`, `Users`, `OPERATORS`, `TICKETS`, `T_NUMBERS`, `CALC_ART_W`, `ART_H`, `ART_REGION`, `ART_HUMI`.
6. A check runs after the import: number of weighings, and gross/tare/net totals per year and direction, compared between old and new.

**Running it** (Windows):
```
dotnet build tools/TruckScale.Import
tools\TruckScale.Import\bin\Debug\net10.0-windows\TruckScale.Import.exe "<path>\DATA.LIS" truckscale.db --ini TS.ini [--overwrite]
```
The importer works on a temporary copy of the source file, so it never modifies DATA.LIS.

First run (2026-11-04, production database): 413 weighings, of which 78 used a stored tare. **All yearly totals match** (2022–2023, IN/OUT, gross/tare/net). Next ticket number: 417.

---

## 4. Decisions
- `OPER_TYPE`: 2 = Beszállítás, 1 = Kiszállítás (you said "mostly Beszállítás", and the seasons confirm it).
- The `ORG_TYPE = 2` organisation is your own company: it becomes the ticket header details (`settings`), not a customer.
- Test data (`Tesztcég`, `Tesztanyag`, `ABC123`) **is migrated**.
- `REGIONS` is not needed.
- Stornó is needed on open weighings.
