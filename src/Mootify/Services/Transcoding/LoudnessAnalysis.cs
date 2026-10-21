using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mootify.Configuration;

namespace Mootify.Services.Transcoding;

public sealed record LoudnessMeasurement(double IntegratedLufs, double TruePeakDb)
{
    // Preserve peak headroom rather than clipping dynamic recordings to reach the target.
    public double GainDb => Math.Min(Math.Clamp(-16 - IntegratedLufs, -30, 12), -1.5 - TruePeakDb);
    public bool IsValid => double.IsFinite(IntegratedLufs) && IntegratedLufs is >= -99 and <= 0
        && double.IsFinite(TruePeakDb) && TruePeakDb is >= -99 and <= 20;
}

/// <summary>Small, versioned measurements only; never writes or re-encodes audio.</summary>
public sealed class LoudnessAnalysis(Transcoder transcoder, IOptionsMonitor<TranscodeOptions> options)
{
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    public string DirectoryPath => Path.Combine(Path.GetFullPath(string.IsNullOrWhiteSpace(options.CurrentValue.CacheDirectory)
        ? Path.Combine("data", "transcode-cache") : options.CurrentValue.CacheDirectory), "loudness");
    public sealed record CachedMeasurement(string Source, long Size, long ModifiedTicks, LoudnessMeasurement Measurement);
    public string CachePath(Guid id) => Path.Combine(DirectoryPath, $"{id:n}-v1.json");

    public LoudnessMeasurement? FindReady(Guid id, string source)
    {
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists) return null;
            var cached = JsonSerializer.Deserialize<CachedMeasurement>(File.ReadAllText(CachePath(id)));
            return cached is not null && cached.Source == Path.GetFullPath(source)
                && cached.Size == info.Length && cached.ModifiedTicks == info.LastWriteTimeUtc.Ticks
                && cached.Measurement is { IsValid: true } ? cached.Measurement : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public async Task<LoudnessMeasurement?> GetOrCreateAsync(Guid id, string source, CancellationToken ct = default)
    {
        if (FindReady(id, source) is { } ready) return ready;
        var gate = _gates[(uint)id.GetHashCode() % (uint)_gates.Length];
        await gate.WaitAsync(ct);
        try
        {
            if (FindReady(id, source) is { } existing) return existing;
            var info = new FileInfo(source);
            if (!info.Exists) return null;
            var size = info.Length;
            var modified = info.LastWriteTimeUtc.Ticks;
            var result = await transcoder.MeasureLoudnessAsync(source, ct);
            if (result is null) return null;
            info.Refresh();
            if (!info.Exists || info.Length != size || info.LastWriteTimeUtc.Ticks != modified) return null;
            Directory.CreateDirectory(DirectoryPath);
            var path = CachePath(id);
            var temp = path + ".partial";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new CachedMeasurement(
                    Path.GetFullPath(source), size, modified, result)), ct);
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return result;
        }
        finally { gate.Release(); }
    }

    internal static LoudnessMeasurement? Parse(string output)
    {
        var start = output.LastIndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var json = JsonDocument.Parse(output[start..(end + 1)]);
            var root = json.RootElement;
            if (!root.TryGetProperty("input_i", out var loudness) || !root.TryGetProperty("input_tp", out var peak)
                || !double.TryParse(loudness.GetString(), CultureInfo.InvariantCulture, out var lufs)
                || !double.TryParse(peak.GetString(), CultureInfo.InvariantCulture, out var db)) return null;
            var result = new LoudnessMeasurement(lufs, db);
            return result.IsValid ? result : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }
}
