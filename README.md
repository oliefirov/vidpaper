# VidPaper

A tiny Windows tray app that plays a video or animation as your desktop wallpaper.
It is a single small executable, has no installer and no dependencies other than FFmpeg (`ffmpeg` and `ffplay`).

- Works with anything FFmpeg can play: `mp4`, `webm`, `mkv`, `mov`, `gif` and more
- Video loops forever, no audio
- Pauses automatically while a maximized or fullscreen window covers the desktop (the last frame stays on screen) and resumes instantly
- Remembers your video and can start with Windows
- Runs at below-normal priority

## Install

1. **Get FFmpeg** (VidPaper needs both `ffmpeg.exe` and `ffplay.exe`):
   ```
   winget install Gyan.FFmpeg
   ```
   Or download a build from <https://www.gyan.dev/ffmpeg/builds/> and put `ffmpeg.exe` and `ffplay.exe` next to `VidPaper.exe`.
2. **Download `VidPaper.exe`** from the [Releases](../../releases) page.
3. **Run it** and pick a video. The wallpaper starts right away.

If VidPaper says `ffmpeg.exe` and `ffplay.exe` were not found right after a `winget` install, sign out and back in, or copy both next to `VidPaper.exe`.

The exe is not code-signed, so Windows SmartScreen may warn about it, and some antivirus tools can be suspicious of programs that move other windows around. You can read the whole source in [`src/VidPaper.cs`](src/VidPaper.cs).

## Use

VidPaper lives in the system tray (it may be under the `^` overflow arrow). Double-click the icon to choose another video, or right-click for the menu:

| Item | What it does |
| --- | --- |
| Choose video... | Pick a new wallpaper |
| Pause | Freeze the video on its current frame until you untick it |
| Alternate desktop mode | Use the other attach method (try this if nothing shows) |
| Pause when a window covers the desktop | Freezes the video while the wallpaper is hidden to save CPU, and resumes instantly when it is visible |
| Renderer | SDL renderer used by `ffplay`. `direct3d11` is the default and the one that works on newer Windows 11 |
| Run at Windows startup | Adds/removes a `Run` registry entry for the current user |
| Open log | Opens `%APPDATA%\VidPaper\log.txt` |
| Exit | Stops the video and restores your normal wallpaper |

Settings are stored in `%APPDATA%\VidPaper\`.

## Performance

`ffplay` decodes on the CPU, so a lighter file means a lighter wallpaper (paused wallpapers use almost no CPU). On integrated graphics, 1080p / 30 fps H.264 is the sweet spot. 4K or high-fps VP9/AV1 files cost much more. Convert a heavy file once with:

```
ffmpeg -i input.webm -vf "scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,fps=30" -c:v libx264 -preset slow -crf 23 -pix_fmt yuv420p -an wallpaper.mp4
```

## Troubleshooting

- **Nothing shows up**: look at the bare desktop (Win+D) first, since a maximized window hides the wallpaper. Then open the tray menu and check **Renderer** is `direct3d11`, then try **Alternate desktop mode**.
- **A balloon says ffmpeg/ffplay keep exiting**: test the file in a terminal with `ffplay -loop 0 -an your_video.mp4`. If that fails, the problem is the file or your FFmpeg build. The log also contains `ffmpeg:` error lines.
- **A tiny hitch when the video loops**: VidPaper loops by stream copy (no re-encoding). If your file hitches at the loop point, convert it once with the command above.

## How it works

1. VidPaper starts `ffmpeg`, which loops your video and streams it into `ffplay`, which shows it in a borderless window sized to your primary monitor. VidPaper passes the data from one to the other.
2. It asks Explorer to create the layer that sits behind the desktop icons and re-parents the `ffplay` window into it.
   - On Windows 11 24H2 and newer, the window becomes a layered child of `Progman` placed between the icons and the system wallpaper.
   - On classic layouts it becomes a child of the `WorkerW` window behind the icons.
3. To pause, VidPaper simply stops passing data to `ffplay`. `ffplay` keeps running and keeps showing the last frame, and `ffmpeg` waits, so nothing is suspended or restarted. Resuming continues instantly from the same point. Window focus changes are detected through a Windows event hook, with a 0.5 second timer as backup. The timer also restarts the pipeline if it dies and repairs the z-order if Explorer changes it.
4. The player window is click-through, so clicks go to your desktop icons as usual.

## Status and limitations

- Primary monitor only.
- No audio.
- GIFs are converted to MJPEG on the fly because they cannot be stream-copied, which costs a little more CPU than other formats.
- Wallpaper changes made through Windows Settings are not touched; exiting VidPaper brings your normal wallpaper back.

To remove VidPaper: choose Exit, untick **Run at Windows startup**, then delete `VidPaper.exe` and `%APPDATA%\VidPaper`.

## License

[MIT](LICENSE)
