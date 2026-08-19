using System.IO.Compression;
using System.Reflection;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmbeddedGeofenceEngineTests
{
    private static string EncodedPayload()
    {
        var payloadType = typeof(EmbeddedGeofenceEngine).Assembly.GetType("Slh.Tms.Api.Services.GeofenceSeedPayload", throwOnError: true)!;
        var field = payloadType.GetField("GzipBase64", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)field.GetRawConstantValue()!;
    }

    [Fact]
    public void Locate_corruption_in_padded_geofence_stream()
    {
        var compact = new string(EncodedPayload().Where(c => !char.IsWhiteSpace(c)).ToArray());
        var bytes = Convert.FromBase64String(compact + "=");
        using var raw = new MemoryStream(bytes);
        using var input = new ChunkedReadStream(raw, 16);
        using var output = new MemoryStream();
        try
        {
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            var buffer = new byte[256];
            while (true)
            {
                var read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
            Assert.Fail($"Unexpectedly decompressed full stream. compressedPosition={raw.Position}; outputLength={output.Length}");
        }
        catch (InvalidDataException exception)
        {
            Assert.Fail($"Corruption: compressedBytes={bytes.Length}; compressedPosition={raw.Position}; approxBase64={(raw.Position * 4) / 3}; outputLength={output.Length}; message={exception.Message}");
        }
    }

    private sealed class ChunkedReadStream(Stream inner, int maxChunk) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, maxChunk));
        public override int Read(Span<byte> buffer) => inner.Read(buffer[..Math.Min(buffer.Length, maxChunk)]);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
