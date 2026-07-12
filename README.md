# Going Cooperative

Experimental cooperative multiplayer for **Going Medieval**.

Going Cooperative uses a host-authoritative model: the host runs the game simulation, clients send player intent, and the host replicates authoritative state back to connected clients.

> [!WARNING]
> Going Cooperative is pre-release prototype software. Gameplay coverage is incomplete, configuration and protocol details may change, and desynchronization or save problems are possible. Back up every save before testing.

## Before you begin

Every computer needs:

- The same version of Going Medieval for Windows x64.
- [BepInEx 5.4.23.5 for Windows x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).
- The same Going Cooperative release.
- A compatible copy of the same starting save.

The host must accept inbound UDP traffic on port `47692`. Internet play normally requires a VPN such as Tailscale or Radmin VPN, or correctly configured UDP port forwarding.

## Player installation

### 1. Find the Going Medieval directory

In Steam, right-click **Going Medieval**, select **Manage → Browse local files**, and open the directory containing:

```text
Going Medieval.exe
Going Medieval_Data\
UnityPlayer.dll
```

### 2. Install BepInEx

Download `BepInEx_win_x64_5.4.23.5.zip` from the [official BepInEx 5.4.23.5 release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).

Do not download the x86 build, BepInEx 6, a Unix build, or GitHub's source-code archives.

Extract the **contents** of the BepInEx archive directly into the Going Medieval directory. The result should look like:

```text
Going Medieval\
├── BepInEx\
├── doorstop_config.ini
├── winhttp.dll
├── Going Medieval.exe
└── Going Medieval_Data\
```

Launch Going Medieval, wait for the main menu, and exit. Confirm that BepInEx created:

```text
BepInEx\plugins\
BepInEx\config\
BepInEx\LogOutput.log
```

If those items were not created, fix the BepInEx installation before continuing.

### 3. Install Going Cooperative

Open the [Going Cooperative Releases](../../releases) page and download `Going-Cooperative-vX.Y.Z.zip` under **Assets**.

> [!IMPORTANT]
> Do not download GitHub's automatically generated **Source code (zip)** or **Source code (tar.gz)** files unless you intend to compile the mod yourself.

Extract the release archive's **contents** directly into the Going Medieval directory. Allow its `BepInEx` folder to merge with the existing one.

The installed release should initially look like:

```text
Going Medieval\
├── BepInEx\
│   └── plugins\
│       └── GoingCooperative\
│           └── GoingCooperative.dll
├── GoingCooperative\
│   ├── replication-host.example.cfg
│   └── replication-client.example.cfg
├── doorstop_config.ini
├── winhttp.dll
└── Going Medieval.exe
```

Do not leave the files inside an extra wrapper directory such as:

```text
Going Medieval\Going-Cooperative-v0.1.0\BepInEx\...
```

### 4. Global settings, not session setup

`Going Medieval\GoingCooperative\replication.cfg` stores global replication and
diagnostic settings. In the menu-driven workflow it should not contain `mode`,
`host`, or `port`: role, remote address, and session port are selected in the UI
for each session.

The shipped host/client examples remain available for legacy config-driven
testing and rollback. Setting `multiplayerMenu=false` restores that old behavior.

The required runtime files are the plugin DLL under
`BepInEx\plugins\GoingCooperative` and the global settings file under
`GoingCooperative`.

### 5. Configure the network

On the host computer:

1. Permit Going Medieval through Windows Firewall.
2. Permit inbound UDP traffic on port `47692`.
3. Enter the host computer's reachable LAN/VPN address in the Join screen.
4. Use the same port in the Host and Join screens.

For play outside the same local network, a private VPN is usually simpler than router port forwarding.

### 6. Start a session

The replacement multiplayer menu is enabled by default. Select the remapped
**Multiplayer** main-menu button or press `F8`:

1. On the host, open **Host Game**, confirm the UDP port, and select **Start Hosting**.
2. On the client, open **Join Game**, enter the host's reachable LAN/VPN address,
   and select **Connect**.
3. Wait for **Connected** and **Compatible peer verified** on both computers.
4. Load compatible copies of the same settlement/save on both computers.

The test menu changes Host/Client role and restarts the replication transport
without creating or editing a session configuration. Session values are kept in
memory for the current process.

Set the following value to disable the replacement menu and restore the original
config-driven runtime and client resync overlay:

```ini
multiplayerMenu=false
```

Save transfer, LAN discovery, and automatic save loading are not implemented in
this test version. The Status page labels resync as a control-path test rather
than implying that save transfer is complete.

### Multiplayer UI test functions

With `multiplayerMenu=true`, use the remapped **Multiplayer** main-menu button.

