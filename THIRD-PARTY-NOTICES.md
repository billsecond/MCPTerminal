# Third-party notices

MCPTerminal itself is MIT licensed (see `LICENSE`). It uses the following
third-party components. All are permissively licensed and compatible with
open-sourcing this project.

## Bundled in this repository

### xterm.js (`@xterm/xterm` 5.5.0) and `@xterm/addon-fit` 0.10.0
MIT License — Copyright (c) 2017-2022, The xterm.js authors; (c) 2014-2016 SourceLair
Private Company; (c) 2012-2013, Christopher Jeffrey.
Vendored under `studio/vendor/`; full license text in
`studio/vendor/xterm-LICENSE.txt`.

## Referenced at build time / redistributed in binaries

### Microsoft.Web.WebView2 (1.0.2903.40)
BSD-3-Clause (Microsoft variant) — Copyright (c) Microsoft Corporation.
NuGet package referenced by MCPTerminal Studio. Its own `NOTICE.txt` carries
BSD-3-Clause notices for Antlr3.Runtime 3.5.2-rc1 and StringTemplate4
4.0.9-rc1, plus Microsoft's LGPL reverse-engineering carve-out. If prebuilt
Studio binaries are ever distributed, ship those notices alongside them.
The WebView2 **Runtime** (proprietary, ships with Windows 10/11) is required
but is *not* redistributed by this project.

### .NET runtime (Microsoft)
MIT License — Copyright (c) .NET Foundation and Contributors.
Embedded in the self-contained builds of the terminal app. Microsoft's
`THIRD-PARTY-NOTICES.TXT` from the runtime applies to those binaries.

## Invoked, never redistributed

`script(1)`, `bash`, `pwsh`, `cmd.exe`, `wsl.exe`, `git`, `node` are executed
as separate processes on the user's machine. They are not linked, bundled, or
copied by this project, so their licenses (GPL, proprietary, or otherwise)
impose no obligations here. Do not bundle them into releases.
