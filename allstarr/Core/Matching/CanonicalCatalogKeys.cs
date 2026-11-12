using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Storage;

namespace allstarr.Core.Matching;

public static class CanonicalCatalogKeys
{
    public static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // Collected playlist songs use the hash of the source's track id as their id, so that id already is the stored key.
    public static string[] StoredKeys(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigitLower) ? [Hash(value), value] : [Hash(value)];

    public static string DefaultPublicId(Guid recordingId) =>
        CanonicalRecordingRecord.DefaultPublicId(recordingId);

}
