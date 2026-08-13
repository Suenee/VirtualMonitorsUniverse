# Virtual Monitors Universe (VMU)

Virtual Monitors Universe is an open-source virtual display manager for Windows 10+.

The goal is to create one or more virtual monitors that Windows treats like ordinary physical displays: they can be used as part of an extended desktop, configured with standard display parameters, and accessed through a low-latency terminal in a web browser.

VMU is designed as a client-server system so virtual displays can be viewed and controlled locally or from another device on the local network. The project intentionally does **not** aim to develop a new virtual display driver from scratch. It will build on a proven existing Windows virtual display driver only after a dedicated technical evaluation confirms that the selected driver meets the project's requirements without unresolved blockers.

## Core principles

- One or more virtual monitors, limited only by Windows, the selected display driver, and available system resources.
- Windows must recognize each virtual monitor as a real display that can participate in the extended desktop.
- Each VMU monitor has a persistent internal UUID independent of its user-visible name or Windows `DISPLAYx` identifier.
- VMU stores monitor definitions persistently and restores their intended state after restart.
- Display capture and streaming are demand-driven: **no viewer = no VMU capture, encoding or network stream**.
- When a viewer is connected, image transfer must use low-latency compression comparable in principle to modern remote-desktop solutions, with GPU acceleration where available.
- Multiple viewers of the same monitor should share capture/encoding work where technically practical instead of duplicating it unnecessarily.
- Local and remote access should use the same core viewer architecture wherever possible.
- Local access does not need a separate clipboard transport because it shares the same Windows clipboard.
- Remote access may optionally transfer keyboard input and clipboard data.
- Internet-facing remote desktop is not an initial goal. The first supported remote scenario is a trusted local network.

## Architecture

VMU consists of three cooperating components.

### VMU Server

The Windows backend and main application runtime. It is responsible for:

- managing virtual monitor definitions and lifecycle,
- communicating with the selected third-party virtual display driver,
- monitor configuration and persistence,
- capture and streaming,
- remote input handling,
- authentication and network access,
- logging and diagnostics,
- exposing the API used by both VMU Client and VMU Module.

The desktop application runs primarily as a tray application with only essential controls available directly from the tray menu.

Typical tray functions:

- VMU Server start / stop,
- service status,
- quick access to individual monitor terminals,
- open Web UI,
- settings,
- log viewer,
- restart / exit.

Stopping VMU Server should power off the virtual monitors in the same conceptual way as switching off physical displays, while keeping their persistent VMU definitions.

### VMU Client

A responsive client-side web application served by VMU Server.

It provides:

- complete VMU configuration,
- monitor overview,
- monitor-specific settings,
- system status and diagnostics,
- log viewer,
- browser terminals for individual virtual monitors.

A terminal shows the live content of a selected virtual monitor and can provide mouse, keyboard and clipboard interaction according to the selected access mode.

The initial remote-access modes are:

- **Disabled** — remote access is not allowed.
- **Presentation** — view only.
- **Collaboration** — view plus mouse, keyboard and linked clipboard.

The Web UI should be responsive enough to be useful from desktop PCs, notebooks, tablets and phones. Smaller devices are primarily intended for monitoring and quick control rather than full desktop work.

### VMU Module

A Bitfocus Companion module used to control VMU from a control surface.

The module should call the same public VMU API operations as VMU Client rather than implementing a separate control path.

The communication protocol should follow the deterministic design principles already used in the VoicePrompter ecosystem: explicit message types, unique IDs, well-defined methods/events, strict parameter schemas, responses/errors and rejection of unknown parameters.

Expected Companion variables include:

- number of configured monitors,
- monitor names,
- monitor power states,
- active resolution,
- refresh rate,
- orientation,
- remote-access state,
- relevant health/status information.

Expected actions include:

- power monitor on/off,
- set resolution,
- set refresh rate,
- set orientation,
- open monitor terminal,
- rename monitor,
- change remote-access mode,
- later manage allow/deny access rules.

## Monitor model

VMU separates a persistent **monitor definition** from the transient Windows display instance.

A monitor definition contains, at minimum:

- immutable internal UUID,
- user-visible name,
- resolution,
- refresh rate,
- orientation,
- intended power state,
- remote-access mode,
- security configuration.

The Windows identifier such as `\\.\DISPLAY3` is considered runtime information only and must not be used as the permanent identity of a VMU monitor.

## Networking and protocols

The implementation may use several protocols, each for the job it is best suited for. The final technology selection is subject to the technical research and proof-of-concept phases.

The intended separation is:

- HTTP/REST or equivalent request/response API for configuration and state,
- WebSocket or equivalent realtime channel for events and control,
- WebRTC or another suitable low-latency media transport for compressed display streaming.

Video must not be transported as an inefficient sequence of full screenshots in the production design.

## Security

Initial development scope:

- localhost access can be trusted automatically,
- LAN access requires authentication,
- API key and/or password protection,
- no public Internet exposure by default.

