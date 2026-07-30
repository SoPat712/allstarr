#!/usr/bin/env bash
set -euo pipefail

: "${JELLYFIN_TOKEN:?Set JELLYFIN_TOKEN to a temporary Jellyfin API key or access token}"

DIRECT_BASE="${DIRECT_BASE:-https://jellyfin.joshpatra.me}"
ALLSTARR_BASE="${ALLSTARR_BASE:-https://jfm.joshpatra.me}"
SAMPLES="${SAMPLES:-3}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-20}"

for command in curl jq awk; do
    command -v "$command" >/dev/null || { echo "Missing required command: $command" >&2; exit 1; }
done
[[ "$SAMPLES" =~ ^[1-9][0-9]*$ ]] || { echo "SAMPLES must be a positive integer" >&2; exit 1; }

started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
run_id="${started_at//[:T-]/}"
users_file="$(mktemp)"
items_file="$(mktemp)"
response_file="$(mktemp)"
timings_file="$(mktemp)"
trap 'rm -f "$users_file" "$items_file" "$response_file" "$timings_file"' EXIT

auth=(-H "X-Emby-Token: $JELLYFIN_TOKEN" -H "User-Agent: AllstarrLiveSmoke/$run_id")
echo "live-smoke-start=$started_at samples=$SAMPLES range_bytes=65536"

curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$DIRECT_BASE/Users" -o "$users_file"
best_user_id=""
best_audio_count=-1
while IFS= read -r user_id; do
    curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        "$DIRECT_BASE/Users/$user_id/Items?Recursive=true&IncludeItemTypes=Audio&Limit=1" -o "$response_file"
    audio_count="$(jq -r '.TotalRecordCount // 0' "$response_file")"
    first_audio_id="$(jq -r '.Items[0].Id // empty' "$response_file")"
    if [[ -n "$first_audio_id" ]]; then
        probe_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" -o /dev/null -w '%{http_code}' \
            "$ALLSTARR_BASE/Users/$user_id/Items/$first_audio_id")"
    else
        probe_code=0
    fi
    if [[ "$probe_code" == 200 ]] && (( audio_count > best_audio_count )); then
        best_user_id="$user_id"
        best_audio_count="$audio_count"
    fi
done < <(jq -r '.[].Id' "$users_file")
[[ -n "$best_user_id" && "$best_audio_count" -gt 0 ]] ||
    { echo "No Jellyfin user with audio visible to Allstarr was found" >&2; exit 1; }

items_query="Recursive=true&IncludeItemTypes=Audio&Limit=100&Fields=PrimaryImageAspectRatio%2CProviderIds%2CMediaSources%2CAlbumId%2CArtistItems%2CGenres"
curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
    "$DIRECT_BASE/Users/$best_user_id/Items?$items_query" -o "$items_file"
media_id="$(jq -r 'first(.Items[] | select(((.MediaSources // []) | length) > 0)) | .Id // empty' "$items_file")"
art_id="$(jq -r 'first(.Items[] | select(.ImageTags.Primary != null)) | .Id // empty' "$items_file")"
lyrics_id="$(jq -r 'first(.Items[] | select(.HasLyrics == true)) | .Id // empty' "$items_file")"
album_id="$(jq -r 'first(.Items[] | select(.AlbumId != null)) | .AlbumId // empty' "$items_file")"
artist_id="$(jq -r 'first(.Items[] | select(((.ArtistItems // []) | length) > 0)) | .ArtistItems[0].Id // empty' "$items_file")"
search_term="$(jq -r 'first(.Items[] | select(.Name != null)) | .Name // empty' "$items_file")"
search_term_encoded="$(jq -rn --arg value "$search_term" '$value | @uri')"
[[ -n "$media_id" ]] || { echo "No streamable audio item found in the first 100 items" >&2; exit 1; }

