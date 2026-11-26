# Repository Guidelines

## Project Structure

- `windows-app/ChelbasketDesktop/` contains the Windows WPF application.
- `mac-app/ChelbasketMac/` contains the Avalonia macOS application. Shared client, model, post, package, and update code is linked from the Windows project; keep shared behavior compatible with both targets.
- `skills/download-tv-covers/SKILL.md` and `plugin.json` define the assistant plugin.
- `.github/workflows/` builds Windows and macOS releases. `README.md` documents use and downloads; platform-specific notes live beside each app.

## Build and Development

Run commands from the repository root. The workflows use the .NET 10 SDK; Windows targets .NET 10 and macOS targets .NET 8:

```powershell
dotnet build windows-app/ChelbasketDesktop/ChelbasketDesktop.csproj -c Release
dotnet build mac-app/ChelbasketMac/ChelbasketMac.csproj -c Release
.\windows-app\publish.ps1
```

The first two commands compile each app. The PowerShell script publishes a self-contained Windows x64 EXE to `windows-app/publish/`. macOS release artifacts are produced by `.github/workflows/macos-app.yml` for `osx-arm64` and `osx-x64`.

## Code Style

Use the existing C# conventions: four spaces, nullable reference types, implicit usings, `PascalCase` for public types and members, and `_camelCase` for private fields. Keep shared logic platform-neutral; place UI and operating-system-specific code in the corresponding app. XAML/Avalonia view names should match their code-behind. Avoid committing generated `bin/`, `obj/`, `publish/`, or local build artifacts.

## Tests and Verification

There is currently no test project or configured test framework. For code changes, build the affected app; when changing shared code, build both projects. Manually check the relevant user flow when UI behavior changes, and include the platform and result in the pull request.

## Commits and Pull Requests

Recent commits use short imperative Russian descriptions (for example, `Исправить открытие окна VK Видео`); English update commits also exist. Keep subjects concise and focused. Pull requests should explain the user-visible change, list verification commands/results, link related issues when applicable, and include screenshots for UI changes. Never push a branch unless the repository maintainer explicitly asks.

## Configuration and Releases

Do not commit credentials, VK tokens, personal configuration, or generated packages. Bump the relevant project `<Version>` for app releases. A tag such as `app-v1.1.1` triggers release workflows; coordinate release tags and pushes with the maintainer.
