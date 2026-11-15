# One-shot screen clipping

Interstellar captures the primary monitor only when the user presses Ctrl+Shift+S or the capture button. It does not create a continuous capture session, poll frames, or show a screen-capture picker.

The window is briefly hidden during the copy so it does not cover the underlying work, then restored without activating itself. The full-resolution image is copied to the Windows clipboard. If Interstellar was foreground, the clip is attached to the Assistant composer. If another application was foreground, Interstellar sends Ctrl+V to the previously focused control; successful insertion depends on the receiving control supporting image paste. Plain text-only fields cannot embed pictures.

The AI payload is JPEG-compressed and downscaled to a maximum 2560-pixel edge to reduce upload size. The clipboard image stays full resolution. Only the clip attached to the current message is sent when the user submits it. Earlier image payloads are dropped when a newer clip is attached, and Fast responses limits the recent conversation context by default.
