# Agent rules

Home Assistant only sees this repo after a version commit is on `origin/main`. Local builds are not a Home Assistant test.

When a change is meant to be tried in Home Assistant, publish it before handing the task back:

1. Stamp the changed App with `yyyy.MM.dd.HH.mm` from `TZ=Europe/London date '+%Y.%m.%d.%H.%M'`.
2. Use that same stamp in every version file for that App, plus its changelog.
3. Commit the change and the version bump together.
4. Push to `origin/main` and stop. Do not poll Actions or GHCR.

`config.yaml` `image` must be `ghcr.io/lmsowner/<slug>-{arch}` with no tag. Bump only the App that changed. A new App is a new install after Check for updates, not an update to Edge Gateway.

Version files and the rest of the publish rules: `.cursor/rules/ha-addon-version-publish.mdc`.