measure() {
    local label="$1" url="$2"
    shift 2
    : >"$timings_file"
    for ((sample = 1; sample <= SAMPLES; sample++)); do
        curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$@" -o /dev/null \
            -w '%{http_code}\t%{size_download}\t%{time_namelookup}\t%{time_connect}\t%{time_appconnect}\t%{time_starttransfer}\t%{time_total}\n' \
            "$url" >>"$timings_file"
    done
    awk -v label="$label" '
        { ok += ($1 >= 200 && $1 < 400); bytes += $2; dns += $3; connect += $4; tls += $5; ttfb += $6; total += $7; codes[$1]++ }
        END {
            code_summary = ""
            for (code in codes) code_summary = code_summary (code_summary ? "," : "") code ":" codes[code]
            printf "%-24s ok=%d/%d codes=%s avg_bytes=%.0f dns_ms=%.1f connect_ms=%.1f tls_ms=%.1f ttfb_ms=%.1f total_ms=%.1f\n",
                label, ok, NR, code_summary, bytes / NR, dns * 1000 / NR, connect * 1000 / NR,
                tls * 1000 / NR, ttfb * 1000 / NR, total * 1000 / NR
        }' "$timings_file"
}

checks=0
failures=0

check_code() {
    local label="$1" expected="$2" method="$3" url="$4" code
    shift 4
    if [[ "$method" == HEAD ]]; then
        code="$(curl -sS --head --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$@" -o /dev/null -w '%{http_code}' "$url")"
    else
        code="$(curl -sS -X "$method" --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$@" -o /dev/null -w '%{http_code}' "$url")"
    fi
    checks=$((checks + 1))
    if [[ ",$expected," == *",$code,"* ]]; then
        printf 'PASS %-34s status=%s\n' "$label" "$code"
    else
        printf 'FAIL %-34s expected=%s actual=%s\n' "$label" "$expected" "$code"
        failures=$((failures + 1))
    fi
}

check_public_code() {
    local label="$1" expected="$2" url="$3" code
    code="$(curl -sS --max-time "$TIMEOUT_SECONDS" -H "User-Agent: AllstarrLiveSmoke/$run_id" \
        -o /dev/null -w '%{http_code}' "$url")"
    checks=$((checks + 1))
    if [[ ",$expected," == *",$code,"* ]]; then
        printf 'PASS %-34s status=%s\n' "$label" "$code"
    else
        printf 'FAIL %-34s expected=%s actual=%s\n' "$label" "$expected" "$code"
        failures=$((failures + 1))
    fi
}

check_json() {
    local label="$1" url="$2" filter="$3" code
    code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" -o "$response_file" -w '%{http_code}' "$url")"
    checks=$((checks + 1))
    if [[ "$code" == 200 ]] && jq -e "$filter" "$response_file" >/dev/null; then
        printf 'PASS %-34s json-shape\n' "$label"
    else
        printf 'FAIL %-34s status=%s json-filter=%s\n' "$label" "$code" "$filter"
        failures=$((failures + 1))
    fi
}

echo "functional-and-security-checks"
check_public_code "public bootstrap" "200" "$ALLSTARR_BASE/System/Info/Public"
check_public_code "protected route needs auth" "401" "$ALLSTARR_BASE/Items"
invalid_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" -H 'X-Emby-Token: invalid-live-smoke-token' \
    -H "User-Agent: AllstarrLiveSmoke/$run_id" -o /dev/null -w '%{http_code}' "$ALLSTARR_BASE/Items")"
checks=$((checks + 1))
if [[ "$invalid_code" == 401 || "$invalid_code" == 403 ]]; then
    printf 'PASS %-34s status=%s\n' "invalid credential rejected" "$invalid_code"
else
    printf 'FAIL %-34s expected=401,403 actual=%s\n' "invalid credential rejected" "$invalid_code"
    failures=$((failures + 1))
fi

check_json "current user profile" "$ALLSTARR_BASE/Users/$best_user_id" \
    '.Id != null and (.Policy.IsDisabled | type == "boolean")'
check_json "audio browse shape" "$ALLSTARR_BASE/Users/$best_user_id/Items?$items_query" \
    '(.Items | type == "array") and (.TotalRecordCount | type == "number") and all(.Items[]; .Type == "Audio")'
check_json "generic music constraint" "$ALLSTARR_BASE/Items?UserId=$best_user_id&Recursive=true&Limit=25" \
    '(.Items | type == "array") and all(.Items[]; (.Type == "Audio" or .Type == "MusicAlbum" or .Type == "MusicArtist" or .Type == "Playlist" or .Type == "MusicGenre"))'
check_json "search hints shape" "$ALLSTARR_BASE/Users/$best_user_id/Search/Hints?SearchTerm=$search_term_encoded&IncludeItemTypes=Audio&Limit=10" \
    '(.SearchHints | type == "array") and all(.SearchHints[]; .Type == "Audio")'
