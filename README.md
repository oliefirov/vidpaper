# VidPaper

A tiny Windows tray app that plays a video or animation as your desktop wallpaper.
It is a single small executable, has no installer and no dependencies other than FFmpeg's `ffplay`.

- Works with anything FFmpeg can play: `mp4`, `webm`, `mkv`, `mov`, `gif` and more
- Video loops forever, no audio
- Pauses automatically while a maximized or fullscreen window covers the desktop
- Remembers your video and can start with Windows
- Runs at below-normal priority

## Install

1. **Get FFmpeg** (VidPaper needs `ffplay.exe`):
   ```
   winget install Gyan.FFmpeg
   ```
   Or download a build from <https://www.gyan.dev/ffmpeg/builds/> and put `ffplay.exe` next to `VidPaper.exe`.
2. **Download `VidPaper.exe`** from the [Releases](../../releases) page.
3. **Run it** and pick a video. The wallpaper starts right away.

If VidPaper says `ffplay.exe` was not found right after a `winget` install, sign out and back in, or copy `ffplay.exe` next to `VidPaper.exe`.

The exe is not code-signed, so Windows SmartScreen may warn about it, and some antivirus tools can be suspicious of programs that move other windows around. You can read the whole source in [`src/VidPaper.cs`](src/VidPaper.cs).

## Use

VidPaper lives in the system tray (it may be under the `^` overflow arrow). Double-click the icon to choose another video, or right-click for the menu:

| Item | What it does |
| --- | --- |
| Choose video... | Pick a new wallpaper |
| Pause | Freeze the video |
| Alternate desktop mode | Use the other attach method (try this if nothing shows) |
| Pause when a window covers the desktop | Saves CPU/battery while the wallpaper is hidden |
| Renderer | SDL renderer used by `ffplay`. `direct3d11` is the default and the one that works on newer Windows 11 |
| Run at Windows startup | Adds/removes a `Run` registry entry for the current user |
| Open log | Opens `%APPDATA%\VidPaper\log.txt` |
| Exit | Stops the video and restores your normal wallpaper |

Settings are stored in `%APPDATA%\VidPaper\`.

## Performance

`ffplay` decodes on the CPU, so a lighter file means a lighter wallpaper. On integrated graphics, 1080p / 30 fps H.264 is the sweet spot. 4K or high-fps VP9/AV1 files cost much more. Convert a heavy file once with:

```
ffmpeg -i input.webm -vf "scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,fps=30" -c:v libx264 -preset slow -crf 23 -pix_fmt yuv420p -an wallpaper.mp4
```

## Troubleshooting

- **Nothing shows up**: look at the bare desktop (Win+D) first, since a maximized window hides the wallpaper. Then open the tray menu and check **Renderer** is `direct3d11`, then try **Alternate desktop mode**.
- **A balloon says ffplay exited**: test the file in a terminal with `ffplay -loop 0 -an your_video.mp4`. If that fails, the problem is the file or your FFmpeg build.

## How it works

1. VidPaper starts `ffplay` with a borderless window sized to your primary monitor.
2. It asks Explorer to create the layer that sits behind the desktop icons and re-parents the `ffplay` window into it.
   - On Windows 11 24H2 and newer, the window becomes a layered child of `Progman` placed between the icons and the system wallpaper.
   - On classic layouts it becomes a child of the `WorkerW` window behind the icons.
3. A 1.5 second timer restarts `ffplay` if it dies, repairs the z-order if Explorer changes it, and suspends the process while the desktop is covered.

## Status and limitations

- Tested on a Windows 11 desktop with the newer (24H2+) layout. The classic Windows 10 / early Windows 11 code path is implemented but less tested, so reports are welcome.
- Primary monitor only.
- No audio.
- Wallpaper changes made through Windows Settings are not touched; exiting VidPaper brings your normal wallpaper back.

To remove VidPaper: choose Exit, untick **Run at Windows startup**, then delete `VidPaper.exe` and `%APPDATA%\VidPaper`.

## License

[MIT](LICENSE)
