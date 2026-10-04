# Wolf Den

A web UI for managing Wolf

## Quickstart

> [!IMPORTANT]
>
> Wolf Den requires access to the Wolf UNIX socket to function correctly.
>
> To enable the Wolf socket:
>
> 1. Set the `WOLF_SOCKET_PATH` environment variable in your Wolf container:
> ```bash
> -e WOLF_SOCKET_PATH=/var/run/wolf/wolf.sock
> ```
> 2. Mount the socket location to the host machine:
> ```bash
> -v /var/run/wolf:/var/run/wolf
> ```
> 3. Ensure the `wolf.sock` file is created inside the container at `/var/run/wolf/wolf.sock`.

```bash
docker run --name wolf-den \
  -p 8080:8080 \
  -e WOLF_SOCKET_PATH=/var/run/wolf/wolf.sock \
  -v /var/run/wolf:/var/run/wolf \
  -v /etc/wolf/wolf-den:/app/wolf-den/ \
  ghcr.io/games-on-whales/wolf-den:stable
```

### Example compose

Follow the [Wolf quickstart guide](https://games-on-whales.github.io/wolf/stable/user/quickstart.html) for your
platform; here's an example using the Intel/AMD platform

```yaml 
version: "3"
services:
  wolf:
    image: ghcr.io/games-on-whales/wolf:stable
    environment:
      # Add this line
      - WOLF_SOCKET_PATH=/var/run/wolf/wolf.sock
    volumes:
      - /etc/wolf/:/etc/wolf
      - /var/run/docker.sock:/var/run/docker.sock:rw
      - /dev/:/dev/:rw
      - /run/udev:/run/udev:rw
      # Wolf will create the socket in your host under this path
      - /var/run/wolf:/var/run/wolf
    device_cgroup_rules:
      - 'c 13:* rmw'
    devices:
      - /dev/dri
      - /dev/uinput
      - /dev/uhid
    network_mode: host
    restart: unless-stopped

  wolf-den:
    image: ghcr.io/games-on-whales/wolf-den:stable
    ports:
      - 8080:8080
    environment:
      - WOLF_SOCKET_PATH=/var/run/wolf/wolf.sock
    volumes:
      - /etc/wolf/wolf-den:/app/wolf-den/
      # Mount the Wolf socket from the host
      - /var/run/wolf:/var/run/wolf
      # Optional, enables Icon/Cover picker for Profiles/Apps.
      - /etc/wolf/covers:/etc/wolf/covers
      # Optional, enables the Compatibility Tools page (Proton-GE, Luxtorpeda, etc.).
      # Wolf Den will auto-create the per-OS subdirectories on startup.
      - /etc/wolf/compatibilitytools.d:/etc/wolf/compatibilitytools.d
```

### Compatibility Tools

Wolf Den can install and remove Steam-style compatibility tools (Proton-GE,
Luxtorpeda, Boxtron, Roberta, etc.) into per-OS directories on the host. Upload
accepts `.tar.gz`, `.tar.xz`, `.tar.bz2`, `.tar`, and `.zip` archives.

The defaults in `appsettings.json` are:

| OS Target | Host Path |
| --- | --- |
| Fedora 43 | `/etc/wolf/compatibilitytools.d/fedora43` |
| Ubuntu    | `/etc/wolf/compatibilitytools.d/ubuntu` |

With `/etc/wolf/compatibilitytools.d` mounted (see the compose example above),
Wolf Den creates both subdirectories for you on first launch.

> [!IMPORTANT]
>
> For Steam to *see* the installed tools, your Steam app's runner container
> must mount the matching OS directory at Steam's
> `compatibilitytools.d` location. Wolf Den cannot add this mount on your
> behalf — it runs in its own container and does not have authority over
> app-runner configuration.
>
> Add a mount to each Steam app via the **Apps → Edit defaults → Default mounts** field. For example, for a Fedora 43-based Steam app:
>
> ```
> /etc/wolf/compatibilitytools.d/fedora43:/home/retro/.steam/root/compatibilitytools.d
> ```
>
> (Adjust the container-side path to match your Steam image's user home.)

To override the default targets, set them in `appsettings.json` or via
environment variables, e.g.:

```
WOLF_CompatibilityTools__Targets__0__Name=Fedora 43
WOLF_CompatibilityTools__Targets__0__Path=/etc/wolf/compatibilitytools.d/fedora43
```
### App catalog and profiles

**Apps** is the persistent system catalog. Set each app's container image, default
icon, shared mounts, and runner settings there. Saving a catalog app works while
Wolf is offline. Catalog entries remain saved when enabled or disabled in a profile.

Fresh installations discover `apps/*/assets/wolf.config.toml` from the GOW
repository and populate the catalog automatically. This imports app definitions
and image references; Docker image downloads remain Wolf's responsibility. A
failed repository request leaves the UI usable. Successful imports are retained,
and **Add new apps from GOW** retries missing definitions without overwriting
customizations or restoring deliberately removed entries. Completed initial
imports do not require a repository connection on later starts.

This fork also imports the published Plasma Desktop definition from
[`Greaper88/gow`, branch `plasma-container`](https://github.com/Greaper88/gow/blob/plasma-container/apps/plasma/assets/wolf.config.toml),
using `ghcr.io/greaper88/plasma:fedora`. Extra app definitions are configured under
`GowCatalog:AdditionalTemplates`, each with `Repository`, `Ref`, and `Path`.

This fork uses matching app icons from
[Greaper88/wolf-logos/itself](https://github.com/Greaper88/wolf-logos/tree/main/itself).
Apps without a matching icon, blank icon settings, and failed image loads display
the bundled generic placeholder. Default icon URLs and Wolf-side icon paths can
be edited in Apps.

The source can be configured in `appsettings.json` or with environment variables:

```yaml
environment:
  WOLF_GowCatalog__Repository: games-on-whales/gow
  WOLF_GowCatalog__Ref: master
  WOLF_GowCatalog__AutoPopulate: "true"
  WOLF_GowCatalog__IconBaseUrl: https://raw.githubusercontent.com/Greaper88/wolf-logos/main/itself
  WOLF_GowCatalog__Icons__steam: wolf-itself-steam.png
```

Set `AutoPopulate` to `false` for a manually managed catalog. A configured fork
must contain the same app-definition directory structure. Custom locally built
apps, such as Plasma Desktop, can be added manually, from pasted TOML, or from
existing Wolf profiles without publishing them to GOW first.

On an existing installation, **Import existing profile apps** links the current
Wolf-UI apps to catalog defaults while preserving their IDs, mounts, icons, and
other settings. Apps with the same name and container image share a catalog entry;
existing differences in mounts, icons, runner settings, and streaming fields stay
with their profiles. Names ignore case and whitespace when matching old templates.
Different images or named apps remain separate; ambiguous saved variants require
an exact configuration match. Old saved SQLite apps are imported
once into the new catalog without removing their database records.

**Profiles** shows all enabled apps, available catalog apps, and the differences
between each profile's mounts and the defaults. Toggle apps, change their order,
replace/add/remove mounts, and optionally override an app icon. **Use default
mounts** resets mount overrides. Unchecking the icon override restores icon
inheritance; checking it and leaving the URL blank explicitly selects the
placeholder. Save the profile to make its settings available to Wolf-UI.

After editing a catalog default, **Apply defaults to enabled profiles** updates
linked profile apps while preserving overrides. Mounts are matched by their
container destination, including explicit removals. For example:

```text
Default Steam library: /srv/steam-library:/games:rw
One profile's override: /home/player/steam-library:/games:rw
```

Changing the default source updates inheriting profiles and preserves that
profile's `/games` override. Sources refer to the Docker host, so a shared game
library need not be mounted inside wolf-den itself. Profile-specific changes to
other fields and unknown fork fields are retained during default updates.

### Storage and API compatibility

The container saves `app-catalog.json` under `/app/wolf-den`, covered by the
persistent volume in the compose example. `WOLF_DataPath` overrides the catalog
and recovery-file directory. Outside the image, the default is `wolf-den` under
.NET's local application-data directory, beside the existing SQLite database.
Use one wolf-den process per catalog directory.

Profile operations use Wolf's JSON API and preserve fields added by forks, along
with both Docker and process runners. Den does not rewrite the live TOML directly.
The TOML template reader accepts table arrays and inline arrays, supplies missing
optional fields, preserves extension fields, and reports syntax/source errors.
HTTP failures are reported before attempting to parse a downloaded template.

Wolf currently provides profile remove/add endpoints rather than an atomic
profile update. Before replacing a profile, Den checks for external changes and
writes a private `profile-recovery-*.json` copy to persistent storage. If adding
the replacement fails, Den checks the server state before attempting to restore
the original. A failed multi-profile default application keeps successful
progress so it can be retried. There remains a brief remove/add window and an
external writer could race after the comparison; an atomic Wolf endpoint would
be needed to eliminate those limitations. Retained recovery files can be used
for manual recovery if Wolf becomes unreachable during a save.

An unavailable Wolf API shows an error on Profiles while Apps remains available.
Event-stream disconnects no longer shut down wolf-den. `WOLF_SOCKET_PATH` accepts
both `/var/run/wolf/wolf.sock` and `unix:///var/run/wolf/wolf.sock`; the socket must
be an active listener, not just a file left behind by a stopped Wolf process.

### Development checks

```sh
dotnet test WolfLeash.Tests/WolfLeash.Tests.csproj
dotnet build WolfLeash.sln -c Release
```

Regression tests cover TOML forms, repository discovery, offline/partial imports,
persistent customizations, mount and icon inheritance, profile field preservation,
conflicting edits, failed saves, and populated legacy SQLite migrations. Browser
checks should use a disposable Wolf API and a separate `WOLF_DataPath` rather
than changing live profiles.
