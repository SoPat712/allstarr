# Version 2 compatibility inputs

`docker-compose.yml` and `example.env` preserve the public v2.0.2 release shapes. The example contains placeholders and empty credentials only. Keep these inputs frozen so compatibility checks exercise the old mounts, ports, environment names and health URL.

`tools/tests/v2_image_upgrade.sh` builds the current image on a disposable CI runner and runs only the old core service against a fake backend. Its isolated folders contain synthetic media. It does not contact a music service, migrate a real installation, start the old optional services or publish an image.
