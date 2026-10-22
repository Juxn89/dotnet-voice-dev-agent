# Whisper models

This folder is empty on purpose. Whisper models are 75 MB to 575 MB each, too big to commit, so
you download the one you want here. `.gitignore` excludes `*.bin`, so a downloaded model is never
committed by accident.

`docker-compose.yml` mounts this folder into the `whisper` container and loads
`ggml-<WHISPER_MODEL>.bin`, with `tiny.en` as the default.

## Pick one

Measured on a 4-core laptop CPU with the vocabulary prompt in `Program.cs`, one speaker, eight
sentences. Treat it as a guide, not a benchmark.

| Model | File | Size | Word error rate | Median per sentence |
|---|---|---|---|---|
| `tiny.en` (default) | `ggml-tiny.en.bin` | 78 MB | 0.68 | 1.8 s |
| `base.en` | `ggml-base.en.bin` | 148 MB | 0.59 | 5.6 s |
| `small.en` | `ggml-small.en.bin` | 488 MB | 0.48 | 13.4 s |
| `large-v3-turbo` (q5_0) | `ggml-large-v3-turbo-q5_0.bin` | 574 MB | 0.41 | over a minute |

On a CPU, only `tiny.en` and `base.en` are fast enough to hold a conversation. The `.en` models
understand English only, and they do not reject other languages: they return fluent English you
never said. `large-v3-turbo` is multilingual, but far too slow on a CPU to talk to.

## Download

From the repository root, replace `tiny.en` with the model you picked:

```bash
curl -L -o models/ggml-tiny.en.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin
```

In Windows PowerShell 5.1, `curl` is an alias for `Invoke-WebRequest`, so call `curl.exe`:

```powershell
curl.exe -L -o models/ggml-tiny.en.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin
```

## Use a model other than the default

```bash
WHISPER_MODEL=base.en docker compose up -d --force-recreate whisper
```

```powershell
$env:WHISPER_MODEL = "base.en"; docker compose up -d --force-recreate whisper
```
