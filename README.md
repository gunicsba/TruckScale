# Truck Scale

Cross-platform truck scale (hídmérleg) program – a remake of the old *TS.exe* (LIS120).
C# / .NET 10, [Avalonia](https://avaloniaui.net/) UI, SQLite database.

## Status
- [x] Migration from the old `DATA.LIS` (Access) database – `tools/TruckScale.Import`
- [x] Main window: live weight, trucks in the yard, today's weighings
- [x] Beszállítás / Kiszállítás: arrival screen pre-filled from the last weighing, 1st and 2nd Mérés, "Használ" (stored tare, one weighing), Stornó
- [x] Scale: serial indicator (`ST,GL,   12340kg` lines) and a built-in simulator
- [ ] Ticket printing from the `.form` layout
- [ ] Napló (history / reports), Adatállomány (lists, merging duplicates), Beállítás (settings)

## Running
```
dotnet run --project src/TruckScale.App -- [--db path\to\truckscale.db] [--sim]
```
- Default database: `%APPDATA%\TruckScale\truckscale.db` (`~/.config/TruckScale/` on Linux); created empty if missing.
- `--sim` uses the simulator instead of the serial port. The simulator is also used automatically when the configured port does not exist.

## Migrating the old data (Windows only)
```
dotnet build tools/TruckScale.Import
tools\TruckScale.Import\bin\Debug\net10.0-windows\TruckScale.Import.exe "<path>\DATA.LIS" truckscale.db --ini TS.ini
```
See [docs/DATABASE.md](docs/DATABASE.md) for the old and new database structure.

## Tests
```
dotnet test
```
