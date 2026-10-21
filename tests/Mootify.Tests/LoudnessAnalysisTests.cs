using Mootify.Services.Transcoding;

namespace Mootify.Tests;

public sealed class LoudnessAnalysisTests
{
    [Theory]
    [InlineData(-20, -8, 4)]
    [InlineData(-10, -1, -6)]
    [InlineData(-30, -2, 0.5)]
    [InlineData(-60, -40, 12)]
    public void Gain_matches_target_with_peak_headroom_and_bounded_boost(double lufs, double peak, double gain)
        => Assert.Equal(gain, new LoudnessMeasurement(lufs, peak).GainDb);

    [Fact]
    public void Parses_ffmpeg_input_measurements_not_normalized_output()
    {
        var result = LoudnessAnalysis.Parse("diagnostics\n{\"input_i\":\"-21.25\",\"input_tp\":\"-8.30\",\"output_i\":\"-16.00\"}\n");
        Assert.Equal(new LoudnessMeasurement(-21.25, -8.3), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"input_i\":\"NaN\",\"input_tp\":\"-1\"}")]
    [InlineData("{\"input_i\":\"-inf\",\"input_tp\":\"-inf\"}")]
    [InlineData("{\"input_i\":10,\"input_tp\":null}")]
    public void Rejects_invalid_or_nonfinite_measurements(string output)
        => Assert.Null(LoudnessAnalysis.Parse(output));
}
