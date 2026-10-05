# Codex Limit Monitor

A free, lightweight Windows tray utility that shows the Codex usage limits available to your signed-in local Codex installation.

## What it does

- Shows the remaining weekly limit and the 5-hour limit when Codex provides one.
- Refreshes every minute and marks data as stale after three minutes without a successful update.
- Shows one notification per 10%, 5%, and 2% threshold for each limit window.
- Offers a diagnostics view and optional start with Windows.

## Requirements

- Windows 10 or 11, 64-bit.
- The ChatGPT/Codex desktop app installed and signed in for the same Windows user.
- No OpenAI API key or Git installation is required. The release ZIP includes the .NET runtime.

## Installation

1. Extract the release ZIP to a permanent folder.
2. Run `CodexLimitMonitor.exe`.
3. Find the icon in the Windows notification area, possibly behind the `^` arrow.

The app has no main window and does not add itself to startup during installation.

## Start with Windows

Right-click the tray icon and select **Start with Windows**. This creates an entry under the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key. Clear the checkbox to remove that entry. Keep the EXE in its permanent folder while startup is enabled.

## Usage

Click the tray icon or choose **Open** to see the available limits, reset times, and last update. Use **Refresh** for an immediate read. Open **Diagnostics** to inspect the connection state and copy safe diagnostics. If Codex does not provide a 5-hour window, the app shows `5-hour limit: —`.

## Privacy

Codex Limit Monitor uses the local Codex app-server method `account/rateLimits/read`. It does not ask for, copy, or store account credentials, cookies, tokens, or API keys. It has no telemetry or analytics. It stores only threshold notification history in the current user's local app-data folder so alerts do not repeat after restart. The app does not need Git for limit reads.

## Troubleshooting

- **Codex is not available:** Install and sign in to the ChatGPT/Codex desktop app, then choose **Refresh**.
- **Unable to read usage data:** Check **Diagnostics**. The app retries automatically and marks old values as stale.
- **A `git.exe` error appears:** The limit reader itself does not require Git. The app launches Codex app-server with a minimal `PATH` that excludes Git. If an installed Codex version still starts Git internally by an absolute path, update Codex and share the safe diagnostics with the maintainer.

Codex Limit Monitor depends on a local Codex interface. A future Codex update may require an app update.

## Disclaimer

Unofficial community tool. Not affiliated with or endorsed by OpenAI. Created by MW Interactive.

