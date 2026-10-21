Synthetic one-second 440 Hz AAC audio in MP4, with no network input. Both files together are 20,463 bytes. The public test key is `00112233445566778899aabbccddeeff`; the key identifier is `102132435465768798a9bacbdcedfe0f`.

Creation commands (ffmpeg 9.0.2):

```sh
ffmpeg -v error -nostdin -f lavfi -i sine=frequency=440:duration=1 -threads 1 -c:a aac -encryption_scheme cenc-aes-ctr -encryption_key 00112233445566778899aabbccddeeff -encryption_kid 102132435465768798a9bacbdcedfe0f encrypted.m4a
ffmpeg -v error -nostdin -decryption_key 00112233445566778899aabbccddeeff -i encrypted.m4a -map 0:a:0 -c copy clear.m4a
```

Tests check `ffmpeg -version` and `ffprobe -version`. With both tools available, they generate encrypted input and run the real subprocesses. Without them, they use these fixtures and a controlled process runner to exercise validation, publication, and streaming. Dedicated real-process tests are explicitly skipped when the tools are absent; fixture substitution does not prove real decryption works on that machine.
