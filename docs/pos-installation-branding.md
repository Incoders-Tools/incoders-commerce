# POS installation branding

`Commerce.Pos.Windows.exe` is the stable executable and process identity for every installation.

The visible application name defaults to `Vaca Verde`. An installation may set a display name in `%LOCALAPPDATA%\Incoders\Commerce\branding.json`:

```json
{ "Commerce": { "ApplicationName": "Store display name" } }
```

`Commerce__ApplicationName` overrides that file. Values are trimmed and must be non-blank, free of Unicode control characters, and no more than 80 characters; an invalid selected value safely falls back to `Vaca Verde`.

`branding.json` is deliberately separate from security-sensitive `installation.json` and stays in the data directory so normal upgrades preserve it. A future installer may accept `COMMERCE_APPLICATION_NAME`, validate it with this contract, write `branding.json`, and use the resulting display name for shortcuts. This repository does not create installer or shortcut assets.
