# AudioMuse status

AudioMuse is shelved. Its connection, discovery and preview code remains in the
build, but Sounds is hidden from the normal dashboard. It is not part of the
supported setup flow. An existing saved connection does not make Sounds appear.

The ordinary For you pages, listening history, taste, mixes and releases remain
available without AudioMuse. The older Intelligence workspace requires the
explicit `development` release profile.

The retained connection uses an external analyzed Jellyfin library. It does not
make AudioMuse a catalog, storage or routing authority. Saved connection keys
remain encrypted. Core startup does not require that service.

The retained implementation lives in `allstarr/Core/AudioMuse/` and
`allstarr/Controllers/AudioMuseController.cs`. Its administration endpoints remain
compiled and registered; hiding the dashboard page does not remove those routes.
