using System.IO.Compression;
using System.Text;

namespace Kavita.Services.Tests.Dictionary;

/// <summary>
/// Writes a minimal EPUB 3 to a temp file and deletes it on dispose. Each page is one XHTML spine item, written
/// byte for byte as given, so a test can pick its line endings and entities
/// </summary>
public sealed class EpubFixture : IDisposable
{
    public string FilePath { get; }

    public EpubFixture(string language, string css, params string[] pages)
    {
        FilePath = Path.Join(Path.GetTempPath(), $"EpubBlocks_{Guid.NewGuid():N}.epub");

        using var zip = ZipFile.Open(FilePath, ZipArchiveMode.Create);
        // The OCF spec wants mimetype first and stored, not deflated
        Add(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Add(zip, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """.ReplaceLineEndings("\n"));

        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        for (var i = 0; i < pages.Length; i++)
        {
            manifest.Append($"""<item id="page{i}" href="page{i}.xhtml" media-type="application/xhtml+xml"/>""");
            spine.Append($"""<itemref idref="page{i}"/>""");
            Add(zip, $"OEBPS/page{i}.xhtml", pages[i]);
        }

        Add(zip, "OEBPS/content.opf", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="uid">urn:uuid:{Guid.NewGuid()}</dc:identifier>
                <dc:title>Block fixture</dc:title>
                <dc:language>{language}</dc:language>
                <meta property="dcterms:modified">2026-01-01T00:00:00Z</meta>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="css" href="style.css" media-type="text/css"/>
                {manifest}
              </manifest>
              <spine page-progression-direction="{(language == "ja" ? "rtl" : "ltr")}">{spine}</spine>
            </package>
            """.ReplaceLineEndings("\n"));
        Add(zip, "OEBPS/nav.xhtml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
            <head><title>Nav</title></head>
            <body><nav epub:type="toc"><ol><li><a href="page0.xhtml">Start</a></li></ol></nav></body>
            </html>
            """.ReplaceLineEndings("\n"));
        Add(zip, "OEBPS/style.css", css);
    }

    private static void Add(ZipArchive zip, string name, string content,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        using var stream = zip.CreateEntry(name, level).Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes);
    }

    public void Dispose()
    {
        File.Delete(FilePath);
    }
}
