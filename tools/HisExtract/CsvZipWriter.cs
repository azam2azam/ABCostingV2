using System.IO.Compression;
using System.Text;

namespace HisExtract;

/// <summary>Writes numbered UTF-8 CSVs to a folder, then zips the folder on dispose.</summary>
public sealed class CsvZipWriter : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);
    private readonly List<string> _log = [];
    private readonly string _readme;
    private readonly string _setupId;
    private readonly string _projectId;

    /// <summary>Data rows per CSV page. Later pages are <c>NN_Entity_p2.csv</c> so they are not overwritten.</summary>
    public static int PageRows { get; set; } = 50_000;

    public CsvZipWriter(string zipPath, string readme, Settings s)
    {
        ZipPath = Path.GetFullPath(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ZipPath)!);
        Dir = Path.Combine(Path.GetDirectoryName(ZipPath)!, Path.GetFileNameWithoutExtension(ZipPath));
        Directory.CreateDirectory(Dir);
        _readme = readme;
        _setupId = s.SetupId;
        _projectId = s.ProjectId.ToString();
    }

    public string ZipPath { get; }
    public string Dir { get; }
    public bool Completed { get; set; }

    public CsvFile Create(string numberedName, params string[] headers)
    {
        string[] all = ["setupId", "projectId", .. headers];
        return new CsvFile(this, numberedName, all);
    }

    public void WriteManifest(string pack, DateTime? fromInclusive, DateTime? toExclusive)
    {
        using var csv = Create("00_manifest.csv",
            "pack", "from", "toInclusive", "toExclusive", "extractedAtUtc");
        var toInc = toExclusive is { } t ? t.AddDays(-1).ToString("yyyy-MM-dd") : "";
        csv.Write(
            pack,
            fromInclusive?.ToString("yyyy-MM-dd") ?? "",
            toInc,
            toExclusive?.ToString("yyyy-MM-dd") ?? "",
            DateTime.UtcNow.ToString("u"));
    }

    public void Note(string line)
    {
        _log.Add(line);
        Console.WriteLine("  " + line);
    }

    internal void NoteInternal(string line) => _log.Add(line);

    internal StreamWriter OpenPage(string numberedName)
    {
        var path = Path.Combine(Dir, numberedName);
        var w = new StreamWriter(path, false, Utf8);
        return w;
    }

    public void Dispose()
    {
        var logText = string.Join("\r\n", _log) + "\r\n";
        File.WriteAllText(Path.Combine(Dir, "extract-log.txt"), logText, Utf8);
        File.WriteAllText(Path.Combine(Dir, "README.txt"), _readme + "\r\n\r\n" + logText, Utf8);
        if (!Completed)
        {
            Console.WriteLine($"  CSV folder left at {Dir} (zip not written because the extract failed).");
            return;
        }
        if (File.Exists(ZipPath)) File.Delete(ZipPath);
        ZipFile.CreateFromDirectory(Dir, ZipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
        Console.WriteLine($"  Wrote {ZipPath}");
    }

    public sealed class CsvFile : IDisposable
    {
        private readonly CsvZipWriter _pack;
        private readonly string _baseName;
        private readonly string[] _headers;
        private StreamWriter _w;
        private string _currentName;
        private int _page = 1;
        private int _pageRows;
        public int Rows { get; private set; }

        internal CsvFile(CsvZipWriter pack, string numberedName, string[] headers)
        {
            _pack = pack;
            _baseName = numberedName;
            _headers = headers;
            _currentName = numberedName;
            _w = pack.OpenPage(numberedName);
            _w.WriteLine(string.Join(",", headers.Select(Csv.Escape)));
        }

        public void Write(params string?[] cells)
        {
            if (_pageRows >= PageRows) Rotate();
            string?[] row = [_pack._setupId, _pack._projectId, .. cells];
            _w.WriteLine(string.Join(",", row.Select(Csv.Escape)));
            Rows++;
            _pageRows++;
        }

        private void Rotate()
        {
            CloseCurrent(log: true);
            _page++;
            _pageRows = 0;
            _currentName = PageName(_baseName, _page);
            _w = _pack.OpenPage(_currentName);
            _w.WriteLine(string.Join(",", _headers.Select(Csv.Escape)));
            Console.WriteLine($"    {_currentName}: page {_page}...");
        }

        private void CloseCurrent(bool log)
        {
            _w.Flush();
            _w.Dispose();
            if (log)
            {
                var line = $"{_currentName}: {_pageRows:N0} rows";
                _pack.NoteInternal(line);
                Console.WriteLine($"    {line}");
            }
        }

        public void Dispose()
        {
            CloseCurrent(log: true);
            if (_page > 1)
            {
                var line = $"{_baseName}: {Rows:N0} rows in {_page} files";
                _pack.NoteInternal(line);
                Console.WriteLine($"    {line}");
            }
        }
    }

    public static string PageName(string numberedName, int page)
    {
        if (page <= 1) return numberedName;
        var ext = Path.GetExtension(numberedName);
        var stem = Path.GetFileNameWithoutExtension(numberedName);
        return $"{stem}_p{page}{ext}";
    }
}

public static class Csv
{
    public static string Escape(string? v)
    {
        v ??= "";
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
