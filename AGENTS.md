# NetPeek project rules

NetPeek is a native Windows x64 WPF connection viewer targeting .NET Framework 4.8. `src/NetPeek.Core/` owns sampling and identity logic; `src/NetPeek.App/` owns the interface. Preserve process identity checks, IPv4/IPv6 scope, distinct TCP and UDP semantics and honest unavailable states. Never commit private connection data or logs.

For relevant changes use `./build.ps1` and focused scripts under `tests/`; `README.md` describes source and final-executable verification. Real sockets, external network, GUI interaction and performance tests have separate scopes. `./build.ps1 -Package -SkipBuild` reuses a tested executable when packaging is requested; do not infer final-build acceptance from older candidate evidence. Documentation-only work needs proportionate checks.

Protect user data and unrelated changes. Publishing, pushing, merging, releasing, dependency changes and deletion require explicit authorization; inspect and stage only the intended paths.
