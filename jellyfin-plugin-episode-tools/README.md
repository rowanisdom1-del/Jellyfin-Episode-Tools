# Episode Tools (Jellyfin 12 plugin)

Adds four tasks under **Dashboard > Scheduled Tasks > Episode Tools**, each with a Run button:

1. Rename episodes: preview (changes nothing)
2. Rename episodes (`Show - S01E02 - Title.mkv`)
3. Convert MKV to MP4: preview (changes nothing)
4. Convert MKV to MP4 (lossless, no re-encoding)

The output of every run is in **Dashboard > Logs** (search for `Episode Tools`). The two scripts are
embedded in the DLL, so there is only one file to install. Settings (folders, delete originals) are on
the plugin's own settings page.

Built for Jellyfin 12.x (.NET 10, ABI 12.0.0.0).

## 1. Build the DLL with GitHub

1. Create a new repository on GitHub (for example `Jellyfin-Episode-Tools`) and upload everything in this
   folder, including the hidden `.github` folder.
2. Open the **Actions** tab. The "Build plugin" run starts by itself. If it turns red, open it and copy the
   error text.
3. To get a download link that works with `wget`, create a release: on your computer in the repo folder run
   `git tag v1.0.0 && git push origin v1.0.0`, or use GitHub's "Releases > Draft a new release" with the tag `v1.0.0`.
   The workflow then attaches `EpisodeTools_1.0.0.0.zip` to the release.

## 2. Install on the Pi (Jellyfin installed with apt)

```bash
sudo apt install unzip acl
wget https://github.com/<your-user>/<your-repo>/releases/download/v1.0.0/EpisodeTools_1.0.0.0.zip
sudo mkdir -p /var/lib/jellyfin/plugins/EpisodeTools_1.0.0.0
sudo unzip -o EpisodeTools_1.0.0.0.zip -d /var/lib/jellyfin/plugins/EpisodeTools_1.0.0.0
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/EpisodeTools_1.0.0.0
```

Jellyfin runs as the user `jellyfin`, which must be allowed to rename files in your media folder:

```bash
sudo setfacl -R -m u:jellyfin:rwX /mnt/storage/media
sudo setfacl -R -d -m u:jellyfin:rwX /mnt/storage/media
```

Then restart Jellyfin and open the dashboard:

```bash
sudo systemctl restart jellyfin
```

Requirements on the server: `python3`, `bash` and `ffmpeg` (`sudo apt install python3 ffmpeg`).

## Notes

- Jellyfin in Docker: the plugin folder is `<your config folder>/plugins/EpisodeTools_1.0.0.0`, but the
  container has no `python3`, so the rename tasks will fail with a clear message.
- Folders: converting scans the whole `/mnt/storage/media` (series and movies). Renaming stays on
  `/mnt/storage/media/Series`, because the rename script takes the first folder under its start folder as the
  show name.
- If the settings page does not open, the defaults above still work. The settings file
  is `/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.EpisodeTools.xml`.
- Converted `.mp4` files are owned by the `jellyfin` user and readable by everyone.
- Stop button: stopping a convert task removes its half-finished `.part` file.
- Audio: files with DTS, TrueHD or FLAC audio cannot go into an MP4 as they are. With the setting
  "Convert DTS, TrueHD and FLAC audio to AAC" (on by default) only those audio tracks are re-encoded to AAC
  (5.1 at 448k, stereo at 192k) and the video is still copied untouched. Turn it off to skip such files instead.
  If you also delete the .mkv, the original lossless audio is gone for good.
- Updating the plugin: unzip the new zip over the existing plugin folder, then restart Jellyfin.
