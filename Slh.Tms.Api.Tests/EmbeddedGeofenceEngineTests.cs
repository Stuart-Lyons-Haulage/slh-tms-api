using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmbeddedGeofenceEngineTests
{
    private const string Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    private static string EncodedPayload()
    {
        var payloadType = typeof(EmbeddedGeofenceEngine).Assembly.GetType("Slh.Tms.Api.Services.GeofenceSeedPayload", throwOnError: true)!;
        var field = payloadType.GetField("GzipBase64", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)field.GetRawConstantValue()!;
    }

    [Fact]
    public void Recover_single_missing_base64_character_from_entire_geofence_payload()
    {
        var compact = new string(EncodedPayload().Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal(16339, compact.Length);
        Assert.Equal(3, compact.Length % 4);
        var stopwatch = Stopwatch.StartNew();
        long candidates = 0;

        // Search every possible insertion point. Indexes 0-15 are skipped because the
        // existing payload already has a valid gzip header and corruption occurs later.
        for (var index = 16; index <= compact.Length; index++)
        {
            foreach (var character in Base64Alphabet)
            {
                candidates++;
                var candidate = compact.Insert(index, character.ToString());
                try
                {
                    var bytes = Convert.FromBase64String(candidate);
                    using var input = new MemoryStream(bytes);
                    using var gzip = new GZipStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    gzip.CopyTo(output);

                    var json = Encoding.UTF8.GetString(output.ToArray());
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 53) continue;

                    var valid = document.RootElement.EnumerateArray().All(record =>
                        record.TryGetProperty("name", out var name) &&
                        !string.IsNullOrWhiteSpace(name.GetString()) &&
                        record.TryGetProperty("points", out var points) &&
                        points.ValueKind == JsonValueKind.Array &&
                        points.GetArrayLength() >= 3);
                    if (!valid) continue;

                    Assert.Fail($"RECOVERED missing Base64 character: index={index}; char={character}; correctedLength={candidate.Length}; jsonLength={json.Length}; geofences=53; candidates={candidates}; elapsedMs={stopwatch.ElapsedMilliseconds}");
                }
                catch (FormatException)
                {
                }
                catch (InvalidDataException)
                {
                }
                catch (JsonException)
                {
                }
            }
        }

        Assert.Fail($"No valid 53-geofence payload found after testing {candidates} single-character insertions across the complete Base64 string in {stopwatch.ElapsedMilliseconds} ms.");
    }
}
