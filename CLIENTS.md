# Client Compatibility

This document lists compatible and incompatible music clients for Allstarr.

## Jellyfin Clients

[Jellyfin](https://jellyfin.org/) is a free and open-source media server. Allstarr connects via the Jellyfin API using your Jellyfin user login. (I plan to move this to api key if possible)

### Compatible Jellyfin Clients

- [Feishin](https://github.com/jeffvli/feishin) (Mac/Windows/Linux)
<img width="1691" height="1128" alt="image" src="https://github.com/user-attachments/assets/c602f71c-c4dd-49a9-b533-1558e24a9f45" />


- [Musiver](https://music.aqzscn.cn/en/) (Android/iOS/Windows/Android)
<img width="523" height="1025" alt="image" src="https://github.com/user-attachments/assets/135e2721-5fd7-482f-bb06-b0736003cfe7" />


- [Finamp](https://github.com/jmshrv/finamp) (Android/iOS)

_Working on getting more currently_

## Subsonic/Navidrome Clients

[Navidrome](https://www.navidrome.org/) and other Subsonic-compatible servers are supported via the Subsonic API.

### Compatible Subsonic Clients

#### PC
- [Aonsoku](https://github.com/victoralvesf/aonsoku)
- [Feishin](https://github.com/jeffvli/feishin)
- [Subplayer](https://github.com/peguerosdc/subplayer)
- [Aurial](https://github.com/shrimpza/aurial)

#### Android
- [Tempus](https://github.com/eddyizm/tempus)
- [Substreamer](https://substreamerapp.com/)

#### iOS
- [Narjo](https://www.reddit.com/r/NarjoApp/)
- [Arpeggi](https://www.reddit.com/r/arpeggiApp/)

> **Want to improve client compatibility?** Pull requests are welcome!

## Incompatible Clients

These clients are **not compatible** with Allstarr due to architectural limitations:

- [Symfonium](https://symfonium.app/) - Uses offline-first architecture and never queries the server for searches, making streaming provider integration impossible. [See details](https://support.symfonium.app/t/suggestions-on-search-function/1121/)
