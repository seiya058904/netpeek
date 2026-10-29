# NetPeek

<!-- impeccable:product-schema 1 -->

## Platform
Windows 10/11 native desktop, Simplified Chinese by default.

## Stack
Delegated by the user's supplied brief: C# / WPF on .NET Framework 4.8 with Windows IP Helper APIs. Build using the Windows framework compiler; no package manager, browser runtime, driver, local web server or default elevation. The brief explicitly delegates visual details and the technical tradeoff after upstream inspection.

## Users and purpose
People who want to open a lightweight desktop utility and immediately understand which applications have network endpoints, and where their current TCP connections lead. Exact IP/port and process identity take precedence over speculative insights.

## Capabilities and constraints
Application list with real executable icons; expandable process and endpoint detail; search, simple filters, address copying, pause display refresh, retry, tray and explicit exit. TCP/UDP and IPv4/IPv6. Stable identity, bounded recent history, background collection decoupled from rendering. No bandwidth, packet capture, firewall, process control, threat scoring, maps, geolocation, AI, device surveillance or enterprise functions.

## Brand commitments
Name: NetPeek. Repository slug: `netpeek`. Tagline: “Your connections, at a glance.” Friendly, light cartoon detailing, soft bright colors, rounded components, readable type, quiet short interactions. No constantly moving decoration. The user authorized the implementer to decide visual details.

## Evidence
User-supplied Snitch-Remix-Codex-Prompt.md dated audit 2026-10-29. Upstream https://github.com/aixisstudio/Snitch at 062f2a8c3ba95ac811988710579874100d667956. Current workstation Windows 11 Enterprise build 22631, .NET Framework 4.8.09032. No Npcap available. No existing local code or design.

## Principles
IP/port are observed facts. A TCP connection is not proof of recent transfer. UDP local binding is not a known remote connection. Unknown attribution stays unknown. Local operation never initiates network requests or downloads. Independently named and attributed under AGPL-3.0-or-later.

## Accessibility
Keyboard operation, visible focus, Windows high DPI, resizable desktop window, selectable/copyable long details, system reduced-motion preference and clear loading/empty/error/stale states.
