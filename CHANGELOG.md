# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-10-16

Initial public release.

### Added

- **GUI** (`HikWarranty.exe`): batch serial-number input with auto de-duplication and upper-casing, live progress, colour-coded result table (in warranty / expired / not found), detail window with the full raw API response, one-click product image, CSV and clipboard export.
- **CLI** (`hikq.exe`): table / JSON / CSV output, configurable concurrency, exit codes for scripting.
- Concurrent batch scheduler (`SemaphoreSlim` + tasks) with cancellation support and input-order results.
- Performance work: TLS warm-up on startup, per-host connection limit raised from the .NET default of 2 to 64, default concurrency 8 — 30 serials complete in ~5 s (vs ~23 s serial).
- Robust response handling: explicit UTF-8 byte decoding (the API sends no charset), tolerant JSON field mapping, automatic retry on timeout / 429 / 5xx.
- Settings persistence (`%LOCALAPPDATA%\HikWarranty\ui.ini`).
- Precise DPI scaling for 100 %–200 % displays.
- Zero-dependency build script using the C# compiler bundled with the .NET Framework.