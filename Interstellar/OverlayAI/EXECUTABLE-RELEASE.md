# Executable release

Run `publish.cmd` or `publish.ps1 -Mode Portable` on a Windows machine with the .NET 8 SDK installed. The output is a self-contained, single-file x64 executable at `dist\portable\Interstellar.exe`. Microsoft Edge WebView2 Runtime is still required for the chat and browser.

Use `publish.ps1 -Mode Small` for a smaller framework-dependent executable that requires the .NET 8 Desktop Runtime. `publish.ps1 -Mode Both` creates both variants.

Configuration and DPAPI-protected API keys remain under `%AppData%\OverlayAI`.