Interactive connection approval and allow/deny lists are planned for the beta stage after the core monitor and streaming architecture is stable.

## Development gates

The project deliberately uses technical gates before committing to the full product.

### Gate 1 — Virtual display driver

Before the main application is developed, available third-party Windows virtual display drivers must be researched in depth.

The selected solution must be verified for, among other things:

- Windows 10 and Windows 11 compatibility,
- proper driver signing and normal installation without test mode,
- licensing suitable for VMU and open-source distribution,
- multiple virtual displays,
- dynamic creation/removal or equivalent controllable lifecycle,
- supported resolutions and refresh rates,
- orientation handling,
- interaction with normal Windows extended desktop,
- stable behavior across restart and sleep/wake,
- compatibility with common NVIDIA, AMD and Intel configurations,
- practical capture of the resulting virtual display,
- usable API, CLI, IPC or another reliable control path,
- active maintenance and known limitations.

The result of Gate 1 must be an explicit **GO / NO-GO** decision. VMU will not begin full development on top of a driver that still has fundamental unresolved questions.

### Gate 2 — Capture and browser terminal

A small proof of concept must verify:

1. a virtual monitor is active,
2. Windows can place real applications on it,
3. the monitor can be captured reliably,
4. the image can be compressed efficiently, preferably using GPU hardware acceleration where available,
5. the stream can be displayed in a browser with acceptably low latency,
6. browser mouse interaction can be mapped correctly back to the virtual display.

Only after Gate 2 passes should the production terminal and streaming architecture be expanded.

# Roadmap

## ALPHA — Driver proof of concept

A minimal command-line tool proving that the selected third-party virtual display driver is genuinely usable for VMU.

Initial commands:

```text
vmu --on
vmu --off
vmu --status
vmu --list
```

The first test monitor:

- name: `Virtual Monitor`,
- resolution: 1920 × 1080,
- aspect ratio: 16:9,
- available as a normal Windows display,
- usable as an extended desktop.

OBS Studio is used as an independent validation tool to verify that the virtual monitor can be captured like an ordinary display.

ALPHA must also include a safe cleanup/recovery mechanism. Cleanup must remove only components and state known to belong to VMU/the selected virtual display driver. Broad or speculative registry deletion is not acceptable.

Acceptance test:

1. enable the virtual display,
2. verify it appears in Windows display settings,
3. extend the Windows desktop to it,
4. verify OBS can capture it,
5. restart Windows and verify the system remains consistent,
6. disable/remove the virtual display,
7. run cleanup if required,
8. verify the machine returns to a clean baseline state.

A successful ALPHA closes Gate 1.

## 0.1 DEVEL — VMU Server and monitor management

- Windows tray application,
- persistent configuration,
- monitor definitions with immutable UUIDs,
- multiple virtual monitors,
- power on/off lifecycle,
- resolution selection,
- refresh-rate selection,
- horizontal/vertical orientation,
- monitor rename,
- basic Web UI,
- monitor overview,
- server status,
- logs and diagnostics,
- configurable bind address (`localhost`, all interfaces, or selected interface),
- configurable service ports,
- optional start with Windows.

## 0.2 DEVEL — Terminal and streaming

- capture a selected virtual monitor,
- compressed low-latency stream,
- GPU acceleration where available,
- browser terminal,
- scaling / fit-to-window,
- mouse control,
- correct coordinate mapping including DPI/scaling considerations,
- demand-driven streaming: no viewer means no VMU capture/encode/network stream,
- shared capture/encoding pipeline for multiple viewers where practical.

Successful completion closes Gate 2.

## 0.3 DEVEL — LAN remote access

- remote browser client,
- keyboard transfer,
- clipboard transfer,
- authentication,
- Presentation mode,
- Collaboration mode,
- Disabled mode,
- reconnect/recovery basics.

## 0.4 DEVEL — Bitfocus Companion integration

- stable VMU control API,
- realtime status/event channel,
- Companion module,
- variables,
- actions,
- presets where useful,
- deterministic protocol based on the proven VoicePrompter design principles.

## BETA — Hardening and operational reliability

- interactive remote connection approval,
- allow/deny lists,
- stronger authentication and security review,
- sleep/wake recovery,
- Windows display topology changes,
- DPI/scaling edge cases,
- multi-GPU testing,
- NVIDIA / AMD / Intel testing,
- encoder fallback paths,
- performance and resource optimization,
- robust reconnect behavior,
- installer,
- updater/upgrade mechanism,
- production-quality logging and diagnostics,
- broader Windows 10/11 compatibility testing.

## Out of scope for the initial project

- developing a new Windows virtual display driver from scratch,
- public Internet-facing remote desktop service,
- replacing Windows Remote Desktop, TeamViewer, Chrome Remote Desktop or similar general-purpose products,
- advanced enterprise identity/authentication infrastructure.

## Project status

The project is currently in the **research / pre-ALPHA** stage. The next step is Gate 1: a deep evaluation of available Windows virtual display driver solutions and an explicit GO/NO-GO recommendation before implementation begins.
