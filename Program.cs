using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

// KodiNfoCreator
// Durchsucht einen Filmordner (Unterordner "Titel (Jahr)"), sucht jeden Film auf TMDB
// (inkl. Adult-Inhalten) und schreibt eine Kodi-kompatible .nfo-Datei neben die Filmdatei.
//
// Standard ist eine "Parsing NFO" (https://kodi.wiki/view/NFO_files/Parsing): die Datei enthält
// nur die TMDB-URL des Films, Kodis Scraper holt damit die Metadaten selbst.
// Mit --full wird stattdessen eine vollständige XML-Metadaten-NFO geschrieben.
//
// Aufruf:
//   KodiNfoCreator <Filmordner> [--lang de-DE] [--overwrite] [--dry-run] [--auto] [--full] [--token <TOKEN>]
//
// Token: TMDB "API Read Access Token" (v4, beginnt mit "eyJ...") oder v3 "API Key".
//   Zu finden unter https://www.themoviedb.org/settings/api (mit deinem Account eingeloggt).
//   Reihenfolge: --token, Umgebungsvariable TMDB_TOKEN, gespeicherte Datei, sonst Abfrage.

var videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg",
    ".iso", ".webm", ".flv", ".vob", ".divx", ".xvid", ".3gp", ".ogm", ".rmvb"
};

string? root = null;
string language = "de-DE";
bool overwrite = false, dryRun = false, auto = false, full = false;
string? token = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--lang": language = args[++i]; break;
        case "--overwrite": overwrite = true; break;
        case "--dry-run": dryRun = true; break;
        case "--auto": auto = true; break;
        case "--full": full = true; break;
        case "--token": token = args[++i]; break;
        case "-h":
        case "--help":
            Console.WriteLine("KodiNfoCreator <Filmordner> [--lang de-DE] [--overwrite] [--dry-run] [--auto] [--full] [--token <TOKEN>]");
            Console.WriteLine("  --overwrite  vorhandene .nfo-Dateien überschreiben");
            Console.WriteLine("  --dry-run    nichts schreiben, nur anzeigen");
            Console.WriteLine("  --auto       keine Rückfragen (bei unklarem Treffer überspringen)");
            Console.WriteLine("  --full       vollständige XML-NFO statt Parsing-NFO (nur TMDB-URL)");
            return 0;
        default: root ??= args[i]; break;
    }
}

Console.OutputEncoding = Encoding.UTF8;

if (string.IsNullOrWhiteSpace(root))
{
    Console.Write("Filmordner: ");
    root = Console.ReadLine()?.Trim().Trim('"');
}
if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
{
    Console.Error.WriteLine($"Ordner nicht gefunden: {root}");
    return 1;
}

// ---- Token laden / speichern ----
var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KodiNfoCreator");
var tokenFile = Path.Combine(configDir, "tmdb_token.txt");
token ??= Environment.GetEnvironmentVariable("TMDB_TOKEN");
if (string.IsNullOrWhiteSpace(token) && File.Exists(tokenFile))
    token = File.ReadAllText(tokenFile).Trim();
bool tokenPrompted = false;
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("TMDB API Read Access Token oder API Key (https://www.themoviedb.org/settings/api):");
    token = Console.ReadLine()?.Trim();
    tokenPrompted = true;
}
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Kein Token angegeben.");
    return 1;
}