check_json "audio item detail" "$ALLSTARR_BASE/Users/$best_user_id/Items/$media_id" '.Id != null and .Type == "Audio"'
check_json "artists browse" "$ALLSTARR_BASE/Artists?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "MusicArtist")'
check_json "album artists browse" "$ALLSTARR_BASE/Artists/AlbumArtists?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "MusicArtist")'
[[ -z "$artist_id" ]] || check_json "artist detail" "$ALLSTARR_BASE/Artists/$artist_id?UserId=$best_user_id" '.Type == "MusicArtist"'
check_json "item filters" "$ALLSTARR_BASE/Items/Filters?UserId=$best_user_id" 'type == "object"'
check_json "item filters2" "$ALLSTARR_BASE/Items/Filters2?UserId=$best_user_id" 'type == "object"'
check_json "genres browse" "$ALLSTARR_BASE/Genres?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "Genre")'
check_json "latest music only" "$ALLSTARR_BASE/Items/Latest?UserId=$best_user_id&Limit=10" \
    'type == "array" and all(.[]; (.Type == "Audio" or .Type == "MusicAlbum" or .Type == "MusicArtist" or .Type == "Playlist" or .Type == "MusicGenre"))'
check_json "suggestions music only" "$ALLSTARR_BASE/Items/Suggestions?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; (.Type == "Audio" or .Type == "MusicAlbum" or .Type == "MusicArtist" or .Type == "Playlist" or .Type == "MusicGenre"))'
check_json "resume music only" "$ALLSTARR_BASE/UserItems/Resume?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "Audio")'
check_json "media folders music only" "$ALLSTARR_BASE/Library/MediaFolders?UserId=$best_user_id" \
    '(.Items | type == "array") and all(.Items[]; .CollectionType == "music")'
check_json "user views music only" "$ALLSTARR_BASE/UserViews?UserId=$best_user_id" \
    '(.Items | type == "array") and all(.Items[]; .CollectionType == "music")'
check_json "music library root" "$ALLSTARR_BASE/Items/Root?UserId=$best_user_id" \
    '.Id != null and .CollectionType == "music"'
check_json "music-only counts" "$ALLSTARR_BASE/Items/Counts?UserId=$best_user_id" \
    '.MovieCount == 0 and .SeriesCount == 0 and .EpisodeCount == 0 and .MusicVideoCount == 0'
check_json "playback info" "$ALLSTARR_BASE/Items/$media_id/PlaybackInfo?UserId=$best_user_id" \
    '(.MediaSources | type == "array") and (.MediaSources | length > 0)'
check_json "similar music" "$ALLSTARR_BASE/Items/$media_id/Similar?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; (.Type == "Audio" or .Type == "MusicAlbum" or .Type == "MusicArtist"))'
check_json "instant mix" "$ALLSTARR_BASE/Songs/$media_id/InstantMix?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "Audio")'
[[ -z "$album_id" ]] || check_json "album instant mix" "$ALLSTARR_BASE/Albums/$album_id/InstantMix?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "Audio")'

for denied_path in \
    "/Videos/security-probe/stream" \
    "/Movies/Recommendations" \
    "/Shows/NextUp" \
    "/LiveTv/Channels" \
    "/Channels" \
    "/SyncPlay/List" \
    "/Plugins" \
    "/ScheduledTasks" \
    "/System/Logs" \
    "/System/ActivityLog/Entries" \
    "/Library/VirtualFolders" \
    "/Users/New" \
    "/api/admin/health" \
    "/Notifications" \
    "/Branding/Configuration" \
    "/Items/Latest?IncludeItemTypes=Movie,Audio" \
    "/Items/Suggestions?Type=Audio,Movie" \
    "/Genres?IncludeItemTypes=Audio,Series" \
    "/Search/Hints?IncludeItemTypes=Movie&SearchTerm=test"; do
    check_code "deny ${denied_path%%\?*}" "403" GET "$ALLSTARR_BASE$denied_path"
done
check_code "deny generic item deletion" "403" DELETE "$ALLSTARR_BASE/Items/allstarr-security-probe"

measure "direct public-info" "$DIRECT_BASE/System/Info/Public"
measure "allstarr public-info" "$ALLSTARR_BASE/System/Info/Public"
measure "direct audio-list" "$DIRECT_BASE/Users/$best_user_id/Items?$items_query"
measure "allstarr audio-list" "$ALLSTARR_BASE/Users/$best_user_id/Items?$items_query"

