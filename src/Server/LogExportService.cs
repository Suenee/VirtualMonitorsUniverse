using System.IO.Compression;
using System.Text;
using System.Xml;

namespace VirtualMonitorsUniverse.Server;

internal static class LogExportService
{
    private static readonly string[] Headers = ["Timecode", "Level", "Service", "Monitor", "Event", "Message", "Details"];

    public static void Export(string path, IReadOnlyList<LogEntry> entries)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".xlsx":
                WriteXlsx(path, entries);
                break;
            case ".csv":
                WriteCsv(path, entries);
                break;
            case ".txt":
                WriteText(path, entries);
                break;
            default:
                throw new InvalidOperationException($"Unsupported export format: {extension}");
        }
    }

    public static byte[] ExportBytes(string format, IReadOnlyList<LogEntry> entries)
    {
        var extension = "." + format.TrimStart('.').ToLowerInvariant();
        var temp = Path.Combine(Path.GetTempPath(), $"vmu-log-{Guid.NewGuid():N}{extension}");
        try
        {
            Export(temp, entries);
            return File.ReadAllBytes(temp);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    private static void WriteCsv(string path, IReadOnlyList<LogEntry> entries)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(',', Headers.Select(Csv)));
        foreach (var entry in entries) writer.WriteLine(string.Join(',', Values(entry).Select(Csv)));
    }

    private static void WriteText(string path, IReadOnlyList<LogEntry> entries)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join('\t', Headers));
        foreach (var entry in entries) writer.WriteLine(string.Join('\t', Values(entry).Select(x => x.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '))));
    }

    private static void WriteXlsx(string path, IReadOnlyList<LogEntry> entries)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
""");
        WriteEntry(archive, "_rels/.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
""");
        WriteEntry(archive, "xl/workbook.xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="VMU Log" sheetId="1" r:id="rId1"/></sheets></workbook>
""");
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>
""");

        var sheet = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using var stream = sheet.Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        xml.WriteStartDocument(true);
        xml.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        xml.WriteStartElement("sheetData");
        WriteRow(xml, Headers);
        foreach (var entry in entries) WriteRow(xml, Values(entry));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void WriteRow(XmlWriter xml, IEnumerable<string> values)
    {
        xml.WriteStartElement("row");
        foreach (var value in values)
        {
            xml.WriteStartElement("c");
            xml.WriteAttributeString("t", "inlineStr");
            xml.WriteStartElement("is");
            xml.WriteElementString("t", value);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content.TrimStart());
    }

    private static IEnumerable<string> Values(LogEntry entry)
    {
        yield return entry.Timestamp.ToString("dd.MM.yyyy HH:mm:ss.fff");
        yield return entry.Level;
        yield return entry.Service;
        yield return entry.MonitorId ?? string.Empty;
        yield return entry.Event;
        yield return entry.Message;
        yield return entry.DetailsJson ?? string.Empty;
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
