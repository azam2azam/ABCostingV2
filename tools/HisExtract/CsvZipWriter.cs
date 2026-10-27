using System.IO.Compression;
using System.Text;

namespace HisExtract;

/// <summary>Writes numbered UTF-8 CSVs to a folder, then zips the folder on dispose.</summary>
public sealed class CsvZipWriter : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);
    private readonly List<string> _log = [];
    private readonly string _readme;

    public CsvZipWriter(string zipPath, string readme)
    {
        ZipPath = Path.GetFullPath(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ZipPath)!);
        Dir = Path.Combine(Path.GetDirectoryName(ZipPath)!, Path.GetFileNameWithoutExtension(ZipPath));
        Directory.CreateDirectory(Dir);
        _readme = readme;
    }

    public string ZipPath { get; }
    public string Dir { get; }
    public bool Completed { get; set; }

    public CsvFile Create(string numberedName, params string[] headers)
    {
        var path = Path.Combine(Dir, numberedName);
        return new CsvFile(numberedName, headers, path, _log);
    }

    public void Note(string line)
    {
        _log.Add(line);
        Console.WriteLine("  " + line);
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
        private readonly string _name;
        private readonly StreamWriter _w;
        private readonly List<string> _log;
        public int Rows { get; private set; }

        public CsvFile(string name, string[] headers, string path, List<string> log)
        {
            _name = name;
            _log = log;
            _w = new StreamWriter(path, false, Utf8);
            _w.WriteLine(string.Join(",", headers.Select(Csv.Escape)));
        }

        public void Write(params string?[] cells)
        {
            _w.WriteLine(string.Join(",", cells.Select(Csv.Escape)));
            Rows++;
            if (Rows % 50_000 == 0)
            {
                _w.Flush();
                Console.WriteLine($"    {_name}: {Rows:N0} rows...");
            }
        }

        public void Dispose()
        {
            _w.Flush();
            _w.Dispose();
            _log.Add($"{_name}: {Rows:N0} rows");
            Console.WriteLine($"    {_name}: {Rows:N0} rows");
        }
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
