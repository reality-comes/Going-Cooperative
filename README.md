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
- Access to the same network or VPN as the host. The host transfers the selected
  starting save to the client through the Multiplayer workflow.

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

### 4. Install the shared runtime configuration

Keep `Going Medieval\GoingCooperative\replication.cfg` on both computers. It
stores the shared replication behavior and performance settings used by the
current build. Both players can use the same baseline file.

For the menu-driven workflow, copy `replication-host.example.cfg` to
`replication.cfg` on both computers. Despite the example filename, its role and
endpoint values are only startup/fallback values once the Multiplayer menu is
used; the menu changes them for the selected Host or Join session. This avoids
requiring a client IP address in advance.

The Multiplayer menu overrides the session-specific values in memory, including
host/client role, remote address, and port. Players do not need to edit or swap
configuration files before starting a session.

The client example remains available for advanced config-driven testing and
rollback. Setting `multiplayerMenu=false` restores that legacy workflow, where
the separate host/client values matter again.

The required runtime files are the plugin DLL under
`BepInEx\plugins\GoingCooperative` and the global settings file under
`GoingCooperative`.

### 5. Configure the network

On the host computer:

1. Permit Going Medieval through Windows Firewall.
2. Permit inbound UDP traffic on port `47692`.
3. Give the client the host computer's reachable LAN/VPN address.
4. Use the same port in the Host and Join screens.

For play outside the same local network, a private VPN is usually simpler than router port forwarding.

### 6. Start and join a session

Use the remapped **Multiplayer** button on the main menu. The intended sequence
is important: connect and transfer the save before either player presses Play.

#### Host

1. Select **Multiplayer**.
2. Choose **Select Load** and select the settlement save to host.
3. Select **Host** and confirm the UDP port.
4. Wait for the client to connect.
5. Wait for the selected save to finish transferring and for both sides to show
   **Connected**.
6. Select **Play** after the client is ready.
7. The normal Going Medieval loading screen opens and loads the hosted save.

#### Client

1. Select **Multiplayer**, then **Join**.
2. Enter the host's reachable LAN/VPN IP address and the same UDP port.
3. Select **Connect**.
4. Wait while the client receives and verifies the host's save.
5. Wait for **Connected**, then select **Play**.
6. The normal Going Medieval loading screen opens and loads the transferred
   save.

The menu assigns the role, endpoint, and port for the current session without
rewriting `replication.cfg`. Do not manually load a different save on the client
after connecting.

Set the following value to disable the replacement menu and restore the original
config-driven runtime and client resync overlay:

```ini
multiplayerMenu=false
```

### In-game controls and Full Session Resync

During multiplayer, a compact transparent HUD appears at the top center of the
game. It shows the connection state and whether the local player is the host or
client. The client also has a **FULL RESYNC** button.

Use Full Resync when the client is visibly desynchronized, missing important
world state, or otherwise no longer matches the host:

1. The client selects **FULL RESYNC**.
2. Both games enter the dedicated resync overlay while gameplay replication is
   paused.
3. The host creates a fresh authoritative checkpoint.
4. The checkpoint is transferred to and verified by the client.
5. Both sides pass through the mod's blank pseudo-home transition, then reload
   the checkpoint through the normal game loading flow.
6. Replication resumes after the synchronized reload completes.

Do not close either game or manually load another save while resync is in
progress. If resync fails, retain the logs from both computers before retrying.
Press `F8` if the Multiplayer panel needs to be reopened as a fallback.

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

- Enter the host's actual LAN or VPN address in **Multiplayer → Join**.
- Confirm the host is running before the client connects.
- Confirm the Host and Join screens use the same UDP port, normally `47692`.
- Check Windows Firewall and any VPN or router rules.
- Do not use `127.0.0.1` unless host and client are running on the same computer.

### The client cannot load the transferred save

- Confirm both computers use the same Going Medieval version and the same Going
  Cooperative build.
- Let the transfer reach **Connected** before selecting **Play**.
- Do not manually start or load a different settlement on the client.
- Check both logs for `save`, `transfer`, `verify`, `hash`, or `load` errors.

### Full Session Resync does not finish

- Leave both games open on the resync overlay; checkpoint creation and loading
  can take time for large settlements.
- Confirm the host still has access to its save directory.
- Check both logs for `resync`, `checkpoint`, `pseudo-Home`, `transfer`, or
  `load` errors.
- Restart the session from the Multiplayer menu if the resync explicitly reports
  failure rather than continuing indefinitely.

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