- **Test Save Transfer** on the Host page runs a safe visual test through
  manifest creation, chunk transfer, SHA-256 verification, quarantine staging,
  and completion. It uses a synthetic 32 MiB payload model and never reads,
  writes, replaces, or loads a user save.
- **Test Resync Flow** on the Status page presents the equivalent checkpoint
  transfer and reload-required sequence without invoking the game save loader.
- **Request Resync** is available to a running client. It sends the real existing
  resync-control request to the host and displays the network response; the host
  currently reports that save export/load integration is blocked.

After starting a Host or Client runtime and entering gameplay, a compact
multiplayer HUD appears in the upper-right with connection state, role, current
test phase, an **Open** action, and a client **Resync** action. The HUD is hidden
on the main menu. Press `F8` at any time as a fallback way to open the full panel.

## Verify the installation

After launching the game, inspect:

```text
BepInEx\LogOutput.log
BepInEx\GoingCooperative\plugin.log
```

A successful startup should include messages similar to:

```text
Going Cooperative replication plugin loaded version=0.1.0
Going Cooperative replication runtime started mode=host
```

or:

```text
Going Cooperative replication runtime started mode=client
```

If `BepInEx\GoingCooperative\plugin.log` does not appear, verify these exact paths:

```text
BepInEx\plugins\GoingCooperative\GoingCooperative.dll
GoingCooperative\replication.cfg
```

## Troubleshooting

### BepInEx does not initialize

- Confirm `winhttp.dll` and `doorstop_config.ini` are beside `Going Medieval.exe`.
- Confirm you installed the Windows x64 BepInEx 5.4.23.5 archive.
- Launch the game once without Going Cooperative and check for `BepInEx\LogOutput.log`.

### The plugin does not load

- Confirm the DLL is exactly `BepInEx\plugins\GoingCooperative\GoingCooperative.dll`.
- Search `BepInEx\LogOutput.log` for `Going Cooperative`, `error`, or `exception`.
- Confirm all participants use the same Going Cooperative and Going Medieval versions.

### The client cannot reach the host

- Replace `HOST_IP_ADDRESS` with the actual host LAN or VPN address.
- Confirm the host is running before the client connects.
- Confirm both configurations use UDP port `47692`.
- Check Windows Firewall and any VPN or router rules.
- Do not use `127.0.0.1` unless host and client are running on the same computer.

## Building from source

Ordinary players should use the ZIP attached to a GitHub Release. The steps below are only for developers building their own copy.

### Prerequisites

- Windows PowerShell 5.1 or newer.
- A .NET SDK containing the Roslyn C# compiler.
- A local Going Medieval installation.
- BepInEx 5.4.23.5 installed in that game directory, or a separate compatible BepInEx core directory.

Open PowerShell in the repository directory—the directory containing `README.md`, `scripts`, and `src`.

### Compile only

Close Going Medieval, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 `
  -Configuration Release `
  -GameRoot "C:\Path\To\Going Medieval"
```

The compiled plugin is written to:

```text
artifacts\bin\Release\GoingCooperative.dll
```

### Create a GitHub release archive

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Package-Release.ps1 `
  -Version 0.1.0 `
  -GameRoot "C:\Path\To\Going Medieval"
```

Replace the example game path with the directory containing `Going Medieval.exe`.

The packaging script will compile the plugin, stage the player-facing directory structure, create the ZIP, and calculate its SHA-256 checksum:

```text
artifacts\Going-Cooperative-v0.1.0.zip
artifacts\Going-Cooperative-v0.1.0.zip.sha256
```

If BepInEx is stored outside the game directory, pass its `core` directory explicitly:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Package-Release.ps1 `
  -Version 0.1.0 `
  -GameRoot "C:\Path\To\Going Medieval" `
  -BepInExRoot "C:\Path\To\BepInEx\core"
```

Upload `Going-Cooperative-v0.1.0.zip` as the player download under GitHub Release **Assets**. The `.sha256` file may be uploaded beside it for integrity verification.

## Repository layout

```text
config\
├── replication-host.example.cfg
└── replication-client.example.cfg
scripts\
├── Build.ps1
└── Package-Release.ps1
src\
├── GoingCooperative.Core\
└── GoingCooperative.Plugin.BepInEx\
```

- `GoingCooperative.Core` contains transport contracts, codecs, replication messages, save identity, and shared runtime primitives compiled into the plugin.
- `GoingCooperative.Plugin.BepInEx` contains the BepInEx entry point and host-authoritative replication runtime.
- Going Medieval, Unity, and BepInEx binaries are intentionally excluded from the repository and release archive.
- Local logs, saves, captures, active configurations, build output, and temporary files should not be committed.

## License

Going Cooperative is available under the [MIT License](LICENSE). Third-party projects and trademarks remain subject to their own terms; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