playlist_query="Recursive=true&IncludeItemTypes=Playlist&Limit=100"
curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
    "$DIRECT_BASE/Users/$best_user_id/Items?$playlist_query" -o "$response_file"
direct_playlists="$(jq -r '.TotalRecordCount // (.Items | length) // 0' "$response_file")"
playlist_id="$(jq -r '.Items[0].Id // empty' "$response_file")"
curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query" -o "$response_file"
allstarr_playlists="$(jq -r '.TotalRecordCount // (.Items | length) // 0' "$response_file")"
echo "playlist-counts direct=$direct_playlists allstarr=$allstarr_playlists"
check_json "playlist browse shape" "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query" \
    '(.Items | type == "array") and all(.Items[]; .Type == "Playlist")'
if [[ -n "$playlist_id" ]]; then
    check_json "playlist entries music only" "$ALLSTARR_BASE/Playlists/$playlist_id/Items?UserId=$best_user_id&Limit=100" \
        '(.Items | type == "array") and all(.Items[]; .Type == "Audio")'
fi
measure "direct playlist-list" "$DIRECT_BASE/Users/$best_user_id/Items?$playlist_query"
measure "allstarr playlist-list" "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query"

if [[ -n "$art_id" ]]; then
    check_code "artwork retrieval" "200,304" GET \
        "$ALLSTARR_BASE/Items/$art_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
    measure "direct artwork" "$DIRECT_BASE/Items/$art_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
    measure "allstarr artwork" "$ALLSTARR_BASE/Items/$art_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
else
    echo "artwork skipped=no candidate in first 100 audio items"
fi

lyrics_id="${lyrics_id:-$media_id}"
check_code "lyrics response" "200,404" GET "$ALLSTARR_BASE/Audio/$lyrics_id/Lyrics?UserId=$best_user_id"
measure "direct lyrics" "$DIRECT_BASE/Audio/$lyrics_id/Lyrics?UserId=$best_user_id"
measure "allstarr lyrics" "$ALLSTARR_BASE/Audio/$lyrics_id/Lyrics?UserId=$best_user_id"
check_code "download HEAD" "200,206" HEAD "$ALLSTARR_BASE/Items/$media_id/Download?UserId=$best_user_id"
check_code "file HEAD" "200,206" HEAD "$ALLSTARR_BASE/Items/$media_id/File?UserId=$best_user_id"
check_code "stream HEAD" "200,206" HEAD "$ALLSTARR_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id"
check_code "universal audio HEAD" "200,206,302" HEAD "$ALLSTARR_BASE/Audio/$media_id/universal?UserId=$best_user_id"
check_code "stream bounded range" "206" GET \
    "$ALLSTARR_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id" --range 0-65535
measure "direct stream-64k" "$DIRECT_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id" --range 0-65535
measure "allstarr stream-64k" "$ALLSTARR_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id" --range 0-65535

nonmusic_id=""
while IFS= read -r user_id && [[ -z "$nonmusic_id" ]]; do
    nonmusic_id="$(
        curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
            "$DIRECT_BASE/Users/$user_id/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode,MusicVideo&Limit=1" |
            jq -r '.Items[0].Id // empty'
    )"
done < <(jq -r '.[].Id' "$users_file")
if [[ -n "$nonmusic_id" ]]; then
    check_code "deny real non-music item" "403" GET \
        "$ALLSTARR_BASE/Items/$nonmusic_id?UserId=$best_user_id"
    check_code "deny real non-music playback" "403" GET \
        "$ALLSTARR_BASE/Items/$nonmusic_id/PlaybackInfo?UserId=$best_user_id"
    check_code "deny real non-music artwork" "403" GET \
        "$ALLSTARR_BASE/Items/$nonmusic_id/Images/Primary?UserId=$best_user_id"
else
    echo "real non-music item checks skipped=no backend candidate"
fi

if command -v ping >/dev/null; then
    for host in "${DIRECT_BASE#*://}" "${ALLSTARR_BASE#*://}"; do
        echo -n "ping $host "
        ping -c "$SAMPLES" "$host" 2>/dev/null | tail -n 1 || echo "unavailable"
    done
fi

echo "live-smoke-end=$(date -u +%Y-%m-%dT%H:%M:%SZ) log-window-start=$started_at checks=$checks failures=$failures"
(( failures == 0 ))
