# Interstellar

A glossy Windows AI overlay built with native WPF, .NET 8, and WebView2. It includes the supplied Interstellar artwork, streaming AI chat, a multi-tab browser, provider settings, one-shot screen clipping, and a control panel for Windows VPN profiles.

## Build and run

1. Install the .NET 8 SDK and Microsoft Edge WebView2 Runtime.
2. Run `dotnet restore` and `dotnet run -c Release` from this folder.
3. Configure OpenAI, Anthropic, Gemini, or local Ollama in **Settings**, or provide the corresponding environment variables / `.env` file. API keys saved through Settings are encrypted with Windows DPAPI for the current user.

## Screen clipping

Press **Ctrl+Shift+S** to capture the primary monitor once. Interstellar briefly gets out of the way so it does not cover the captured work.

- When Interstellar is the foreground app, the clip is attached to the Assistant composer. Type a question and press Enter (or Send) to submit it with the image.
- When another app is in the foreground, the full-resolution clip is placed on the Windows clipboard and Interstellar sends Ctrl+V to the previously focused control. Image insertion works in image-capable editors and rich-text fields; plain text-only boxes cannot embed pictures. The clip remains on the clipboard either way.
- The Capture button uses the same one-shot workflow.
- There is no live screen monitoring, background capture session, or Windows capture picker.

The clip captures the primary monitor only, matching the original quick-screenshot behavior. The clipboard retains the full-resolution image; the AI upload is JPEG-compressed and downscaled to a maximum 2560-pixel edge to reduce upload size and latency. `Ctrl+Shift+S` remains available while Interstellar is minimized.

## Browser tabs

Open **Browser** and use the `+` button to create additional independent WebView2 tabs. Each tab has its own navigation state and can be closed with its `×` tab control.

## VPN

Open **VPN** to view and connect/disconnect VPN profiles already configured in Windows. Interstellar uses the built-in `rasdial.exe` client and does not store VPN credentials or provide a VPN subscription. Use **Open Windows VPN settings** to add or manage profiles. Third-party VPN applications continue to be managed by their own clients.

## Faster responses

**Fast responses** is enabled by default. It sends a shorter recent conversation history and applies lower output limits to reduce prompt size, token use, and time spent generating long replies. The actual response speed still depends on the selected model, provider, network, and hardware. Turn it off in Settings when a longer answer or more conversation context matters.

The HTTP client reuses pooled connections, and streamed text is flushed to the UI in small batches. Fast mode does not change your selected model.

## Hotkeys

All global shortcuts use Ctrl+Shift:

| Key | Action |
|---|---|
| Space | Minimize / restore Interstellar |
| S | Clip the primary screen and insert/paste the image |
| C | Clear the conversation and cancel a running response |
| Arrow keys | Move the overlay by 40 px while it is expanded |
| T | Toggle click-through |
| ] / [ | Increase / decrease overlay opacity |
| Enter | Bring Interstellar forward and focus the assistant input |
| H | Toggle hiding the window from screen capture / streams (on by default) |

Interstellar is a normal Windows application and appears in the taskbar, Alt+Tab, Task Manager, and ordinary process listings. There is no notification-area icon or hidden background mode.

## Publish a Windows executable

Run `publish.cmd` or `publish.ps1 -Mode Portable` on Windows. The self-contained single-file output is `dist\portable\Interstellar.exe`; the Microsoft Edge WebView2 Runtime is still required. `publish.ps1 -Mode Small` creates a smaller framework-dependent executable that also requires the .NET 8 Desktop Runtime.

The app continues to use `%AppData%\OverlayAI` so existing provider settings and encrypted API keys are preserved.

## Architecture notes

The screen clipper uses a one-shot GDI desktop copy and does not load CsWinRT or SharpDX. No kernel driver, screen-recording service, process-hiding feature, or VPN tunnel implementation is installed by this project. The VPN panel controls Windows VPN profiles that the user has already created.

## Hidden from streams and recordings

By default Interstellar sets Windows' `WDA_EXCLUDEFROMCAPTURE` flag on its window, so it does not appear in OBS (display, window or game capture), Discord/Teams sharing, Game Bar or screenshots. Press **Ctrl+Shift+H** to toggle it; the status bar shows the current state. The taskbar button is also hidden by default (`HideTaskbarButton` in `%AppData%\OverlayAI\config.json`; set it to `false` and restart to get it back). With it hidden, the – button and **Ctrl+Shift+Space** hide the overlay completely and bring it back, and it no longer appears in Alt+Tab. Limits: a hardware capture card or camera pointed at your monitor still sees the window, and Interstellar still appears in Task Manager and process lists.
