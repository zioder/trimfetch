# TrimFetch Windows installer

## End users

Download from [GitHub Releases](https://github.com/zioder/trimfetch/releases):

- **`TrimFetchSetup-<version>-x64.exe`** — most PCs
- **`TrimFetchSetup-<version>-arm64.exe`** — Windows on ARM

GitHub also provides **Source code (zip)** and **Source code (tar.gz)** on each release page.

## Build locally

```powershell
# x64
dotnet publish src/TrimFetch/TrimFetch.csproj `
  -c Release -p:Platform=x64 -p:WindowsPackageType=None -r win-x64 --self-contained true

& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer/TrimFetch.iss `
  "/DPublishDir=src/TrimFetch/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/publish" `
  /DMyAppVersion=1.0.0 /DTargetArch=x64 /DOutputDir=artifacts
```

Install [Inno Setup 6](https://jrsoftware.org/isinfo.php) first. Repeat with `ARM64`, `win-arm64`, and `/DTargetArch=arm64` for the ARM installer.
