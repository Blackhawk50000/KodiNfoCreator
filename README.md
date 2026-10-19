# KodiNfoCreator

Ein Kommandozeilen-Tool (.NET 10), das für eine Filmsammlung automatisch
Kodi-kompatible `.nfo`-Dateien anlegt. Jeder Film wird auf
[The Movie Database (TMDb)](https://www.themoviedb.org/) gesucht, die `.nfo`
landet direkt neben der Filmdatei.

## Funktionen

- Durchsucht einen Filmordner mit Unterordnern im Format `Titel (Jahr)`
- Findet den passenden Film auf TMDb – auch wenn der Ordnername `" - "` statt `":"` enthält
  (z. B. `Jurassic World - Das gefallene Königreich (2018)`)
- TMDb-ID direkt im Ordnernamen möglich, z. B. `Film (2001) {tmdb-12345}`
- Bei unklaren Treffern Auswahl aus einer Liste oder Eingabe der TMDb-ID
- Zwei NFO-Arten:
  - **Parsing-NFO** (Standard): enthält nur die TMDb-URL, Kodis Scraper holt den Rest selbst
    ([Kodi-Wiki](https://kodi.wiki/view/NFO_files/Parsing))
  - **Vollständige NFO** (`--full`): Titel, Handlung, Bewertung, Altersfreigabe, Genres, Studios,
    Filmreihe, Poster, Fanart, Regie, Drehbuch und Besetzung als XML
- Ordner mit Videodatei bekommen `<Dateiname>.nfo`, Ordner ohne (z. B. `BDMV`/`VIDEO_TS`) `movie.nfo`
- Sample- und Trailer-Dateien werden ignoriert, vorhandene `.nfo`-Dateien standardmäßig übersprungen

## Download

Die fertige `KodiNfoCreator.exe` gibt es unter
[Releases](https://github.com/Blackhawk50000/KodiNfoCreator/releases/latest).
Sie läuft ohne Installation und ohne .NET (Windows 10/11, x64).

## Voraussetzungen

- Ein kostenloser **TMDb-Zugang** – unter
  [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api) entweder den
  „API Read Access Token“ (v4, beginnt mit `eyJ…`) oder den v3-„API Key“ kopieren.
- Zum selbst Bauen: [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Benutzung

```
KodiNfoCreator <Filmordner> [--lang de-DE] [--overwrite] [--dry-run] [--auto] [--full] [--token <TOKEN>]
```

| Option        | Bedeutung                                                  |
|---------------|------------------------------------------------------------|
| `--lang`      | Sprache für die TMDb-Daten (Standard `de-DE`)              |
| `--overwrite` | vorhandene `.nfo`-Dateien überschreiben                    |
| `--dry-run`   | nichts schreiben, nur anzeigen                             |
| `--auto`      | keine Rückfragen – unklare Treffer werden übersprungen     |
| `--full`      | vollständige XML-NFO statt Parsing-NFO                     |
| `--token`     | TMDb-Token bzw. API-Key direkt angeben                     |

Per Doppelklick gestartet, fragt das Programm nach dem Filmordner.

### Token

Der Token wird in dieser Reihenfolge gesucht:

1. `--token`
2. Umgebungsvariable `TMDB_TOKEN`
3. gespeicherte Datei `%AppData%\KodiNfoCreator\tmdb_token.txt`
4. sonst Abfrage beim Start – ein dort eingegebener Token wird nach erfolgreicher Prüfung
   in der Datei aus Punkt 3 gespeichert

Der Token bleibt also nur lokal auf deinem Rechner.

## Selbst bauen

```
git clone https://github.com/Blackhawk50000/KodiNfoCreator.git
cd KodiNfoCreator
dotnet run -- "D:\Filme"
```

## Hinweis

Dieses Produkt nutzt die TMDb-API, ist aber nicht von TMDb unterstützt oder zertifiziert.

## Lizenz

[MIT](LICENSE)