var tmdb = new Tmdb(token);
try
{
    await tmdb.ValidateToken();
    Console.WriteLine("TMDB-Login OK.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"TMDB-Login fehlgeschlagen: {ex.Message}");
    return 1;
}
if (tokenPrompted)
{
    Directory.CreateDirectory(configDir);
    File.WriteAllText(tokenFile, token);
    Console.WriteLine($"Token gespeichert in {tokenFile}");
}

// ---- Ordner verarbeiten ----
var folderRegex = new Regex(@"^(?<title>.+?)\s*\((?<year>\d{4})\)", RegexOptions.Compiled);
var idRegex = new Regex(@"tmdb(?:id)?[-=_ ]?(?<id>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
var sampleRegex = new Regex(@"(^|[\W_])(sample|trailer)([\W_]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

int created = 0, skipped = 0, notFound = 0, failed = 0;
var dirs = Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
Console.WriteLine($"{dirs.Count} Ordner gefunden.\n");

foreach (var dir in dirs)
{
    var folderName = Path.GetFileName(dir);
    try
    {
        var videos = Directory.GetFiles(dir)
            .Where(f => videoExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !sampleRegex.IsMatch(Path.GetFileNameWithoutExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Pro Videodatei "<Dateiname>.nfo"; ohne Videodatei (z.B. BDMV/VIDEO_TS) "movie.nfo"
        var nfoPaths = videos.Count > 0
            ? videos.Select(v => Path.ChangeExtension(v, ".nfo")).ToList()
            : [Path.Combine(dir, "movie.nfo")];

        if (!overwrite && nfoPaths.All(File.Exists))
        {
            Console.WriteLine($"[skip] {folderName} (nfo vorhanden)");
            skipped++;
            continue;
        }

        // TMDB-ID direkt im Ordnernamen? z.B. "Film (2001) {tmdb-12345}"
        int? movieId = null;
        var idMatch = idRegex.Match(folderName);
        if (idMatch.Success) movieId = int.Parse(idMatch.Groups["id"].Value);

        string title = folderName;
        int? year = null;
        var m = folderRegex.Match(folderName);
        if (m.Success)
        {
            title = m.Groups["title"].Value.Trim();
            year = int.Parse(m.Groups["year"].Value);
        }

        if (movieId == null)
        {
            // Mehrere Suchbegriffe probieren, bis einer eindeutig passt; Treffer werden gesammelt
            var results = new List<SearchResult>();
            SearchResult? best = null;
            foreach (var query in QueryVariants(title))
            {
                var found = await tmdb.SearchMovie(query, year, language);
                if (found.Count == 0 && year != null)
                    found = await tmdb.SearchMovie(query, null, language);
                results.AddRange(found.Where(f => results.All(r => r.Id != f.Id)));
                best = PickBest(results, title, year);
                if (best != null) break;
            }

            if (best != null)
            {
                movieId = best.Id;
            }
            else if (!auto)
            {
                Console.WriteLine($"[ ?? ] {folderName}: kein eindeutiger Treffer.");
                var shown = Math.Min(results.Count, 8);
                for (int r = 0; r < shown; r++)
                    Console.WriteLine($"         {r + 1}) {results[r].Title} ({results[r].Year}) [tmdb {results[r].Id}]");
                Console.Write("         Nummer, TMDB-ID oder Enter zum Überspringen: ");
                var input = Console.ReadLine()?.Trim();
                if (int.TryParse(input, out var n))
                    movieId = n >= 1 && n <= shown ? results[n - 1].Id : n;
            }
        }

        if (movieId == null)
        {
            Console.WriteLine($"[----] {folderName}: nicht gefunden");
            notFound++;
            continue;
        }

        var details = await tmdb.GetMovie(movieId.Value, language);

        foreach (var nfoPath in nfoPaths)
        {
            if (dryRun || (!overwrite && File.Exists(nfoPath))) continue;
            if (full)
            {
                var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
                using var writer = XmlWriter.Create(nfoPath, settings);
                NfoBuilder.Build(details, language).Save(writer);
            }
            else
            {
                // Parsing NFO: nur die URL, sonst nichts
                File.WriteAllText(nfoPath, $"https://www.themoviedb.org/movie/{movieId}\n", new UTF8Encoding(false));
            }
        }

        var foundYear = details.Str("release_date") is { Length: >= 4 } rd ? rd[..4] : "?";
        var adult = details.TryGetProperty("adult", out var a) && a.ValueKind == JsonValueKind.True ? " [ADULT]" : "";
        var yearWarning = year != null && foundYear != year.ToString() ? "  !! Jahr weicht ab" : "";
        Console.WriteLine($"[ OK ] {folderName}  ->  {details.Str("title")} ({foundYear}) [tmdb {movieId}]{adult}{(dryRun ? " (dry-run)" : "")}{yearWarning}");
        created++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] {folderName}: {ex.Message}");
        failed++;
    }
}

Console.WriteLine($"\nFertig. Erstellt: {created}, übersprungen: {skipped}, nicht gefunden: {notFound}, Fehler: {failed}");
if (args.Length == 0)
{
    // Per Doppelklick gestartet: Fenster offen lassen
    Console.WriteLine("Enter zum Beenden...");
    Console.ReadLine();
}
return 0;

static SearchResult? PickBest(List<SearchResult> results, string title, int? year)
{
    if (results.Count == 0) return null;
    var norm = Normalize(title);

    bool TitleMatch(SearchResult r) => Normalize(r.Title) == norm || Normalize(r.OriginalTitle) == norm;
    bool YearMatch(SearchResult r, int tolerance) => year == null || (r.Year != null && Math.Abs(r.Year.Value - year.Value) <= tolerance);

    return results.FirstOrDefault(r => TitleMatch(r) && YearMatch(r, 0))
        ?? results.FirstOrDefault(r => TitleMatch(r) && YearMatch(r, 1))
        ?? (results.Count(TitleMatch) == 1 ? results.First(TitleMatch) : null) // Jahr auf TMDB weicht ab, Titel eindeutig
        ?? (results.Count == 1 && YearMatch(results[0], 1) ? results[0] : null);
}

// TMDB findet nichts, wenn der Suchbegriff " - " enthält. Ordnernamen nutzen das aber oft als Ersatz
// für ":" (unter Windows im Dateinamen nicht erlaubt), z.B. "Jurassic World - Das gefallene Königreich".
static IEnumerable<string> QueryVariants(string title)
{
    var separator = new Regex(@"\s+[-–—]\s+|\s*:\s*");
    return new[]
        {
            title,
            separator.Replace(title, " ").Trim(), // "Jurassic World Das gefallene Königreich"
            separator.Split(title)[0].Trim(),     // "Jurassic World" (Treffer muss trotzdem den vollen Titel haben)
        }
        .Where(q => q.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase);
}

static string Normalize(string s)
{
    var sb = new StringBuilder();
    foreach (var c in s.Normalize(NormalizationForm.FormD))
    {
        if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
        if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
    }
    return sb.ToString();
}

record SearchResult(int Id, string Title, string OriginalTitle, int? Year);

static class JsonExt
{
    public static string Str(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
}

class Tmdb
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public Tmdb(string token)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token.StartsWith("eyJ"))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else
            _apiKey = token;
    }

    private async Task<JsonElement> Get(string path)
    {
        if (_apiKey != null) path += (path.Contains('?') ? "&" : "?") + "api_key=" + _apiKey;
        for (int attempt = 0; ; attempt++)
        {
            using var resp = await _http.GetAsync(path);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 5)
            {
                await Task.Delay(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2));
                continue;
            }
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"TMDB {(int)resp.StatusCode}: {body}");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
    }

    public async Task ValidateToken() => await Get("authentication");

    public async Task<List<SearchResult>> SearchMovie(string query, int? year, string language)
    {
        var path = $"search/movie?query={Uri.EscapeDataString(query)}&include_adult=true&language={language}";
        if (year != null) path += $"&year={year}";
        var r = await Get(path);
        return r.Arr("results").Select(x => new SearchResult(
            x.GetProperty("id").GetInt32(),
            x.Str("title"),
            x.Str("original_title"),
            x.Str("release_date") is { Length: >= 4 } d && int.TryParse(d[..4], out var y) ? y : null)).ToList();
    }

    public async Task<JsonElement> GetMovie(int id, string language)
    {
        var lang2 = language.Split('-')[0];
        return await Get($"movie/{id}?language={language}&append_to_response=credits,release_dates,images,translations&include_image_language={lang2},en,null");
    }
}

