# Download Faster

A Windows download manager that pulls one file over **Wi-Fi and Ethernet at the same time**, so their speeds add up. Every adapter is adjustable while the download runs: turn it on or off, choose how many connections it opens, cap its speed, or give it a fixed share of the file.

![Download Faster downloading over Ethernet and Wi-Fi at once](docs/screenshot.png)

<sub>Screenshot from a test run with simulated adapters on a local server, which is why the addresses are 127.0.0.x.</sub>

## Will it actually be faster?

The speeds add up when **the two adapters reach the internet through different lines**, for example:

- home broadband on Ethernet + a phone hotspot or a second network on Wi-Fi
- office network on Ethernet + guest Wi-Fi on a separate connection
- an internet plan faster than either link alone (e.g. 2 Gbps fibre with a 1 Gbps Ethernet port, and Wi-Fi 6 to the same router)

If Wi-Fi and Ethernet both go to the same router on the same internet line, that line is the limit and combining them gains little. Click **Test speed** to find out: it measures each adapter alone, then all together, and tells you whether you gain anything.

Even with one adapter, the app can help: it downloads with several connections in parallel, which beats servers that slow down each single connection.

## Download

1. Open the [Actions tab](https://github.com/kseoluberkay/Download-Faster/actions/workflows/build.yml), pick the latest green run and download the **DownloadFaster-windows** artifact. (When a release is tagged, the files are also on the [Releases page](https://github.com/kseoluberkay/Download-Faster/releases).)
2. Unzip it and run `DownloadFaster-x64.exe` (most PCs) or `DownloadFaster-arm64.exe` (Snapdragon / Windows on ARM laptops).

It is a single portable `.exe`: no installer, no .NET install needed, no admin rights. Because the file is not code-signed, Windows SmartScreen may warn you the first time; click **More info > Run anyway**.

Requires Windows 10 or 11.

## How to use

1. Connect both Wi-Fi and Ethernet.
2. Paste a download link (the app offers a copied link automatically) and press **Enter** or **Download**.
3. Watch the live speed per adapter in the **Now** column and in the status bar.

Pause, resume, cancel, open the file or show it in its folder from the buttons or the right-click menu. Closing the app pauses running downloads; they come back as paused next time and continue where they stopped.

## Settings

Everything below applies instantly, also to downloads that are already running, and is remembered between runs.

| Setting | What it does |
| --- | --- |
| **Use** | Include or exclude an adapter. Unticking one mid-download hands its work to the others. |
| **Connections** | Parallel connections per adapter (1-32). 4-8 is usually plenty; more can help with far-away or throttled servers. |
| **Speed limit** | Caps an adapter, e.g. to spare a metered hotspot. Pick a value or type any number of MB/s (1 MB/s = 8 Mbps). |
| **Balancing: Automatic** (default) | Every adapter grabs the next piece as soon as it is free, so faster adapters do more and the download finishes as early as possible. |
| **Balancing: Fixed split** | Each adapter downloads the share you set with its **Share** slider, e.g. 80% Ethernet / 20% Wi-Fi. |
| **Chunk size** | Size of the pieces the file is cut into (1-32 MB). Smaller balances better between adapters, bigger means fewer requests. |
| **Test speed** | Measures each ticked adapter alone, then together. |
| **Refresh adapters** | Re-scan after plugging in a cable or joining a network. |

## How it works

- **Adapter binding.** Each connection's socket is bound to one adapter's IP address before it connects. Windows sends traffic out of the adapter that owns the source address, so connections bound to the Wi-Fi address use Wi-Fi and the others use Ethernet.
- **Split download.** The app asks the server for the file size, cuts the file into chunks and requests them with HTTP range requests. Connections on every adapter take chunks from one shared queue and write them straight to the right place in the file.
- **No slow finish.** When the queue is empty, an idle connection splits the biggest unfinished chunk with the connection working on it, in proportion to their speeds. If the other connection is much slower, the fast one takes over the rest completely, so a slow adapter never holds up the last few percent.
- **Resilience.** Dropped connections are retried, an adapter that keeps failing (e.g. Wi-Fi with no internet) is backed off while the others carry on, and a `.dfpart.json` file next to the partial `.dfpart` file records what is still missing, so a download can resume after a pause, crash or reboot.
- **Fallback.** If a server does not support range requests, the download still works over one connection.

## Limitations

- Works with direct HTTP/HTTPS file links. It does not speed up browsers, Steam, streaming or other apps, and it cannot use links that need you to be logged in.
- Downloads go straight out of the adapters, not through a system proxy or a full-tunnel VPN.
- Windows may decide on its own to disconnect Wi-Fi while Ethernet is plugged in. If that happens, see below.

## Troubleshooting

**Wi-Fi disconnects when Ethernet is plugged in.** Windows can be set to "minimize simultaneous connections". To allow both, run this in an admin Command Prompt and restart:

```
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\WcmSvc\GroupPolicy" /v fMinimizeConnections /t REG_DWORD /d 0 /f
```

(Same setting in Group Policy: Computer Configuration > Administrative Templates > Network > Windows Connection Manager > *Minimize the number of simultaneous connections to the Internet or a Windows Domain* > Enabled, option 0.) Delete the value again to undo it.

**An adapter shows "Problem, retrying".** Hover over it to see the error. Usually that network has no internet access (captive portal, hotspot out of data). Untick it, or fix the connection and it rejoins on its own.

**An adapter is missing.** Only connected adapters with an internet gateway are listed. Connect it, then click **Refresh adapters**. VPN and virtual adapters are listed but unticked by default.

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet test                                                    # engine tests
dotnet run --project src/DownloadFaster.App                    # run the app (Windows)
dotnet publish src/DownloadFaster.App -c Release -r win-x64    # single DownloadFaster.exe
```

The app only runs on Windows, but everything builds and the engine tests run on Linux and macOS too. The tests use a local server and bind to `127.0.0.1` and `127.0.0.2` to stand in for two adapters.

| Folder | Contents |
| --- | --- |
| `src/DownloadFaster.Core` | Download engine: adapter discovery, bound connections, chunk scheduler, speed limits, resume. No UI code. |
| `src/DownloadFaster.App` | Windows Forms app. |
| `tests/DownloadFaster.Core.Tests` | End-to-end engine tests against a local range-capable server. |
