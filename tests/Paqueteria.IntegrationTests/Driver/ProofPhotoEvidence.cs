using System.Text;
using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// DRV-PROOF-PHOTO-REDUCE-2026-10-20 (project owner, "Reducir y quitar metadatos (Recommended)"): the photos the
/// browser suites pick, and the checks on the photo the phone stores, uploads and the server keeps - a JPEG of at most
/// 1600 px per side, re-encoded on the phone, with no metadata segment.
/// </summary>
internal static class ProofPhotoEvidence
{
    internal const int MaximumSide = 1600;

    /// <summary>A decodable 1x1 PNG of rgb(15, 118, 110); the phone turns it into a 1x1 JPEG.</summary>
    internal static readonly byte[] TealPixelPng =
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d,
        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, 0xde, 0x00, 0x00, 0x00,
        0x0c, 0x49, 0x44, 0x41, 0x54, 0x78, 0xda, 0x63, 0xe0, 0x2f, 0xcb, 0x03,
        0x00, 0x01, 0x8b, 0x00, 0xf4, 0x02, 0x38, 0xf3, 0x86, 0x00, 0x00, 0x00,
        0x00, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
    ];

    /// <summary>The colour of <see cref="TealPixelPng"/>.</summary>
    internal static readonly int[] Teal = [15, 118, 110];

    /// <summary>
    /// The synthetic EXIF GPS photo shared with the web unit tests
    /// (apps/web/src/driver/proofs/proof-photo-exif-gps.fixture.jpg): 2000x1000 pixels stored, EXIF orientation 6
    /// (shown 1000x2000, red on top and blue below), invented make and model, impossible GPS coordinates, XMP, a
    /// comment and an ICC profile. Reduced, it is an 800x1600 JPEG.
    /// </summary>
    internal static byte[] ExifGpsJpeg() =>
        File.ReadAllBytes(Path.Combine(
            DriverStopsNextServer.FindRepositoryRoot(),
            "apps",
            "web",
            "src",
            "driver",
            "proofs",
            "proof-photo-exif-gps.fixture.jpg"));

    /// <summary>The frame size and every marker before the first scan.</summary>
    internal sealed record JpegHeader(int Width, int Height, IReadOnlyList<int> Markers);

    /// <summary>Reads the marker segments up to the first scan; fails on anything that is not a JPEG header.</summary>
    internal static JpegHeader ReadJpegHeader(byte[] bytes)
    {
        Assert.True(bytes.Length > 4 && bytes[0] == 0xff && bytes[1] == 0xd8, "The photo does not start with SOI.");
        var markers = new List<int>();
        int? width = null;
        int? height = null;
        var index = 2;
        while (true)
        {
            Assert.True(index + 3 < bytes.Length && bytes[index] == 0xff, "The JPEG header is malformed.");
            var marker = bytes[index + 1];
            var length = (bytes[index + 2] << 8) | bytes[index + 3];
            Assert.True(length >= 2 && index + 2 + length <= bytes.Length, "A JPEG segment runs past the end.");
            markers.Add(marker);
            if (marker is >= 0xc0 and <= 0xcf and not 0xc4 and not 0xc8 and not 0xcc && width is null)
            {
                height = (bytes[index + 5] << 8) | bytes[index + 6];
                width = (bytes[index + 7] << 8) | bytes[index + 8];
            }
            if (marker == 0xda)
            {
                break;
            }
            index += 2 + length;
        }
        Assert.NotNull(width);
        Assert.NotNull(height);
        return new JpegHeader(width.Value, height.Value, markers);
    }

    /// <summary>
    /// The bytes are the reduced proof photo: a JPEG whose only APP segment is the JFIF header, with no comment, no
    /// EXIF, XMP or ICC data, nothing after EOI and no side longer than 1600 px.
    /// </summary>
    internal static JpegHeader AssertReducedJpeg(byte[] bytes)
    {
        var header = ReadJpegHeader(bytes);
        Assert.Equal(0xe0, header.Markers[0]);
        Assert.Equal("JFIF\0", Encoding.Latin1.GetString(bytes, 6, 5));
        Assert.DoesNotContain(header.Markers, marker => marker is >= 0xe1 and <= 0xef or 0xfe);
        Assert.Single(header.Markers, marker => marker == 0xe0);
        Assert.InRange(Math.Max(header.Width, header.Height), 1, MaximumSide);
        Assert.True(bytes[^2] == 0xff && bytes[^1] == 0xd9, "The photo does not end with EOI.");
        var text = Encoding.Latin1.GetString(bytes);
        foreach (var metadata in new[]
                 {
                     "Exif\0\0",
                     "http://ns.adobe.com/xap/1.0/",
                     "ICC_PROFILE",
                     "Paquetenvia sintetica",
                     "Fixture sin camara",
                     "Paquetenvia synthetic proof fixture",
                 })
        {
            Assert.DoesNotContain(metadata, text, StringComparison.Ordinal);
        }
        return header;
    }

    /// <summary>The RGB of one pixel of an image, decoded by the page's own browser (EXIF orientation applied).</summary>
    internal static async Task<int[]> ReadPixelAsync(IPage page, byte[] bytes, string contentType, int x, int y) =>
        await page.EvaluateAsync<int[]>(
            """
            async value => {
              const binary = atob(value.base64);
              const bytes = new Uint8Array(binary.length);
              for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
              const bitmap = await createImageBitmap(new Blob([bytes], { type: value.contentType }));
              const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
              const context = canvas.getContext("2d");
              context.drawImage(bitmap, 0, 0);
              bitmap.close();
              return Array.from(context.getImageData(value.x, value.y, 1, 1).data.slice(0, 3));
            }
            """,
            new { base64 = Convert.ToBase64String(bytes), contentType, x, y });

    /// <summary>Each channel of <paramref name="actual"/> is within JPEG rounding of <paramref name="expected"/>.</summary>
    internal static void AssertColour(int[] expected, int[] actual)
    {
        Assert.Equal(3, actual.Length);
        for (var channel = 0; channel < 3; channel++)
        {
            Assert.InRange(actual[channel], expected[channel] - 16, expected[channel] + 16);
        }
    }
}
