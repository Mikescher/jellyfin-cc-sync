# Deployment

## Requirements

- Jellyfin **12.3** or newer.
- jClipCorn database schema `38` (main) / `3` (user data).
- jClipCorn's NFO export enabled for movies and series (Settings → NFO), so every NFO carries
  `<uniqueid type="clipcorn">`. Without it, movies and series fall back to TMDB/IMDB/name
  matching; episodes and seasons cannot be matched at all.
- The Jellyfin libraries use the **Nfo** metadata reader (library settings → Metadata readers).
- The jClipCorn folder (`ClipCornDB/`) is visible inside the Jellyfin process as a directory, and
  writable for the Jellyfin user (see below).

## Jellyfin settings

- Dashboard → NFO Settings → "Save user watch data to NFO files for": **leave empty**, or at
  least not the sync user. Otherwise Jellyfin imports jClipCorn's NFO `<playcount>`/
  `<lastplayed>` into that user's data on refresh (stale after plugin writes, and in jClipCorn's
  UI date format) and writes watch data back into jClipCorn's NFO files.
- Everything else is configured on the plugin's own page (Dashboard → Plugins → jellyfin-cc-sync).

## Making the database visible to Jellyfin

The plugin reads and writes plain files below a configured directory; it never mounts anything
itself (a container cannot mount SMB/NFS without `CAP_SYS_ADMIN`). Mount the **directory**,
never single files: a single-file bind mount pins the inode, so a file replaced by rename (as
jcc-mirror does) would stay invisible.

The plugin writes `ClipCornUserData.db` with SQLite's rollback journal, which needs:

- create/delete permission in the directory (for `ClipCornUserData.db-journal`),
- working byte-range locks on network mounts: **never** mount with `nobrl` (cifs), `nolock` or
  `local_lock` (nfs).

### A — Jellyfin runs on the same Synology

Bind-mount the share folder into the container:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:12.3
    volumes:
      - /volume1/clipcorn/ClipCornDB:/clipcorn
      # … config, cache, media
```

Plugin setting: ClipCorn directory = `/clipcorn`. The container user (root for the official
image unless `user:` is set; `PUID`/`PGID` for linuxserver images) must be allowed to write the
folder; on DSM that means a uid that has write access to the shared folder.

### B — Jellyfin in Docker on another host, SMB

A Docker volume backed by the kernel cifs client (mounted by the Docker daemon, not by the
container):

```yaml
services:
  jellyfin:
    volumes:
      - clipcorn:/clipcorn

volumes:
  clipcorn:
    driver: local
    driver_opts:
      type: cifs
      device: "//nas.local/clipcorn/ClipCornDB"
      o: "username=jellyfin,password=${CLIPCORN_SMB_PASSWORD},vers=3.0,uid=1000,gid=1000,file_mode=0664,dir_mode=0775"
```

`uid`/`gid` = the user Jellyfin runs as inside the container.

### C — NFS

```yaml
volumes:
  clipcorn:
    driver: local
    driver_opts:
      type: nfs
      o: "addr=nas.local,nfsvers=4.1,rw"
      device: ":/volume1/clipcorn/ClipCornDB"
```

NFSv4 has locking built in. Synology: enable NFS for the shared folder and squash to a user with
write access.

### D — Jellyfin without Docker

Mount the share on the host (`/etc/fstab`, systemd mount unit) with the same options as above
and point the plugin at the mount point.

## Checking the setup

Dashboard → Plugins → jellyfin-cc-sync → Settings → **Test ClipCorn access**:

- directory exists and both DB files are readable,
- directory is writable (creates and deletes `.jellyfin-cc-sync-probe`),
- lock state (who holds it, heartbeat age),
- schema versions and DUUID pairing,
- element counts read from a snapshot, and elements whose JSON columns do not parse,
- the configured jClipCorn time zone resolves.

An unmounted share usually shows up as "directory exists but is empty".

## Coexistence

- **jClipCorn** running anywhere (its lock file is present and fresh): the plugin only reads
  snapshots and queues writes until jClipCorn is closed. A jClipCorn that crashed leaves its lock
  behind; writes resume once the heartbeat is older than `LockStaleMinutes` (180, jClipCorn's own
  rule), or immediately after jClipCorn has been started and closed cleanly once.
- **jcc-mirror** (subscriber NAS): it replaces `ClipCornDB.db` by rename and never touches
  `ClipCornUserData.db`. The plugin reads the main DB from a snapshot and writes only user data,
  so both can run on the same folder.
- **jellyfin-watchnotify**: independent; the plugin's own Jellyfin writes use the `Import`
  reason, which watchnotify ignores.

## Plugin state on disk

`<jellyfin data>/jellyfin-cc-sync/` (in Docker usually `/config/data/jellyfin-cc-sync/`):

| Path | Content |
|---|---|
| `state.db` | pending updates, change history, run summaries |
| `backups/ClipCornUserData.<utc timestamp>.db` | copy taken before each write session, rotated |
| `snapshots/` | transient read snapshots, deleted after each run |

Jellyfin's built-in backup does not include this folder.