static class NfoBuilder
{
    private const string ImgBase = "https://image.tmdb.org/t/p/original";
    private const string PosterPreview = "https://image.tmdb.org/t/p/w500";
    private const string FanartPreview = "https://image.tmdb.org/t/p/w780";

    private static XElement Thumb(string path, string previewBase, string? aspect = null)
    {
        var thumb = new XElement("thumb", ImgBase + path);
        if (aspect != null) thumb.Add(new XAttribute("aspect", aspect));
        thumb.Add(new XAttribute("preview", previewBase + path));
        return thumb;
    }

    public static XDocument Build(JsonElement d, string language)
    {
        var movie = new XElement("movie");
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) movie.Add(new XElement(name, value));
        }

        var id = d.GetProperty("id").GetInt32();
        var releaseDate = d.Str("release_date");

        // Fallback auf Englisch, falls in der gewünschten Sprache kein Text existiert
        var plot = d.Str("overview");
        var tagline = d.Str("tagline");
        if (string.IsNullOrWhiteSpace(plot) && d.TryGetProperty("translations", out var tr))
        {
            var en = tr.Arr("translations").FirstOrDefault(t => t.Str("iso_639_1") == "en");
            if (en.ValueKind == JsonValueKind.Object && en.TryGetProperty("data", out var enData))
            {
                plot = enData.Str("overview");
                if (string.IsNullOrWhiteSpace(tagline)) tagline = enData.Str("tagline");
            }
        }

        Add("title", d.Str("title"));
        Add("originaltitle", d.Str("original_title"));
        if (releaseDate.Length >= 4) Add("year", releaseDate[..4]);
        Add("premiered", releaseDate);
        Add("plot", plot);
        Add("outline", plot);
        Add("tagline", tagline);
        if (d.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Number && rt.GetInt32() > 0)
            Add("runtime", rt.GetInt32().ToString());

        var votes = d.TryGetProperty("vote_count", out var vc) && vc.ValueKind == JsonValueKind.Number ? vc.GetInt32() : 0;
        if (votes > 0 && d.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number)
        {
            movie.Add(new XElement("ratings",
                new XElement("rating", new XAttribute("name", "themoviedb"), new XAttribute("max", "10"), new XAttribute("default", "true"),
                    new XElement("value", va.GetDouble().ToString("0.0", CultureInfo.InvariantCulture)),
                    new XElement("votes", votes))));
        }

        // Altersfreigabe: Land aus der Sprache (de-DE -> DE), sonst US
        var country = language.Contains('-') ? language.Split('-')[1].ToUpperInvariant() : "US";
        var cert = GetCertification(d, country);
        if (cert != null) Add("mpaa", country == "DE" ? $"FSK {cert}" : country == "US" ? $"Rated {cert}" : cert);
        else if ((cert = GetCertification(d, "US")) != null) Add("mpaa", $"Rated {cert}");

        movie.Add(new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), id));
        var imdb = d.Str("imdb_id");
        if (!string.IsNullOrWhiteSpace(imdb))
            movie.Add(new XElement("uniqueid", new XAttribute("type", "imdb"), imdb));

        foreach (var g in d.Arr("genres")) Add("genre", g.Str("name"));
        foreach (var c in d.Arr("production_countries")) Add("country", c.Str("name"));
        foreach (var s in d.Arr("production_companies")) Add("studio", s.Str("name"));

        if (d.TryGetProperty("belongs_to_collection", out var col) && col.ValueKind == JsonValueKind.Object)
            movie.Add(new XElement("set", new XElement("name", col.Str("name"))));

        // Bilder: volle Auflösung + verkleinerte Vorschau (wie Kodis eigener TMDB-Scraper)
        var poster = d.Str("poster_path");
        if (!string.IsNullOrEmpty(poster))
            movie.Add(Thumb(poster, PosterPreview, "poster"));
        if (d.TryGetProperty("images", out var imgs))
            foreach (var p in imgs.Arr("posters").Where(p => p.Str("file_path") != poster).Take(5))
                movie.Add(Thumb(p.Str("file_path"), PosterPreview, "poster"));

        var fanart = new XElement("fanart");
        var backdrop = d.Str("backdrop_path");
        if (!string.IsNullOrEmpty(backdrop)) fanart.Add(Thumb(backdrop, FanartPreview));
        if (d.TryGetProperty("images", out imgs))
            foreach (var b in imgs.Arr("backdrops").Where(b => b.Str("file_path") != backdrop).Take(5))
                fanart.Add(Thumb(b.Str("file_path"), FanartPreview));
        if (fanart.HasElements) movie.Add(fanart);

        if (d.TryGetProperty("credits", out var credits))
        {
            foreach (var c in credits.Arr("crew").Where(c => c.Str("job") == "Director"))
                Add("director", c.Str("name"));
            foreach (var c in credits.Arr("crew").Where(c => c.Str("department") == "Writing").DistinctBy(c => c.Str("name")))
                Add("credits", c.Str("name"));

            int order = 0;
            foreach (var a in credits.Arr("cast").Take(30))
            {
                var actor = new XElement("actor",
                    new XElement("name", a.Str("name")),
                    new XElement("role", a.Str("character")),
                    new XElement("order", order++));
                var profile = a.Str("profile_path");
                if (!string.IsNullOrEmpty(profile)) actor.Add(new XElement("thumb", ImgBase + profile));
                movie.Add(actor);
            }
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), movie);
    }

    private static string? GetCertification(JsonElement d, string country)
    {
        if (!d.TryGetProperty("release_dates", out var rd)) return null;
        var entry = rd.Arr("results").FirstOrDefault(r => r.Str("iso_3166_1") == country);
        if (entry.ValueKind != JsonValueKind.Object) return null;
        return entry.Arr("release_dates").Select(x => x.Str("certification")).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
    }
}
