using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class BlurHashEncoderTests
{
    [Fact]
    public void FourByThreeComponentsMatchPublicReferenceEncoder()
    {
        // Vector from https://github.com/woltapp/blurhash/blob/master/C/encode.c.
        byte[] pixels =
        [
            255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255,
            0, 0, 0, 255, 255, 0, 0, 255, 255, 255, 0, 255,
            32, 64, 96, 128, 160, 192, 224, 128, 32, 16, 48, 80
        ];

        Assert.Equal("L?J*etx+q@:v~qs}+Y;Cx[rX-N=y", BlurHashEncoder.EncodeRgb(pixels, 4, 3));
    }

    [Fact]
    public void BlackMatchesPublicReferenceEncoder()
    {
        Assert.Equal("L00000fQfQfQfQfQfQfQfQfQfQfQ", BlurHashEncoder.EncodeRgb(new byte[36], 4, 3));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(4, 0)]
    [InlineData(3, 3)]
    public void RejectsMismatchedPixelDimensions(int width, int height)
    {
        Assert.Throws<ArgumentException>(() => BlurHashEncoder.EncodeRgb(new byte[36], width, height));
    }

    [Fact]
    public void InvalidEncodedImageHasNoHash()
    {
        Assert.Null(BlurHashEncoder.TryEncode([1, 2, 3]));
    }
}
